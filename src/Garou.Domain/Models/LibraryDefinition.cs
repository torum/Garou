namespace Garou.Domain.Models;

public sealed class LibraryDefinition
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required string RootPath { get; init; }

    public bool IsOnline { get; set; } = true;

    public bool IsEnabled { get; set; } = true;
}
