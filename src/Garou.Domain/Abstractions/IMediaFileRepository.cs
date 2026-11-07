using Garou.Domain.Entities;

namespace Garou.Domain.Abstractions;

public interface IMediaFileRepository
{
    Task<MediaFile?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<MediaFile?> GetByPathAsync(string path, CancellationToken cancellationToken);

    Task<IReadOnlyList<MediaFile>> GetPageAsync(
        Guid? folderId,
        Guid afterId,
        int pageSize,
        CancellationToken cancellationToken);

    Task UpsertBatchAsync(IReadOnlyList<MediaFile> mediaFiles, CancellationToken cancellationToken);

    Task UpdateThumbnailPathAsync(Guid mediaFileId, string? thumbnailPath, CancellationToken cancellationToken);

    Task DeleteByPathAsync(string path, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetPathsUnderRootAsync(string rootPath, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, MediaFileSignature>> GetSignaturesUnderRootAsync(string rootPath, CancellationToken cancellationToken);
}

public sealed record MediaFileSignature(long FileSize, DateTime? ModifiedDateUtc, string? ThumbnailPath);
