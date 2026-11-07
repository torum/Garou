using Garou.Infrastructure.Settings;

namespace Garou.Infrastructure.Thumbnails;

public sealed class ThumbnailEvictionScheduler(
    ThumbnailCacheEvictionService evictionService,
    AppSettingsSession settingsSession,
    TimeProvider timeProvider)
{
    public async Task RunAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval, timeProvider);

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await EvictOnceAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task EvictOnceAsync(CancellationToken cancellationToken)
    {
        await evictionService.EvictAsync(settingsSession.Current.ThumbnailCacheLimitBytes, cancellationToken).ConfigureAwait(false);
    }
}
