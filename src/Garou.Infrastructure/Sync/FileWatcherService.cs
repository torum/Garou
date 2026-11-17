using Garou.Domain.Abstractions;
using Garou.Domain.Models;

namespace Garou.Infrastructure.Sync;

public sealed class FileWatcherService : IFileWatcherService
{
    private const int ChannelCapacity = 4096;
    private const int WatcherBufferSize = 64 * 1024;

    internal ApplicationStorageFilter StorageFilter { get; set; } = ApplicationStorageFilter.Default;

    public async IAsyncEnumerable<FileChangeEvent> WatchAsync(
        LibraryDefinition library,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!Directory.Exists(library.RootPath))
        {
            library.IsOnline = false;
            yield break;
        }

        var queue = new WatcherEventQueue(library.RootPath, ChannelCapacity);

        void Publish(FileChangeEvent change)
        {
            var filtered = StorageFilter.Filter(change);
            if (filtered is not null)
            {
                queue.Publish(filtered);
            }
        }

        FileChangeEvent Create(FileChangeKind kind, string fullPath, string? oldFullPath = null, bool requiresFullRescan = false) =>
            new()
            {
                LibraryRoot = library.RootPath,
                FullPath = fullPath,
                OldFullPath = oldFullPath,
                Kind = kind,
                RequiresFullRescan = requiresFullRescan
            };

        using var watcher = new FileSystemWatcher(library.RootPath)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = WatcherBufferSize,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
        };

        watcher.Created += (_, e) => Publish(Create(FileChangeKind.Created, e.FullPath));
        watcher.Changed += (_, e) => Publish(Create(FileChangeKind.Modified, e.FullPath));
        watcher.Deleted += (_, e) => Publish(Create(FileChangeKind.Deleted, e.FullPath));
        watcher.Renamed += (_, e) => Publish(Create(FileChangeKind.Renamed, e.FullPath, e.OldFullPath));
        watcher.Error += (_, e) => queue.ReportError(e.GetException());

        watcher.EnableRaisingEvents = true;

        try
        {
            yield return new FileChangeEvent { LibraryRoot = library.RootPath, FullPath = library.RootPath, WatcherReady = true };
            await foreach (var change in queue.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return change;
            }
        }
        finally
        {
            queue.Complete();
        }
    }
}
