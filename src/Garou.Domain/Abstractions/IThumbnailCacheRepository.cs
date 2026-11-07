using Garou.Domain.Entities;

namespace Garou.Domain.Abstractions;

public interface IThumbnailCacheRepository
{
    Task<ThumbnailCacheEntry?> GetAsync(string thumbnailPath, CancellationToken cancellationToken);

    Task UpsertAsync(ThumbnailCacheEntry entry, CancellationToken cancellationToken);

    Task<long> GetTotalSizeBytesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ThumbnailCacheEntry>> GetLeastRecentlyAccessedAsync(int count, CancellationToken cancellationToken);

    Task DeleteAsync(string thumbnailPath, CancellationToken cancellationToken);
}
