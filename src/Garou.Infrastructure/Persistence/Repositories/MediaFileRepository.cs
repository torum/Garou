using System.Globalization;
using Garou.Application.Queries;
using Garou.Domain.Abstractions;
using Garou.Domain.Entities;
using Garou.Domain.Enums;
using Microsoft.Data.Sqlite;

namespace Garou.Infrastructure.Persistence.Repositories;

public sealed class MediaFileRepository(SqliteConnectionFactory connectionFactory) : IMediaFileRepository, IMediaSearchRepository
{
    private const string SelectColumns =
        """
        Id, Path, FileName, Extension, MediaType, FileSize, HashValue, ThumbnailPath,
        CreatedDateUtc, ModifiedDateUtc, LastIndexedUtc, FolderId
        """;

    public async Task<MediaFile?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        await connectionFactory.QuerySingleAsync($"SELECT {SelectColumns} FROM MediaFile WHERE Id = $value;", Map, cancellationToken, ("$value", id.ToString("N")));

    public async Task<MediaFile?> GetByPathAsync(string path, CancellationToken cancellationToken) =>
        await connectionFactory.QuerySingleAsync($"SELECT {SelectColumns} FROM MediaFile WHERE Path = $value;", Map, cancellationToken, ("$value", path));

    public async Task<IReadOnlyList<MediaFile>> GetPageAsync(
        Guid? folderId,
        Guid afterId,
        int pageSize,
        CancellationToken cancellationToken)
    {
        return await connectionFactory.QueryAsync(
            $"""
            SELECT {SelectColumns}
            FROM MediaFile
            WHERE Id > $afterId
              AND ($folderId IS NULL OR FolderId = $folderId)
            ORDER BY Id
            LIMIT $pageSize;
            """,
            Map,
            cancellationToken,
            ("$afterId", afterId.ToString("N")),
            ("$folderId", folderId?.ToString("N")),
            ("$pageSize", pageSize));
    }

    public async Task<IReadOnlyList<MediaFile>> SearchAsync(BuiltQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return await connectionFactory.QueryAsync(
            query.Sql,
            Map,
            cancellationToken,
            query.Parameters.Select(p => (p.Key, p.Value)).ToArray());
    }

    public async Task<long> CountAsync(BuiltQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var count = await connectionFactory.ExecuteScalarAsync(
            query.Sql,
            cancellationToken,
            query.Parameters.Select(p => (p.Key, p.Value)).ToArray());

        return Convert.ToInt64(count, CultureInfo.InvariantCulture);
    }

