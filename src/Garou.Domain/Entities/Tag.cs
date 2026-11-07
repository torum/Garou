namespace Garou.Domain.Entities;

public sealed class Tag
{
    public long Id { get; init; }

    public required string Name { get; init; }
}
