using System.Collections.Concurrent;
using Garou.Domain.Abstractions;
using Garou.Domain.Entities;
using Garou.Domain.Enums;
using Garou.Domain.Models;

namespace Garou.Infrastructure.Sync;

public sealed class MediaIndexerService(
    IFileWatcherService watcher,
    ILibraryRepository libraries,
    IMetadataExtractionService metadataExtraction,
    IThumbnailGeneratorService thumbnailGenerator,
    IMediaFileRepository mediaFiles,
    IFolderRepository folders,
    TimeProvider timeProvider) : ILibraryScanner
{
    private const int BatchSize = 200;
    private static readonly TimeSpan OfflineRetryInterval = TimeSpan.FromSeconds(30);

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v", ".mpg", ".mpeg"
    };

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
        if (!Directory.Exists(library.RootPath))
        {
            library.IsOnline = false;
            return;
        }

        library.IsOnline = true;
        progress?.Report(new ScanProgress("Enumerating files", 0, 0));
        var files = Directory.EnumerateFiles(library.RootPath, "*", SearchOption.AllDirectories)
            .Where(path => IsSupported(Path.GetExtension(path)))
            .ToList();

        var known = await mediaFiles.GetSignaturesUnderRootAsync(library.RootPath, cancellationToken).ConfigureAwait(false);

        var folderIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in files.Select(Path.GetDirectoryName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await EnsureFolderAsync(directory, library.RootPath, folderIds, cancellationToken).ConfigureAwait(false);
        }

        var changed = files.Where(path => IsChanged(path, known)).ToList();
        var processed = 0;
        var results = new ConcurrentBag<MediaFile>();
        await Parallel.ForEachAsync(
            changed,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = cancellationToken
            },
            async (path, ct) =>
            {
                var mediaFile = await BuildMediaFileAsync(path, folderIds[Path.GetDirectoryName(path)!], ct).ConfigureAwait(false);
                if (mediaFile is not null)
                {
                    results.Add(mediaFile);
                }

                var done = Interlocked.Increment(ref processed);
                progress?.Report(new ScanProgress("Indexing media", done, changed.Count));
            }).ConfigureAwait(false);

        foreach (var batch in results.Chunk(BatchSize))
        {
            await mediaFiles.UpsertBatchAsync(batch, cancellationToken).ConfigureAwait(false);
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
        if (!Directory.Exists(library.RootPath))
        {
            library.IsOnline = false;
            return;
        }

        if (change.RequiresFullRescan)
        {
            await ScanAsync(library, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        switch (change.Kind)
        {
            case FileChangeKind.Deleted:
                await mediaFiles.DeleteByPathAsync(change.FullPath, cancellationToken).ConfigureAwait(false);
                break;
            case FileChangeKind.Renamed:
                await mediaFiles.DeleteByPathAsync(change.OldFullPath!, cancellationToken).ConfigureAwait(false);
                await IndexFileAsync(library.RootPath, change.FullPath, cancellationToken).ConfigureAwait(false);
                break;
            default:
                await IndexFileAsync(library.RootPath, change.FullPath, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    internal async Task<MediaFile?> BuildMediaFileAsync(string path, long? folderId, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var metadata = await metadataExtraction.ExtractAsync(path, cancellationToken).ConfigureAwait(false);
        var info = new FileInfo(path);
        var draft = new MediaFile
        {
            Path = path,
            FileName = info.Name,
            Extension = info.Extension,
            MediaType = ResolveMediaType(info.Extension),
            FileSize = info.Length,
            Width = metadata.Width,
            Height = metadata.Height,
            DurationSeconds = metadata.DurationSeconds,
            CreatedDateUtc = info.CreationTimeUtc,
            ModifiedDateUtc = info.LastWriteTimeUtc,
            LastIndexedUtc = timeProvider.GetUtcNow().UtcDateTime,
            FolderId = folderId
        };

        var thumbnailPath = await thumbnailGenerator.GenerateAsync(draft, cancellationToken).ConfigureAwait(false);
        return new MediaFile
        {
            Path = draft.Path,
            FileName = draft.FileName,
            Extension = draft.Extension,
            MediaType = draft.MediaType,
            FileSize = draft.FileSize,
            Width = draft.Width,
            Height = draft.Height,
            DurationSeconds = draft.DurationSeconds,
            CreatedDateUtc = draft.CreatedDateUtc,
            ModifiedDateUtc = draft.ModifiedDateUtc,
            LastIndexedUtc = draft.LastIndexedUtc,
            FolderId = draft.FolderId,
            ThumbnailPath = thumbnailPath
        };
    }

    internal async Task IndexFileAsync(string rootPath, string path, CancellationToken cancellationToken)
    {
        var folderIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var directory = Path.GetDirectoryName(path);
        long? folderId = directory is null
            ? null
            : await EnsureFolderAsync(directory, rootPath, folderIds, cancellationToken).ConfigureAwait(false);

        var mediaFile = await BuildMediaFileAsync(path, folderId, cancellationToken).ConfigureAwait(false);
        if (mediaFile is null)
        {
            return;
        }

        await mediaFiles.UpsertBatchAsync([mediaFile], cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> EnsureFolderAsync(string directory, string rootPath, IDictionary<string, long> folderIds, CancellationToken cancellationToken)
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

        long? parentId = null;
        var isRoot = string.Equals(directory, Path.TrimEndingDirectorySeparator(rootPath), StringComparison.OrdinalIgnoreCase);
        if (!isRoot && Path.GetDirectoryName(directory) is { } parentDirectory)
        {
            parentId = await EnsureFolderAsync(parentDirectory, rootPath, folderIds, cancellationToken).ConfigureAwait(false);
        }

        var id = await folders.AddAsync(new Folder { Id = 0, Path = directory, ParentId = parentId }, cancellationToken).ConfigureAwait(false);
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

    internal static bool IsSupported(string extension) =>
        ImageExtensions.Contains(extension) || VideoExtensions.Contains(extension);

    private static MediaType ResolveMediaType(string extension) =>
        ImageExtensions.Contains(extension) ? MediaType.Image
        : VideoExtensions.Contains(extension) ? MediaType.Video
        : MediaType.Unknown;
}
