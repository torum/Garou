using System.Threading.Channels;
using Garou.Domain.Abstractions;
using Garou.Domain.Models;

namespace Garou.Infrastructure.Sync;

public sealed class FileWatcherService : IFileWatcherService
{
    private const int ChannelCapacity = 4096;
    private const int WatcherBufferSize = 64 * 1024;

    public async IAsyncEnumerable<FileChangeEvent> WatchAsync(
        LibraryDefinition library,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!Directory.Exists(library.RootPath))
        {
            library.IsOnline = false;
            yield break;
        }

        var channel = Channel.CreateBounded<FileChangeEvent>(new BoundedChannelOptions(ChannelCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        var overflowed = 0;

        void Publish(FileChangeEvent change)
        {
            if (!channel.Writer.TryWrite(change))
            {
                Interlocked.Exchange(ref overflowed, 1);
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
        watcher.Error += (_, _) =>
        {
            Interlocked.Exchange(ref overflowed, 1);
            channel.Writer.TryWrite(Create(FileChangeKind.Modified, library.RootPath, requiresFullRescan: true));
        };

        watcher.EnableRaisingEvents = true;

        try
        {
            await foreach (var change in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return change;

                if (Interlocked.Exchange(ref overflowed, 0) == 1)
                {
                    yield return Create(FileChangeKind.Modified, library.RootPath, requiresFullRescan: true);
                }
            }
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    }
}
