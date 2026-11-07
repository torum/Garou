using Garou.Domain.Models;

namespace Garou.Domain.Abstractions;

public interface ILibraryRepository
{
    Task<IReadOnlyList<LibraryDefinition>> GetAllAsync(CancellationToken cancellationToken);

    Task AddAsync(LibraryDefinition library, CancellationToken cancellationToken);

    Task RemoveAsync(string id, CancellationToken cancellationToken);

    Task SetEnabledAsync(string id, bool isEnabled, CancellationToken cancellationToken);
}
