using Garou.Domain.Abstractions;
using Garou.Domain.Entities;

namespace Garou.Infrastructure.Persistence;

public sealed class BatchWriter(IMediaFileRepository mediaFileRepository)
{
    public const int DefaultBatchSize = 500;

    public async Task UpsertMediaFilesAsync(
        IReadOnlyList<MediaFile> mediaFiles,
        CancellationToken cancellationToken,
        int batchSize = DefaultBatchSize)
    {
        for (var offset = 0; offset < mediaFiles.Count; offset += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var count = Math.Min(batchSize, mediaFiles.Count - offset);
            await mediaFileRepository.UpsertBatchAsync(mediaFiles.Skip(offset).Take(count).ToList(), cancellationToken);
        }
    }
}
