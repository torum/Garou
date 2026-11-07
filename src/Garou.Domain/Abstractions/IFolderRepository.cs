using Garou.Domain.Entities;

namespace Garou.Domain.Abstractions;

public interface IFolderRepository
{
    Task<Folder?> GetByIdAsync(long id, CancellationToken cancellationToken);

    Task<Folder?> GetByPathAsync(string path, CancellationToken cancellationToken);

    Task<IReadOnlyList<Folder>> GetAllAsync(CancellationToken cancellationToken);

    Task<long> AddAsync(Folder folder, CancellationToken cancellationToken);

    Task DeleteSubtreeAsync(long folderId, CancellationToken cancellationToken);
}
