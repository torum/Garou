using Garou.Domain.Models;

namespace Garou.Infrastructure.Sync;

internal sealed class ApplicationStorageFilter(string thumbnailDirectory, string databasePath, string settingsPath)
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static ApplicationStorageFilter Default { get; } = new(
        AppStoragePaths.ThumbnailDirectory, AppStoragePaths.DatabaseFilePath, AppStoragePaths.SettingsFilePath);

    public bool IsIgnored(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var relativeToThumbnails = Path.GetRelativePath(thumbnailDirectory, fullPath);
        if (!Path.IsPathRooted(relativeToThumbnails) && relativeToThumbnails != ".."
            && !relativeToThumbnails.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(fullPath, databasePath, PathComparison)
            || string.Equals(fullPath, databasePath + "-wal", PathComparison)
            || string.Equals(fullPath, databasePath + "-shm", PathComparison)
            || string.Equals(fullPath, databasePath + "-journal", PathComparison)
            || string.Equals(fullPath, settingsPath, PathComparison);
    }

    public FileChangeEvent? Filter(FileChangeEvent change)
    {
        if (change.RequiresFullRescan)
            return change;

        var ignored = IsIgnored(change.FullPath);
        if (change.Kind != FileChangeKind.Renamed || change.OldFullPath is null)
            return ignored ? null : change;

        var oldIgnored = IsIgnored(change.OldFullPath);
        if (ignored == oldIgnored)
            return ignored ? null : change;

        return new FileChangeEvent
        {
            LibraryRoot = change.LibraryRoot,
            FullPath = ignored ? change.OldFullPath : change.FullPath,
            Kind = ignored ? FileChangeKind.Deleted : FileChangeKind.Created
        };
    }
}
