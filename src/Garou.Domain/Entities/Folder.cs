namespace Garou.Domain.Entities;

public sealed class Folder
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required string Path { get; init; }

    public Guid? ParentId { get; init; }
}
