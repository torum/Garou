using Garou.Domain.Entities;

namespace Garou.Domain.Abstractions;

public interface IFolderRepository
{
    Task<Folder?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<Folder?> GetByPathAsync(string path, CancellationToken cancellationToken);

    Task<IReadOnlyList<Folder>> GetAllAsync(CancellationToken cancellationToken);

    Task<Guid> AddAsync(Folder folder, CancellationToken cancellationToken);

    Task DeleteSubtreeAsync(Guid folderId, CancellationToken cancellationToken);
}
