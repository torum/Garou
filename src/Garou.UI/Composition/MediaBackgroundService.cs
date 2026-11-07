using Garou.Infrastructure.Sync;
using Garou.Infrastructure.Thumbnails;
using Microsoft.Extensions.Hosting;

namespace Garou.Composition;

public sealed class MediaBackgroundService(
    MediaIndexerService indexer,
    ThumbnailEvictionScheduler evictionScheduler) : BackgroundService
{
    private static readonly TimeSpan EvictionInterval = TimeSpan.FromMinutes(10);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var indexing = indexer.RunAsync(stoppingToken);
        var eviction = evictionScheduler.RunAsync(EvictionInterval, stoppingToken);

        return Task.WhenAll(indexing, eviction);
    }
}
