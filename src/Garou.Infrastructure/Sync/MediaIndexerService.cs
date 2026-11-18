using System.Collections.Concurrent;
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
    private static readonly StringComparison SidecarPathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly StringComparer SidecarPathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public event Action<string>? AutomaticScanStarted;
    public event Action<ScanProgress>? AutomaticScanProgress;
    public event Action<string, bool, Exception?, bool>? AutomaticScanFinished;
    public event Action<string, string>? IndexingIssue;
    public event Action<string, int, string>? ItemsSkipped;

    internal Func<string, IEnumerable<string>> EnumerateDirectoryEntries { get; set; } =
        path => Directory.EnumerateFileSystemEntries(path);

    internal ApplicationStorageFilter StorageFilter { get; set; } = ApplicationStorageFilter.Default;
    internal TimeSpan RetryInterval { get; set; } = OfflineRetryInterval;

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
        var needsInitialScan = true;
        var reconcileAfterRestart = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            var scanning = false;
            try
            {
                if (needsInitialScan)
                {
                    scanning = true;
                    using (await operationGate.EnterAsync(cancellationToken).ConfigureAwait(false))
                    {
                        await RunAutomaticScanAsync(library, clearSuppressedPaths: true, cancellationToken, isOfflineRetry: !library.IsOnline).ConfigureAwait(false);
                    }
                    scanning = false;
                    if (!library.IsOnline)
                    {
                        await Task.Delay(RetryInterval, timeProvider, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    needsInitialScan = false;
                }

                await foreach (var change in watcher.WatchAsync(library, cancellationToken).ConfigureAwait(false))
                {
                    if (change.WatcherReady)
                    {
                        if (reconcileAfterRestart)
                        {
                            scanning = true;
                            using (await operationGate.EnterAsync(cancellationToken).ConfigureAwait(false))
                            {
                                await RunAutomaticScanAsync(library, clearSuppressedPaths: false, cancellationToken).ConfigureAwait(false);
                            }
                            scanning = false;
                            reconcileAfterRestart = false;
                            if (!library.IsOnline)
                            {
                                needsInitialScan = true;
                                break;
                            }
                        }
                    }
                    else
                    {
                        await HandleAsync(library, change, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    if (library.IsOnline)
                    {
                        IndexingIssue?.Invoke(library.RootPath, "File monitoring stopped unexpectedly; retrying.");
                        reconcileAfterRestart = true;
                    }
                    else
                    {
                        needsInitialScan = true;
                    }
                    await Task.Delay(RetryInterval, timeProvider, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine($"Indexing of '{library.RootPath}' failed: {exception}");
                if (!scanning)
                {
                    IndexingIssue?.Invoke(library.RootPath, exception.Message);
                }
                if (!needsInitialScan)
                {
                    reconcileAfterRestart = true;
                }
                await Task.Delay(RetryInterval, timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task ScanAsync(LibraryDefinition library, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        using var operation = await operationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await ScanCoreAsync(library, progress, clearSuppressedPaths: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunAutomaticScanAsync(LibraryDefinition library, bool clearSuppressedPaths, CancellationToken cancellationToken, Func<Task>? afterScan = null, bool isOfflineRetry = false)
    {
        if (!isOfflineRetry)
            AutomaticScanStarted?.Invoke(library.RootPath);
        Exception? error = null;
        try
        {
            await ScanCoreAsync(library, isOfflineRetry ? null : new AutomaticProgress(this), clearSuppressedPaths, cancellationToken).ConfigureAwait(false);
            if (afterScan is not null)
                await afterScan().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            error = exception;
            throw;
        }
        finally
        {
            AutomaticScanFinished?.Invoke(library.RootPath, library.IsOnline, error, isOfflineRetry);
        }
    }

    private sealed class AutomaticProgress(MediaIndexerService owner) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => owner.AutomaticScanProgress?.Invoke(value);
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

        progress?.Report(new ScanProgress("ScanEnumerating", 0, 0));
        var excludedPaths = await excludedFolders.GetPathsAsync(library.Id, cancellationToken).ConfigureAwait(false);
        var files = new List<string>();
        var sidecars = new List<string>();
        var uninspectableSidecars = new HashSet<string>(SidecarPathComparer);
        var unreadableDirectories = new List<string>();
        var skippedEntries = new List<string>();
        var pendingDirectories = new Stack<string>();
        var enumerationTimestamp = timeProvider.GetTimestamp();
        pendingDirectories.Push(library.RootPath);
        while (pendingDirectories.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (StorageFilter.IsIgnored(directory))
            {
                continue;
            }
            try
            {
                foreach (var path in EnumerateDirectoryEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (StorageFilter.IsIgnored(path) || IsExcluded(path, excludedPaths)
                        || (!clearSuppressedPaths && operationGate.IsPathSuppressed(path)))
                    {
                        continue;
                    }

                    bool isDirectory;
                    try
                    {
                        var attributes = File.GetAttributes(path);
                        isDirectory = (attributes & FileAttributes.Directory) != 0;
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            if (!isDirectory && path.EndsWith(".xnfo", SidecarPathComparison))
                            {
                                sidecars.Add(path);
                            }
                            continue;
                        }
                    }
                    catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
                    {
                        // An entry that vanished or is unreadable must not abort the rest of its folder.
                        System.Diagnostics.Debug.WriteLine($"Could not read entry '{path}': {exception.Message}");
                        if (path.EndsWith(".xnfo", SidecarPathComparison))
                        {
                            uninspectableSidecars.Add(path[..^5]);
                        }
                        if (exception is not UnauthorizedAccessException)
                        {
                            skippedEntries.Add(path);
                        }

                        continue;
                    }

                    if (isDirectory)
                    {
                        if (settingsSession.Current.ScanSubfolders)
                        {
                            pendingDirectories.Push(path);
                        }
                    }
                    else if (path.EndsWith(".xnfo", SidecarPathComparison))
                    {
                        sidecars.Add(path);
                    }
                    else if (IsSupported(Path.GetExtension(path)))
                    {
                        files.Add(path);
                        var now = timeProvider.GetTimestamp();
                        if (timeProvider.GetElapsedTime(enumerationTimestamp, now) >= TimeSpan.FromMilliseconds(200))
                        {
                            enumerationTimestamp = now;
                            progress?.Report(new ScanProgress("ScanEnumerating", files.Count, 0));
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                System.Diagnostics.Debug.WriteLine($"Could not enumerate folder '{directory}': {exception.Message}");
                unreadableDirectories.Add(directory);
                if (string.Equals(directory, library.RootPath, StringComparison.Ordinal))
                {
                    library.IsOnline = false;
                    return;
                }
                if (exception is not UnauthorizedAccessException)
                {
                    skippedEntries.Add(directory);
                }
            }
        }

        if (skippedEntries.Count > 0)
        {
            ItemsSkipped?.Invoke(library.RootPath, skippedEntries.Count, skippedEntries[0]);
        }

        progress?.Report(new ScanProgress("ScanReadingIndex", 0, 0));
        var known = await mediaFiles.GetSignaturesUnderRootAsync(library.RootPath, cancellationToken).ConfigureAwait(false);

        progress?.Report(new ScanProgress("ScanPreparingFolders", 0, 0));
        var folderIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        await EnsureFolderAsync(library.RootPath, library.RootPath, folderIds, cancellationToken).ConfigureAwait(false);
        foreach (var directory in files.Select(Path.GetDirectoryName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await EnsureFolderAsync(directory, library.RootPath, folderIds, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new ScanProgress("ScanCheckingFiles", 0, 0));
        var changed = new List<string>();
        var uninspectableFiles = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var checkedCount = 0;
        var checkTimestamp = timeProvider.GetTimestamp();
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (IsChanged(path, known))
                {
                    changed.Add(path);
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                System.Diagnostics.Debug.WriteLine($"Could not inspect media file '{path}': {exception.Message}");
                uninspectableFiles.TryAdd(path, 0);
            }
            checkedCount++;
            var now = timeProvider.GetTimestamp();
            if (checkedCount == files.Count || timeProvider.GetElapsedTime(checkTimestamp, now) >= TimeSpan.FromMilliseconds(200))
            {
                checkTimestamp = now;
                progress?.Report(new ScanProgress("ScanCheckingFiles", checkedCount, files.Count));
            }
        }
        progress?.Report(new ScanProgress("ScanIndexingMedia", 0, changed.Count));
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

            progress?.Report(new ScanProgress("ScanIndexingMedia", done, changed.Count));
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
                        MediaFile? mediaFile;
                        try
                        {
                            mediaFile = await BuildMediaFileAsync(path, folderIds[Path.GetDirectoryName(path)!], ct).ConfigureAwait(false);
                        }
                        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
                        {
                            System.Diagnostics.Debug.WriteLine($"Could not inspect media file '{path}': {exception.Message}");
                            mediaFile = null;
                        }

                        if (mediaFile is not null)
                        {
                            await channel.Writer.WriteAsync(mediaFile, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            uninspectableFiles.TryAdd(path, 0);
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

        bool IsUnderUnreadableDirectory(string path) =>
            unreadableDirectories.Any(directory => IsSameOrDescendant(directory, path));

        if (xnfoIndex is not null)
        {
            progress?.Report(new ScanProgress("ScanIndexingMetadata", 0, 0));
            var inspectableFiles = files.Where(path => !uninspectableFiles.ContainsKey(path) && !IsUnderUnreadableDirectory(path))
                .ToHashSet(SidecarPathComparer);
            var discoveredSidecars = new List<(string MediaPath, string SidecarPath)>();
            foreach (var sidecar in sidecars)
            {
                if (inspectableFiles.TryGetValue(sidecar[..^5], out var mediaPath))
                {
                    discoveredSidecars.Add((mediaPath, sidecar));
                }
            }
            await xnfoIndex.SyncScanAsync(library.RootPath, inspectableFiles, discoveredSidecars, uninspectableSidecars, progress, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new ScanProgress("ScanRemovingStale", 0, 0));
        var seen = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stale = known.Keys.Where(path => !seen.Contains(path) && !IsUnderUnreadableDirectory(path));

        foreach (var path in stale)
        {
            await mediaFiles.DeleteByPathAsync(path, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task HandleAsync(LibraryDefinition library, FileChangeEvent change, CancellationToken cancellationToken)
    {
        if (change.WatcherError is { } watcherError)
        {
            IndexingIssue?.Invoke(library.RootPath, watcherError);
            return;
        }

        var filtered = StorageFilter.Filter(change);
        if (filtered is null)
        {
            return;
        }

        using var operation = await operationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await HandleCoreAsync(library, filtered, cancellationToken).ConfigureAwait(false);
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
            await RunAutomaticScanAsync(library, clearSuppressedPaths: false, cancellationToken).ConfigureAwait(false);
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
                    await RunAutomaticScanAsync(library, clearSuppressedPaths: false, cancellationToken,
                        () => RemoveMissingFolderAsync(change.FullPath, cancellationToken)).ConfigureAwait(false);
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
                    await RunAutomaticScanAsync(library, clearSuppressedPaths: false, cancellationToken,
                        () => RemoveMissingFolderAsync(change.OldFullPath, cancellationToken)).ConfigureAwait(false);
                }
                else if (File.Exists(change.FullPath))
                {
                    await MoveFileAsync(library, change.OldFullPath, change.FullPath, cancellationToken).ConfigureAwait(false);
                }
                break;
            default:
                if (Directory.Exists(change.FullPath))
                {
                    if (change.Kind == FileChangeKind.Created)
                    {
                        await RunAutomaticScanAsync(library, clearSuppressedPaths: false, cancellationToken).ConfigureAwait(false);
                    }
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
