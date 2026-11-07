namespace Garou.Domain.Entities;

public sealed class Folder
{
    public long Id { get; init; }

    public required string Path { get; init; }

    public long? ParentId { get; init; }
}
