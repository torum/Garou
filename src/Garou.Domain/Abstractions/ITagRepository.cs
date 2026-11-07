using Garou.Domain.Entities;

namespace Garou.Domain.Abstractions;

public interface ITagRepository
{
    Task<Tag?> GetByNameAsync(string name, CancellationToken cancellationToken);

    Task<long> AddAsync(Tag tag, CancellationToken cancellationToken);

    Task AssignAsync(MediaFileTag mediaFileTag, CancellationToken cancellationToken);

    Task RemoveAssignmentAsync(MediaFileTag mediaFileTag, CancellationToken cancellationToken);
}
