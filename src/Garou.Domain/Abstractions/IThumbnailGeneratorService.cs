using Garou.Domain.Entities;

namespace Garou.Domain.Abstractions;

public interface IThumbnailGeneratorService
{
    Task<string?> GenerateAsync(MediaFile mediaFile, CancellationToken cancellationToken);

    Task ClearCacheAsync(MediaFile mediaFile, CancellationToken cancellationToken);

    Task<string?> GenerateAsync(MediaFile mediaFile, int thumbnailWidth, CancellationToken cancellationToken) =>
        GenerateAsync(mediaFile, cancellationToken);
}
