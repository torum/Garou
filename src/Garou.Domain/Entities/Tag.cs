namespace Garou.Domain.Entities;

public sealed class Tag
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required string Name { get; init; }
}
