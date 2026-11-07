using Garou.Domain.Entities;

namespace Garou.Application.Queries;

public interface IMediaSearchRepository
{
    Task<IReadOnlyList<MediaFile>> SearchAsync(BuiltQuery query, CancellationToken cancellationToken);
    Task<long> CountAsync(BuiltQuery query, CancellationToken cancellationToken);
}
