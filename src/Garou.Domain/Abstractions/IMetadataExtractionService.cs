using Garou.Domain.Models;

namespace Garou.Domain.Abstractions;

public interface IMetadataExtractionService
{
    Task<MediaMetadata> ExtractAsync(string filePath, CancellationToken cancellationToken);
}
