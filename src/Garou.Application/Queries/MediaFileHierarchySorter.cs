using Garou.Domain.Entities;

namespace Garou.Application.Queries;

public static class MediaFileHierarchySorter
{
    public static IEnumerable<MediaFile> Sort(IEnumerable<MediaFile> files, bool sortByFolder, CancellationToken cancellationToken = default)
    {
        var checkedFiles = CancellableSort.CheckItems(files, cancellationToken);
        return sortByFolder
            ? SortByFolder(checkedFiles, cancellationToken)
            : checkedFiles.OrderBy(file => file.FileName, CancellableSort.Comparer(LogicalFileNameComparer.Instance, cancellationToken));
    }

    private static IEnumerable<MediaFile> SortByFolder(IEnumerable<MediaFile> files, CancellationToken cancellationToken)
    {
        var folderParts = new Dictionary<string, string[]>(StringComparer.Ordinal);
        return files.Select(file =>
            {
                var directory = Path.GetDirectoryName(file.Path) ?? string.Empty;
                if (!folderParts.TryGetValue(directory, out var parts))
                {
                    parts = directory.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
                    folderParts.Add(directory, parts);
                }
                return new { File = file, FolderParts = parts };
            })
            .OrderBy(item => item.FolderParts, CancellableSort.Comparer(FolderPartsComparer.Instance, cancellationToken))
            .ThenBy(item => item.File.FileName, LogicalFileNameComparer.Instance)
            .ThenBy(item => item.File.FileName, StringComparer.Ordinal)
            .ThenBy(item => item.File.Id)
            .Select(item => item.File);
    }

    private sealed class FolderPartsComparer : IComparer<string[]>
    {
        public static FolderPartsComparer Instance { get; } = new();

        public int Compare(string[]? x, string[]? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            for (var index = 0; index < Math.Min(x.Length, y.Length); index++)
            {
                var result = LogicalFileNameComparer.Instance.Compare(x[index], y[index]);
                if (result != 0) return result;

                result = StringComparer.Ordinal.Compare(x[index], y[index]);
                if (result != 0) return result;
            }

            return x.Length.CompareTo(y.Length);
        }
    }
}
