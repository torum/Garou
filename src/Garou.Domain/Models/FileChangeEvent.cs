namespace Garou.Domain.Models;

public enum FileChangeKind
{
    Created = 0,
    Modified = 1,
    Deleted = 2,
    Renamed = 3
}

public sealed class FileChangeEvent
{
    public required string LibraryRoot { get; init; }

    public required string FullPath { get; init; }

    public string? OldFullPath { get; init; }

    public FileChangeKind Kind { get; init; }

    public bool RequiresFullRescan { get; init; }
}
