using System.Threading.Channels;
using Garou.Application.Libraries;
using Garou.Domain.Abstractions;
using Garou.Domain.Entities;
using Garou.Domain.Enums;
using Garou.Domain.Models;
using Garou.Infrastructure.Metadata;
using Garou.Infrastructure.Settings;

namespace Garou.Infrastructure.Sync;

public sealed class MediaIndexerService(
    IFileWatcherService watcher,
    ILibraryRepository libraries,
    IMediaFileRepository mediaFiles,
    IFolderRepository folders,
    ILibraryExcludedFolderRepository excludedFolders,
    LibraryOperationGate operationGate,
    AppSettingsSession settingsSession,
    TimeProvider timeProvider,
    XnfoIndexService? xnfoIndex = null) : ILibraryScanner
{
    private const int BatchSize = 200;
    private static readonly TimeSpan OfflineRetryInterval = TimeSpan.FromSeconds(30);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var enabled = (await libraries.GetAllAsync(cancellationToken).ConfigureAwait(false))
            .Where(library => library.IsEnabled);

        var tasks = enabled
            .Select(library => ConsumeAsync(library, cancellationToken));

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task ConsumeAsync(LibraryDefinition library, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await ScanAsync(library, null, cancellationToken).ConfigureAwait(false);
            if (!library.IsOnline)
            {
                await Task.Delay(OfflineRetryInterval, timeProvider, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await foreach (var change in watcher.WatchAsync(library, cancellationToken).ConfigureAwait(false))
            {
                await HandleAsync(library, change, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task ScanAsync(LibraryDefinition library, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        using var operation = await operationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await ScanCoreAsync(library, progress, clearSuppressedPaths: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task ScanCoreAsync(
        LibraryDefinition library,
        IProgress<ScanProgress>? progress,
        bool clearSuppressedPaths,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(library.RootPath))
        {
            library.IsOnline = false;
            return;
        }

        library.IsOnline = true;
        if (clearSuppressedPaths)
        {
            operationGate.ClearSuppressedPathsUnder(library.RootPath);
        }
        else if (operationGate.IsPathSuppressed(library.RootPath))
        {
            return;
        }

        progress?.Report(new ScanProgress("Enumerating files", 0, 0));
        var searchOption = settingsSession.Current.ScanSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var excludedPaths = await excludedFolders.GetPathsAsync(library.Id, cancellationToken).ConfigureAwait(false);
        var files = Directory.EnumerateFiles(library.RootPath, "*", searchOption)
            .Where(path => IsSupported(Path.GetExtension(path))
                && !IsExcluded(path, excludedPaths)
                && (clearSuppressedPaths || !operationGate.IsPathSuppressed(path)))
            .ToList();

        var known = await mediaFiles.GetSignaturesUnderRootAsync(library.RootPath, cancellationToken).ConfigureAwait(false);

        var folderIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        await EnsureFolderAsync(library.RootPath, library.RootPath, folderIds, cancellationToken).ConfigureAwait(false);
        foreach (var directory in files.Select(Path.GetDirectoryName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await EnsureFolderAsync(directory, library.RootPath, folderIds, cancellationToken).ConfigureAwait(false);
        }

        var changed = files.Where(path => IsChanged(path, known)).ToList();
        var processed = 0;
        var progressLock = new object();
        var lastReportedTimestamp = timeProvider.GetTimestamp();
        var channel = Channel.CreateBounded<MediaFile>(new BoundedChannelOptions(BatchSize * 2)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });
        using var processingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        void ReportProgress(int done)
        {
            var now = timeProvider.GetTimestamp();
            lock (progressLock)
            {
                if (done != changed.Count && timeProvider.GetElapsedTime(lastReportedTimestamp, now) < TimeSpan.FromMilliseconds(200))
                {
                    return;
                }

                lastReportedTimestamp = now;
            }

            progress?.Report(new ScanProgress("Indexing media", done, changed.Count));
        }

        async Task ProcessChangedFilesAsync()
        {
            try
            {
                await Parallel.ForEachAsync(
                    changed,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Environment.ProcessorCount,
                        CancellationToken = processingCancellation.Token
                    },
                    async (path, ct) =>
                    {
                        var mediaFile = await BuildMediaFileAsync(path, folderIds[Path.GetDirectoryName(path)!], ct).ConfigureAwait(false);
                        if (mediaFile is not null)
                        {
                            await channel.Writer.WriteAsync(mediaFile, ct).ConfigureAwait(false);
                        }

                        ReportProgress(Interlocked.Increment(ref processed));
                    }).ConfigureAwait(false);
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }

        var processingTask = ProcessChangedFilesAsync();
        try
        {
            var batch = new List<MediaFile>(BatchSize);
            await foreach (var mediaFile in channel.Reader.ReadAllAsync(processingCancellation.Token).ConfigureAwait(false))
            {
                batch.Add(mediaFile);
                if (batch.Count == BatchSize)
                {
                    await mediaFiles.UpsertBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                    batch.Clear();
                }
            }

            await processingTask.ConfigureAwait(false);
            if (batch.Count > 0)
            {
                await mediaFiles.UpsertBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            processingCancellation.Cancel();
            try
            {
                await processingTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (processingCancellation.IsCancellationRequested)
            {
            }

            throw;
        }

        if (xnfoIndex is not null)
        {
            await xnfoIndex.SyncScanAsync(files, cancellationToken).ConfigureAwait(false);
        }

        var seen = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stale = known.Keys.Where(path => !seen.Contains(path));

        foreach (var path in stale)
        {
            await mediaFiles.DeleteByPathAsync(path, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task HandleAsync(LibraryDefinition library, FileChangeEvent change, CancellationToken cancellationToken)
    {
        using var operation = await operationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await HandleCoreAsync(library, change, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleCoreAsync(LibraryDefinition library, FileChangeEvent change, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(library.RootPath))
        {
            library.IsOnline = false;
            return;
        }

        if (change.RequiresFullRescan)
        {
            await ScanCoreAsync(library, null, clearSuppressedPaths: false, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (xnfoIndex is not null)
        {
            var isSidecar = change.FullPath.EndsWith(".xnfo", StringComparison.OrdinalIgnoreCase);
            var oldSidecarPath = change.Kind == FileChangeKind.Renamed ? change.OldFullPath : null;
            var wasSidecar = oldSidecarPath?.EndsWith(".xnfo", StringComparison.OrdinalIgnoreCase) == true;
            if (isSidecar || wasSidecar)
            {
                if (isSidecar)
                    await SyncSidecarAsync(change.FullPath, cancellationToken).ConfigureAwait(false);
                if (wasSidecar)
                    await SyncSidecarAsync(oldSidecarPath!, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        switch (change.Kind)
        {
            case FileChangeKind.Deleted:
                if (await folders.GetByPathAsync(change.FullPath, cancellationToken).ConfigureAwait(false) is not null)
                {
                    await ScanCoreAsync(library, null, clearSuppressedPaths: false, cancellationToken).ConfigureAwait(false);
                    await RemoveMissingFolderAsync(change.FullPath, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await mediaFiles.DeleteByPathAsync(change.FullPath, cancellationToken).ConfigureAwait(false);
                }
                break;
            case FileChangeKind.Renamed:
                ArgumentException.ThrowIfNullOrWhiteSpace(change.OldFullPath);
                if (Directory.Exists(change.FullPath))
                {
                    var paths = await mediaFiles.GetPathsUnderRootAsync(change.OldFullPath, cancellationToken).ConfigureAwait(false);
                    foreach (var oldPath in paths)
                    {
                        var newPath = Path.Combine(change.FullPath, Path.GetRelativePath(change.OldFullPath, oldPath));
                        await MoveFileAsync(library, oldPath, newPath, cancellationToken).ConfigureAwait(false);
                    }
                    await ScanCoreAsync(library, null, clearSuppressedPaths: false, cancellationToken).ConfigureAwait(false);
                    await RemoveMissingFolderAsync(change.OldFullPath, cancellationToken).ConfigureAwait(false);
                }
                else if (File.Exists(change.FullPath))
                {
                    await MoveFileAsync(library, change.OldFullPath, change.FullPath, cancellationToken).ConfigureAwait(false);
                }
                break;
            default:
                if (Directory.Exists(change.FullPath))
                {
                    await ScanCoreAsync(library, null, clearSuppressedPaths: false, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await IndexFileAsync(library.Id, library.RootPath, change.FullPath, cancellationToken).ConfigureAwait(false);
                }
                break;
        }
    }

    private async Task SyncSidecarAsync(string sidecarPath, CancellationToken cancellationToken)
    {
        var mediaPath = sidecarPath[..^5];
        if (xnfoIndex is not null && await mediaFiles.GetByPathAsync(mediaPath, cancellationToken).ConfigureAwait(false) is not null)
        {
            await xnfoIndex.SyncObservedAsync(mediaPath, notifyChange: true, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RemoveMissingFolderAsync(string path, CancellationToken cancellationToken)
    {
        if (Directory.Exists(path) || (await mediaFiles.GetPathsUnderRootAsync(path, cancellationToken).ConfigureAwait(false)).Count != 0)
        {
            return;
        }

        var registeredLibraries = await libraries.GetAllAsync(cancellationToken).ConfigureAwait(false);
        if (registeredLibraries.Any(library => IsSameOrDescendant(path, library.RootPath)))
        {
            return;
        }

        var folder = await folders.GetByPathAsync(path, cancellationToken).ConfigureAwait(false);
        if (folder is not null)
        {
            await folders.DeleteSubtreeAsync(folder.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task MoveFileAsync(LibraryDefinition library, string oldPath, string newPath, CancellationToken cancellationToken)
    {
        var excludedPaths = await excludedFolders.GetPathsAsync(library.Id, cancellationToken).ConfigureAwait(false);
        if (operationGate.IsPathSuppressed(newPath) || IsExcluded(newPath, excludedPaths)
            || !IsSameOrDescendant(library.RootPath, newPath)
            || !IsWithinScanScope(library.RootPath, newPath) || !IsSupported(Path.GetExtension(newPath)))
        {
            await mediaFiles.DeleteByPathAsync(oldPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        var directory = Path.GetDirectoryName(newPath)
            ?? throw new InvalidOperationException("The moved media file has no parent directory.");
        var folderId = await EnsureFolderAsync(directory, library.RootPath, new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase), cancellationToken).ConfigureAwait(false);
        var file = await BuildMediaFileAsync(newPath, folderId, cancellationToken).ConfigureAwait(false);
        if (file is not null)
        {
            if (!await mediaFiles.MoveAsync(oldPath, file, cancellationToken).ConfigureAwait(false))
            {
                await mediaFiles.UpsertBatchAsync([file], cancellationToken).ConfigureAwait(false);
            }
            if (xnfoIndex is not null)
            {
                await xnfoIndex.SyncObservedAsync(newPath, notifyChange: true, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal async Task<MediaFile?> BuildMediaFileAsync(string path, Guid? folderId, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var info = new FileInfo(path);
        var draft = new MediaFile
        {
            Path = path,
            FileName = info.Name,
            Extension = info.Extension,
            MediaType = ResolveMediaType(info.Extension),
            FileSize = info.Length,
            CreatedDateUtc = info.CreationTimeUtc,
            ModifiedDateUtc = info.LastWriteTimeUtc,
            LastIndexedUtc = timeProvider.GetUtcNow().UtcDateTime,
            FolderId = folderId
        };

        return draft;
    }

    internal async Task IndexFileAsync(Guid libraryId, string rootPath, string path, CancellationToken cancellationToken)
    {
        if (operationGate.IsPathSuppressed(path))
        {
            return;
        }

        var excludedPaths = await excludedFolders.GetPathsAsync(libraryId, cancellationToken).ConfigureAwait(false);
        if (IsExcluded(path, excludedPaths))
        {
            return;
        }

        if (!IsWithinScanScope(rootPath, path) || !IsSupported(Path.GetExtension(path)))
        {
            await mediaFiles.DeleteByPathAsync(path, cancellationToken).ConfigureAwait(false);
            return;
        }

        var folderIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var directory = Path.GetDirectoryName(path);
        Guid? folderId = directory is null
            ? null
            : await EnsureFolderAsync(directory, rootPath, folderIds, cancellationToken).ConfigureAwait(false);

        var mediaFile = await BuildMediaFileAsync(path, folderId, cancellationToken).ConfigureAwait(false);
        if (mediaFile is null)
        {
            return;
        }

        await mediaFiles.UpsertBatchAsync([mediaFile], cancellationToken).ConfigureAwait(false);
        if (xnfoIndex is not null)
        {
            await xnfoIndex.SyncObservedAsync(path, notifyChange: true, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsExcluded(string path, IReadOnlyList<string> excludedPaths) =>
        excludedPaths.Any(excludedPath => IsSameOrDescendant(excludedPath, path));

    private static bool IsSameOrDescendant(string parentPath, string path)
    {
        var fullParentPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentPath));
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.Equals(fullParentPath, fullPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return true;
        }

        var relativePath = Path.GetRelativePath(fullParentPath, fullPath);
        return !Path.IsPathRooted(relativePath)
            && relativePath != ".."
            && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private bool IsWithinScanScope(string rootPath, string path)
    {
        if (settingsSession.Current.ScanSubfolders)
        {
            return true;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        return string.Equals(directory, root, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<Guid> EnsureFolderAsync(string directory, string rootPath, IDictionary<string, Guid> folderIds, CancellationToken cancellationToken)
    {
        if (folderIds.TryGetValue(directory, out var cachedId))
        {
            return cachedId;
        }

        var existing = await folders.GetByPathAsync(directory, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            folderIds[directory] = existing.Id;
            return existing.Id;
        }

        Guid? parentId = null;
        var isRoot = string.Equals(directory, Path.TrimEndingDirectorySeparator(rootPath), StringComparison.OrdinalIgnoreCase);
        if (!isRoot && Path.GetDirectoryName(directory) is { } parentDirectory)
        {
            parentId = await EnsureFolderAsync(parentDirectory, rootPath, folderIds, cancellationToken).ConfigureAwait(false);
        }

        var id = await folders.AddAsync(new Folder { Path = directory, ParentId = parentId }, cancellationToken).ConfigureAwait(false);
        folderIds[directory] = id;
        return id;
    }

    internal static bool IsChanged(string path, IReadOnlyDictionary<string, MediaFileSignature> known)
    {
        if (!known.TryGetValue(path, out var signature))
        {
            return true;
        }

        var info = new FileInfo(path);
        return info.Length != signature.FileSize
            || info.LastWriteTimeUtc != signature.ModifiedDateUtc;
    }

    private bool IsSupported(string extension) =>
        MediaFileExtensions.All.Contains(extension, StringComparer.OrdinalIgnoreCase)
        && settingsSession.Current.IncludedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

    private static MediaType ResolveMediaType(string extension) =>
        MediaFileExtensions.Images.Contains(extension, StringComparer.OrdinalIgnoreCase) ? MediaType.Image
        : MediaFileExtensions.Videos.Contains(extension, StringComparer.OrdinalIgnoreCase) ? MediaType.Video
        : MediaFileExtensions.Audio.Contains(extension, StringComparer.OrdinalIgnoreCase) ? MediaType.Audio
        : MediaType.Unknown;
}
