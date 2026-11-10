using System.Threading.Channels;
using Garou.Application.Libraries;
using Garou.Domain.Abstractions;
using Garou.Domain.Entities;
using Garou.Domain.Enums;
using Garou.Domain.Models;
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
    TimeProvider timeProvider) : ILibraryScanner
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

        switch (change.Kind)
        {
            case FileChangeKind.Deleted:
                await mediaFiles.DeleteByPathAsync(change.FullPath, cancellationToken).ConfigureAwait(false);
                break;
            case FileChangeKind.Renamed:
                await mediaFiles.DeleteByPathAsync(change.OldFullPath!, cancellationToken).ConfigureAwait(false);
                await IndexFileAsync(library.Id, library.RootPath, change.FullPath, cancellationToken).ConfigureAwait(false);
                break;
            default:
                await IndexFileAsync(library.Id, library.RootPath, change.FullPath, cancellationToken).ConfigureAwait(false);
                break;
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
    }

    private static bool IsExcluded(string path, IReadOnlyList<string> excludedPaths) =>
        excludedPaths.Any(excludedPath => IsSameOrDescendant(excludedPath, path));

    private static bool IsSameOrDescendant(string parentPath, string path)
    {
        var fullParentPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentPath));
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.Equals(fullParentPath, fullPath, StringComparison.OrdinalIgnoreCase))
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
