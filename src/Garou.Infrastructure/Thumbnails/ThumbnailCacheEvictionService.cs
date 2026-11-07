using Garou.Domain.Abstractions;
using Garou.Domain.Entities;

namespace Garou.Infrastructure.Thumbnails;

public sealed class ThumbnailCacheEvictionService(IThumbnailCacheRepository cacheRepository)
{
    public const long MaxLimitBytes = 1024L * 1024 * 1024 - 1;
    private const int BatchSize = 100;

    public async Task EvictAsync(long limitBytes, CancellationToken cancellationToken)
    {
        var limit = Math.Min(limitBytes, MaxLimitBytes);
        var total = await cacheRepository.GetTotalSizeBytesAsync(cancellationToken).ConfigureAwait(false);

        while (total > limit)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidates = await cacheRepository.GetLeastRecentlyAccessedAsync(BatchSize, cancellationToken).ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                return;
            }

            foreach (var entry in candidates)
            {
                if (total <= limit)
                {
                    return;
                }

                DeleteFileIfExists(entry);
                await cacheRepository.DeleteAsync(entry.ThumbnailPath, cancellationToken).ConfigureAwait(false);
                total -= entry.SizeBytes;
            }
        }
    }

    private static void DeleteFileIfExists(ThumbnailCacheEntry entry)
    {
        if (File.Exists(entry.ThumbnailPath))
        {
            File.Delete(entry.ThumbnailPath);
        }
    }
}
