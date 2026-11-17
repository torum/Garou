using System.Text.Encodings.Web;
using System.Text.Json;
using Garou.Domain.Models;
using Garou.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Garou.Infrastructure.Metadata;

public sealed class XnfoIndexService(XnfoStore store, SqliteConnectionFactory connectionFactory)
{
    private const int ScanBatchSize = 200;
    private static readonly XnfoJsonContext JsonContext = new(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    private string? _lastError;

    public event Action<string>? IndexingError;
    public event Action? MetadataChanged;
    public string? LastError => Volatile.Read(ref _lastError);

    public async Task SyncObservedAsync(string mediaPath, bool notifyChange, CancellationToken cancellationToken)
    {
        try
        {
            await SyncAsync(mediaPath, cancellationToken).ConfigureAwait(false);
            if (notifyChange)
                MetadataChanged?.Invoke();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidOperationException or NotSupportedException)
        {
            ReportIndexingError(mediaPath, exception);
        }
    }

    private void ReportIndexingError(string mediaPath, Exception exception)
    {
        var message = $"Could not index metadata for '{mediaPath}': {exception.Message}";
        Volatile.Write(ref _lastError, message);
        System.Diagnostics.Debug.WriteLine(message);
        IndexingError?.Invoke(message);
    }

    internal async Task SyncScanAsync(string rootPath, IReadOnlySet<string> inspectableFiles,
        IReadOnlyList<(string MediaPath, string SidecarPath)> sidecars, IReadOnlySet<string> uninspectableSidecars,
        IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        if (inspectableFiles.Count == 0)
            return;

        var processed = 0;
        var lastReported = System.Diagnostics.Stopwatch.GetTimestamp();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var observed = sidecars.Select(sidecar => sidecar.MediaPath)
            .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        observed.UnionWith(uninspectableSidecars);
        var missing = new List<string>();
        var prefix = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT MediaFile.Path FROM XnfoMetadata
                INNER JOIN MediaFile ON MediaFile.Id = XnfoMetadata.MediaFileId
                WHERE substr(MediaFile.Path, 1, length($prefix)) = $prefix;
                """;
            command.AddValue("$prefix", prefix);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var path = reader.GetString(0);
                if (inspectableFiles.Contains(path) && !observed.Contains(path))
                    missing.Add(path);
            }
        }

        foreach (var paths in missing.Chunk(ScanBatchSize))
        {
            await using var transaction = connection.BeginTransaction();
            foreach (var path in paths)
                await SyncMetadataAsync(connection, transaction, path, null, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var paths in sidecars.Chunk(ScanBatchSize))
        {
            var loaded = new List<(string Path, XnfoMetadata? Metadata)>(paths.Length);
            foreach (var (mediaPath, sidecarPath) in paths)
            {
                try
                {
                    loaded.Add((mediaPath, await store.LoadFromPathAsync(sidecarPath, discovered: true, cancellationToken).ConfigureAwait(false)));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidOperationException or NotSupportedException)
                {
                    ReportIndexingError(mediaPath, exception);
                }
            }

            await using var transaction = connection.BeginTransaction();
            foreach (var (path, metadata) in loaded)
            {
                try
                {
                    await SyncMetadataAsync(connection, transaction, path, metadata, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidOperationException or NotSupportedException)
                {
                    ReportIndexingError(path, exception);
                }
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            processed += paths.Length;
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (processed == sidecars.Count || System.Diagnostics.Stopwatch.GetElapsedTime(lastReported, now) >= TimeSpan.FromMilliseconds(200))
            {
                lastReported = now;
                progress?.Report(new ScanProgress("ScanIndexingMetadata", processed, sidecars.Count));
            }
        }
    }

    public Task<XnfoMetadata?> LoadAsync(string mediaPath, CancellationToken cancellationToken) =>
        store.LoadAsync(mediaPath, cancellationToken);

    public async Task SaveAsync(string mediaPath, XnfoMetadata metadata, CancellationToken cancellationToken)
    {
        await store.SaveAsync(mediaPath, metadata, cancellationToken).ConfigureAwait(false);
        try
        {
            await SyncAsync(mediaPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException("The companion file was saved, but its database index could not be updated. Rescan the library to retry.", exception);
        }
    }

    public async Task SyncAsync(string mediaPath, CancellationToken cancellationToken)
    {
        var metadata = await store.LoadAsync(mediaPath, cancellationToken).ConfigureAwait(false);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await SyncMetadataAsync(connection, null, mediaPath, metadata, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SyncMetadataAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string mediaPath, XnfoMetadata? metadata, CancellationToken cancellationToken)
    {
        if (metadata is null)
        {
            await ExecuteAsync(connection, transaction,
                "DELETE FROM XnfoMetadata WHERE MediaFileId = (SELECT Id FROM MediaFile WHERE Path = $path);",
                cancellationToken, ("$path", mediaPath)).ConfigureAwait(false);
            return;
        }

        var rows = await ExecuteAsync(connection, transaction,
            """
            INSERT INTO XnfoMetadata (MediaFileId, Title, Subtitle, SortTitle, Publisher, StudioLabel, ReleaseDate,
                Performers, Countries, Tags, Genres, Categories, ContentRating, Description, Comments, CoverArt)
            SELECT Id, $title, $subtitle, $sortTitle, $publisher, $studioLabel, $releaseDate,
                $performers, $countries, $tags, $genres, $categories, $contentRating, $description, $comments, $coverArt
            FROM MediaFile WHERE Path = $path
            ON CONFLICT(MediaFileId) DO UPDATE SET
                Title = excluded.Title, Subtitle = excluded.Subtitle, SortTitle = excluded.SortTitle,
                Publisher = excluded.Publisher, StudioLabel = excluded.StudioLabel, ReleaseDate = excluded.ReleaseDate,
                Performers = excluded.Performers, Countries = excluded.Countries, Tags = excluded.Tags,
                Genres = excluded.Genres, Categories = excluded.Categories, ContentRating = excluded.ContentRating,
                Description = excluded.Description, Comments = excluded.Comments, CoverArt = excluded.CoverArt
            WHERE (Title, Subtitle, SortTitle, Publisher, StudioLabel, ReleaseDate, Performers, Countries, Tags,
                   Genres, Categories, ContentRating, Description, Comments, CoverArt)
               IS NOT (excluded.Title, excluded.Subtitle, excluded.SortTitle, excluded.Publisher, excluded.StudioLabel,
                   excluded.ReleaseDate, excluded.Performers, excluded.Countries, excluded.Tags, excluded.Genres,
                   excluded.Categories, excluded.ContentRating, excluded.Description, excluded.Comments, excluded.CoverArt);
            """, cancellationToken,
            ("$path", mediaPath), ("$title", metadata.Title), ("$subtitle", metadata.Subtitle),
            ("$sortTitle", metadata.SortTitle), ("$publisher", metadata.Publisher), ("$studioLabel", metadata.StudioLabel),
            ("$releaseDate", metadata.ReleaseDate), ("$performers", JsonSerializer.Serialize(metadata.Performers, JsonContext.ListString)),
            ("$countries", JsonSerializer.Serialize(metadata.Countries, JsonContext.ListString)), ("$tags", JsonSerializer.Serialize(metadata.Tags, JsonContext.ListString)),
            ("$genres", JsonSerializer.Serialize(metadata.Genres, JsonContext.ListString)), ("$categories", JsonSerializer.Serialize(metadata.Categories, JsonContext.ListString)),
            ("$contentRating", metadata.ContentRating), ("$description", metadata.Description),
            ("$comments", metadata.Comments), ("$coverArt", metadata.CoverArt)).ConfigureAwait(false);
        if (rows == 0)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT 1 FROM MediaFile WHERE Path = $path;";
            command.AddValue("$path", mediaPath);
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
                throw new InvalidOperationException($"The media file '{mediaPath}' is not indexed.");
        }
    }

    private static async Task<int> ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.AddValue(name, value);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
