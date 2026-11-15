using System.Text.Encodings.Web;
using System.Text.Json;
using Garou.Domain.Models;
using Garou.Infrastructure.Persistence;

namespace Garou.Infrastructure.Metadata;

public sealed class XnfoIndexService(XnfoStore store, SqliteConnectionFactory connectionFactory)
{
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
            var message = $"Could not index metadata for '{mediaPath}': {exception.Message}";
            Volatile.Write(ref _lastError, message);
            System.Diagnostics.Debug.WriteLine(message);
            IndexingError?.Invoke(message);
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
        if (metadata is null)
        {
            await connectionFactory.ExecuteNonQueryAsync(
                "DELETE FROM XnfoMetadata WHERE MediaFileId = (SELECT Id FROM MediaFile WHERE Path = $path);",
                cancellationToken, ("$path", mediaPath)).ConfigureAwait(false);
            return;
        }

        var rows = await connectionFactory.ExecuteNonQueryAsync(
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
                Description = excluded.Description, Comments = excluded.Comments, CoverArt = excluded.CoverArt;
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
            throw new InvalidOperationException($"The media file '{mediaPath}' is not indexed.");
        }
    }
}
