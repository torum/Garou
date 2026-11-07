using Garou.Domain.Abstractions;
using Garou.Domain.Models;

namespace Garou.Infrastructure.Metadata;

public sealed class MetadataExtractionService(IEnumerable<IMediaMetadataProvider> providers) : IMetadataExtractionService
{
    private readonly IMediaMetadataProvider[] _providers = providers.ToArray();

    public Task<MediaMetadata> ExtractAsync(string filePath, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(filePath);
        var provider = _providers.FirstOrDefault(p => p.Supports(extension));

        return provider is null
            ? Task.FromResult(new MediaMetadata())
            : provider.ReadAsync(filePath, cancellationToken);
    }
}
