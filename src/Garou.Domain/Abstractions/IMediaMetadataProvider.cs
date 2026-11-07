using Garou.Domain.Models;

namespace Garou.Domain.Abstractions;

public interface IMediaMetadataProvider
{
    bool Supports(string extension);

    Task<MediaMetadata> ReadAsync(string filePath, CancellationToken cancellationToken);
}
