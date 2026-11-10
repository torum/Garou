namespace Garou.Domain.Abstractions;

public interface ILibraryExcludedFolderRepository
{
    Task<IReadOnlyList<string>> GetPathsAsync(Guid libraryId, CancellationToken cancellationToken);

    Task AddAsync(Guid libraryId, string path, CancellationToken cancellationToken);
}
