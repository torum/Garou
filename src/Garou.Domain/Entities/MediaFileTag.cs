namespace Garou.Domain.Entities;

public sealed class MediaFileTag
{
    public Guid MediaFileId { get; init; }

    public Guid TagId { get; init; }
}
