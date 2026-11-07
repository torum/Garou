namespace Garou.Domain.Models;

public sealed class LibraryDefinition
{
    public required string Id { get; init; }

    public required string RootPath { get; init; }

    public bool IsOnline { get; set; } = true;

    public bool IsEnabled { get; set; } = true;
}
