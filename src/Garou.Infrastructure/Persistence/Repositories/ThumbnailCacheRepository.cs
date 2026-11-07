using Garou.Domain.Abstractions;
using Garou.Domain.Entities;
using Microsoft.Data.Sqlite;

namespace Garou.Infrastructure.Persistence.Repositories;

public sealed class ThumbnailCacheRepository(SqliteConnectionFactory connectionFactory) : IThumbnailCacheRepository
{
    private const string SelectColumns = "SELECT ThumbnailPath, SizeBytes, LastAccessUtc FROM ThumbnailCacheEntry";

    public async Task<ThumbnailCacheEntry?> GetAsync(string thumbnailPath, CancellationToken cancellationToken) =>
        await connectionFactory.QuerySingleAsync(
            $"{SelectColumns} WHERE ThumbnailPath = $thumbnailPath;",
            Map,
            cancellationToken,
            ("$thumbnailPath", thumbnailPath));

    public async Task UpsertAsync(ThumbnailCacheEntry entry, CancellationToken cancellationToken)
    {
        await connectionFactory.ExecuteNonQueryAsync(
            """
            INSERT INTO ThumbnailCacheEntry (ThumbnailPath, SizeBytes, LastAccessUtc)
            VALUES ($thumbnailPath, $sizeBytes, $lastAccessUtc)
            ON CONFLICT(ThumbnailPath) DO UPDATE SET
                SizeBytes = excluded.SizeBytes,
                LastAccessUtc = excluded.LastAccessUtc;
            """,
            cancellationToken,
            ("$thumbnailPath", entry.ThumbnailPath),
            ("$sizeBytes", entry.SizeBytes),
            ("$lastAccessUtc", entry.LastAccessUtc.ToDbDate()));
    }

    public async Task<long> GetTotalSizeBytesAsync(CancellationToken cancellationToken)
    {
        var result = await connectionFactory.ExecuteScalarAsync(
            "SELECT COALESCE(SUM(SizeBytes), 0) FROM ThumbnailCacheEntry;",
            cancellationToken);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<ThumbnailCacheEntry>> GetLeastRecentlyAccessedAsync(int count, CancellationToken cancellationToken) =>
        await connectionFactory.QueryAsync(
            $"{SelectColumns} ORDER BY LastAccessUtc LIMIT $count;",
            Map,
            cancellationToken,
            ("$count", count));

    public async Task DeleteAsync(string thumbnailPath, CancellationToken cancellationToken)
    {
        await connectionFactory.ExecuteNonQueryAsync(
            "DELETE FROM ThumbnailCacheEntry WHERE ThumbnailPath = $thumbnailPath;",
            cancellationToken,
            ("$thumbnailPath", thumbnailPath));
    }

    private static ThumbnailCacheEntry Map(SqliteDataReader reader) => new()
    {
        ThumbnailPath = reader.GetString(0),
        SizeBytes = reader.GetInt64(1),
        LastAccessUtc = reader.ReadDate(2)!.Value
    };
}