    public async Task UpsertBatchAsync(IReadOnlyList<MediaFile> mediaFiles, CancellationToken cancellationToken)
    {
        if (mediaFiles.Count == 0)
        {
            return;
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO MediaFile (Id, Path, FileName, Extension, MediaType, FileSize, HashValue,
                                   ThumbnailPath, CreatedDateUtc, ModifiedDateUtc, LastIndexedUtc, FolderId)
            VALUES ($id, $path, $fileName, $extension, $mediaType, $fileSize, $hashValue,
                    $thumbnailPath, $createdDateUtc, $modifiedDateUtc, $lastIndexedUtc, $folderId)
            ON CONFLICT(Path) DO UPDATE SET
                FileName = excluded.FileName,
                Extension = excluded.Extension,
                MediaType = excluded.MediaType,
                FileSize = excluded.FileSize,
                HashValue = excluded.HashValue,
                ThumbnailPath = excluded.ThumbnailPath,
                CreatedDateUtc = excluded.CreatedDateUtc,
                ModifiedDateUtc = excluded.ModifiedDateUtc,
                LastIndexedUtc = excluded.LastIndexedUtc,
                FolderId = excluded.FolderId;
            """;

        var id = command.AddValue("$id", null);
        var path = command.AddValue("$path", null);
        var fileName = command.AddValue("$fileName", null);
        var extension = command.AddValue("$extension", null);
        var mediaType = command.AddValue("$mediaType", null);
        var fileSize = command.AddValue("$fileSize", null);
        var hashValue = command.AddValue("$hashValue", null);
        var thumbnailPath = command.AddValue("$thumbnailPath", null);
        var createdDateUtc = command.AddValue("$createdDateUtc", null);
        var modifiedDateUtc = command.AddValue("$modifiedDateUtc", null);
        var lastIndexedUtc = command.AddValue("$lastIndexedUtc", null);
        var folderId = command.AddValue("$folderId", null);
        await command.PrepareAsync(cancellationToken);

        foreach (var file in mediaFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            id.Value = (file.Id == Guid.Empty ? Guid.CreateVersion7() : file.Id).ToString("N");
            path.Value = file.Path;
            fileName.Value = file.FileName;
            extension.Value = file.Extension;
            mediaType.Value = (int)file.MediaType;
            fileSize.Value = file.FileSize ?? (object)DBNull.Value;
            hashValue.Value = file.HashValue ?? (object)DBNull.Value;
            thumbnailPath.Value = file.ThumbnailPath ?? (object)DBNull.Value;
            createdDateUtc.Value = file.CreatedDateUtc.ToDbDate() ?? (object)DBNull.Value;
            modifiedDateUtc.Value = file.ModifiedDateUtc.ToDbDate() ?? (object)DBNull.Value;
            lastIndexedUtc.Value = file.LastIndexedUtc.ToDbDate() ?? (object)DBNull.Value;
            folderId.Value = file.FolderId?.ToString("N") ?? (object)DBNull.Value;

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpdateThumbnailPathAsync(Guid mediaFileId, string? thumbnailPath, CancellationToken cancellationToken) =>
        await connectionFactory.ExecuteNonQueryAsync(
            "UPDATE MediaFile SET ThumbnailPath = $thumbnailPath WHERE Id = $id;",
            cancellationToken,
            ("$thumbnailPath", thumbnailPath),
            ("$id", mediaFileId.ToString("N")));

    public async Task DeleteByPathAsync(string path, CancellationToken cancellationToken) =>
        await connectionFactory.ExecuteNonQueryAsync("DELETE FROM MediaFile WHERE Path = $path;", cancellationToken, ("$path", path));

    public async Task<IReadOnlyList<string>> GetPathsUnderRootAsync(string rootPath, CancellationToken cancellationToken)
    {
        var prefix = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return await connectionFactory.QueryAsync(
            "SELECT Path FROM MediaFile WHERE substr(Path, 1, length($prefix)) = $prefix;",
            reader => reader.GetString(0),
            cancellationToken,
            ("$prefix", prefix));
    }

    public async Task<IReadOnlyDictionary<string, MediaFileSignature>> GetSignaturesUnderRootAsync(string rootPath, CancellationToken cancellationToken)
    {
        var prefix = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        var rows = await connectionFactory.QueryAsync(
            "SELECT Path, FileSize, ModifiedDateUtc, ThumbnailPath FROM MediaFile WHERE substr(Path, 1, length($prefix)) = $prefix;",
            reader => (Path: reader.GetString(0), Signature: new MediaFileSignature(
                reader.ReadNullable<long>(1) ?? -1,
                reader.ReadDate(2),
                reader.ReadNullableString(3))),
            cancellationToken,
            ("$prefix", prefix));

        var signatures = new Dictionary<string, MediaFileSignature>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            signatures[row.Path] = row.Signature;
        }

        return signatures;
    }

    private static MediaFile Map(SqliteDataReader reader)
    {
        return new MediaFile
        {
            Id = Guid.Parse(reader.GetString(0)),
            Path = reader.GetString(1),
            FileName = reader.GetString(2),
            Extension = reader.GetString(3),
            MediaType = (MediaType)reader.GetInt32(4),
            FileSize = reader.ReadNullable<long>(5),
            HashValue = reader.ReadNullableString(6),
            ThumbnailPath = reader.ReadNullableString(7),
            CreatedDateUtc = reader.ReadDate(8),
            ModifiedDateUtc = reader.ReadDate(9),
            LastIndexedUtc = reader.ReadDate(10),
            FolderId = reader.IsDBNull(11) ? null : Guid.Parse(reader.GetString(11))
        };
    }
}
