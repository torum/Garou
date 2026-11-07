using Garou.Domain.Abstractions;
using Garou.Domain.Entities;
using Garou.Domain.Enums;
using Garou.Domain.Models;
using Garou.Infrastructure.Sync;

namespace Garou.Tests.Sync;

public sealed class SyncTests
{
    [Fact]
    public async Task WhenLibraryRootIsMissingThenLibraryIsMarkedOfflineAndNothingIsWritten()
    {
        var repository = new FakeMediaFileRepository();
        var library = new LibraryDefinition
        {
            Id = "lib",
            RootPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}")
        };

        await CreateScanService(repository).ScanAsync(library, null, CancellationToken.None);

        Assert.False(library.IsOnline);
        Assert.Empty(repository.Upserted);
        Assert.Equal(0, repository.DeleteCount);
    }

    [Fact]
    public async Task WhenInitialScanRunsThenOnlySupportedFilesAreIndexed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        await File.WriteAllBytesAsync(Path.Combine(root, "photo.jpg"), [1, 2, 3], cancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(root, "nested", "clip.mp4"), [1, 2, 3], cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, "notes.txt"), "not media", cancellationToken);
        var repository = new FakeMediaFileRepository();
        var library = new LibraryDefinition { Id = "lib", RootPath = root };

        try
        {
            await CreateScanService(repository).ScanAsync(library, null, CancellationToken.None);

            Assert.Equal(2, repository.Upserted.Count);
            Assert.DoesNotContain(repository.Upserted, file => file.FileName == "notes.txt");
            Assert.All(repository.Upserted, file => Assert.Null(file.ThumbnailPath));
            Assert.True(library.IsOnline);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WhenLibraryIsEmptyThenItsRootFolderIsCreated()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"empty-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var folders = new FakeFolderRepository();

        try
        {
            await CreateScanService(new FakeMediaFileRepository(), folders)
                .ScanAsync(new LibraryDefinition { Id = "lib", RootPath = root }, null, cancellationToken);

            Assert.Equal(root, Assert.Single(await folders.GetAllAsync(cancellationToken)).Path);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WhenAudioFileIsScannedThenItIsIndexedAsAudio()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"audio-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(Path.Combine(root, "track.mp3"), [1, 2, 3], cancellationToken);
        var repository = new FakeMediaFileRepository();

        try
        {
            await CreateScanService(repository).ScanAsync(new LibraryDefinition { Id = "lib", RootPath = root }, null, cancellationToken);

            Assert.Equal(MediaType.Audio, Assert.Single(repository.Upserted).MediaType);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WhenManyFilesAreScannedThenWritesAreBatchedAndProgressIsThrottled()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"batch-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var repository = new FakeMediaFileRepository();
        var progress = new RecordingProgress();

        try
        {
            for (var index = 0; index < 405; index++)
            {
                await File.WriteAllBytesAsync(Path.Combine(root, $"track{index}.mp3"), [1, 2, 3], cancellationToken);
            }

            await CreateScanService(repository).ScanAsync(
                new LibraryDefinition { Id = "lib", RootPath = root },
                progress,
                cancellationToken);

            Assert.Equal([200, 200, 5], repository.UpsertBatchSizes);
            Assert.True(progress.Reports.Count < 405);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WhenInitialScanRunsThenNoRecordsAreDeleted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"nodelete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(Path.Combine(root, "photo.png"), [1, 2, 3], cancellationToken);
        var repository = new FakeMediaFileRepository();

        try
        {
            await CreateScanService(repository).ScanAsync(new LibraryDefinition { Id = "lib", RootPath = root }, null, CancellationToken.None);

            Assert.Equal(0, repository.DeleteCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WhenLibraryIsRescannedThenFilesMissingFromDatabaseAreIndexed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"rescan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(Path.Combine(root, "photo.jpg"), [1, 2, 3], cancellationToken);
        var repository = new FakeMediaFileRepository();

        try
        {
            await CreateScanService(repository).ScanAsync(new LibraryDefinition { Id = "lib", RootPath = root }, null, CancellationToken.None);

            Assert.Single(repository.Upserted);
            Assert.Equal("photo.jpg", repository.Upserted[0].FileName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WhenRescanRunsThenFilesMissingFromDiskAreDeleted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"stale-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(Path.Combine(root, "photo.jpg"), [1, 2, 3], cancellationToken);
        var stalePath = Path.Combine(root, "removed.jpg");
        var repository = new FakeMediaFileRepository();
        repository.StoredPaths.Add(Path.Combine(root, "photo.jpg"));
        repository.StoredPaths.Add(stalePath);

        try
        {
            await CreateScanService(repository).ScanAsync(new LibraryDefinition { Id = "lib", RootPath = root }, null, CancellationToken.None);

            Assert.Equal([stalePath], repository.DeletedPaths);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WhenWatcherReportsRenameThenOldPathIsDeletedAndNewPathIsIndexed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"rename-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var newPath = Path.Combine(root, "renamed.jpg");
        var oldPath = Path.Combine(root, "original.jpg");
        await File.WriteAllBytesAsync(newPath, [1, 2, 3], cancellationToken);
        var repository = new FakeMediaFileRepository();
        var library = new LibraryDefinition { Id = "lib", RootPath = root, IsEnabled = true };
        using var stop = new CancellationTokenSource();
        var watcher = new ScriptedWatcher(
            () => { },
            [new FileChangeEvent { LibraryRoot = root, FullPath = newPath, OldFullPath = oldPath, Kind = FileChangeKind.Renamed }],
            stop);

        try
        {
            await CreateService(repository, watcher, library).RunAsync(stop.Token);

            Assert.Equal([oldPath], repository.DeletedPaths);
            Assert.Contains(repository.Upserted, file => file.Path == newPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WhenWatcherRequestsFullRescanThenFilesAddedWhileWatchingAreIndexed()
    {
        var root = Path.Combine(Path.GetTempPath(), $"fullrescan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var latePath = Path.Combine(root, "late.jpg");
        var repository = new FakeMediaFileRepository();
        var library = new LibraryDefinition { Id = "lib", RootPath = root, IsEnabled = true };
        using var stop = new CancellationTokenSource();
        var watcher = new ScriptedWatcher(
            () => File.WriteAllBytes(latePath, [1, 2, 3]),
            [new FileChangeEvent { LibraryRoot = root, FullPath = root, Kind = FileChangeKind.Modified, RequiresFullRescan = true }],
            stop);

        try
        {
            await CreateService(repository, watcher, library).RunAsync(stop.Token);

            Assert.Single(repository.Upserted);
            Assert.Equal("late.jpg", repository.Upserted[0].FileName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static MediaIndexerService CreateService(FakeMediaFileRepository repository, IFileWatcherService watcher, LibraryDefinition library)
    {
        return new MediaIndexerService(
            watcher,
            new ScriptedLibraryRepository(library),
            new NoopMetadata(),
            repository,
            new FakeFolderRepository(),
            TimeProvider.System);
    }

    private static MediaIndexerService CreateScanService(FakeMediaFileRepository repository, FakeFolderRepository? folders = null)
    {
        return new MediaIndexerService(
            new NoopWatcher(),
            new EmptyLibraryRepository(),
            new NoopMetadata(),
            repository,
            folders ?? new FakeFolderRepository(),
            TimeProvider.System);
    }

    private sealed class FakeFolderRepository : IFolderRepository
    {
        private readonly List<Folder> _folders = [];

        public Task<Folder?> GetByIdAsync(long id, CancellationToken cancellationToken) =>
            Task.FromResult(_folders.FirstOrDefault(folder => folder.Id == id));

        public Task<Folder?> GetByPathAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(_folders.FirstOrDefault(folder => string.Equals(folder.Path, path, StringComparison.OrdinalIgnoreCase)));

        public Task<IReadOnlyList<Folder>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Folder>>(_folders.ToList());

        public Task<long> AddAsync(Folder folder, CancellationToken cancellationToken)
        {
            var id = _folders.Count + 1L;
            _folders.Add(new Folder { Id = id, Path = folder.Path, ParentId = folder.ParentId });
            return Task.FromResult(id);
        }

        public Task DeleteSubtreeAsync(long folderId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class EmptyLibraryRepository : ILibraryRepository
    {
        public Task<IReadOnlyList<LibraryDefinition>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LibraryDefinition>>([]);

        public Task AddAsync(LibraryDefinition library, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetEnabledAsync(string id, bool isEnabled, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ScriptedLibraryRepository(LibraryDefinition library) : ILibraryRepository
    {
        public Task<IReadOnlyList<LibraryDefinition>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LibraryDefinition>>([library]);

        public Task AddAsync(LibraryDefinition library, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetEnabledAsync(string id, bool isEnabled, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ScriptedWatcher(Action beforeEmit, IReadOnlyList<FileChangeEvent> changes, CancellationTokenSource stop) : IFileWatcherService
    {
        public async IAsyncEnumerable<FileChangeEvent> WatchAsync(LibraryDefinition library, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            beforeEmit();
            foreach (var change in changes)
            {
                yield return change;
            }

            stop.Cancel();
        }
    }

    private sealed class NoopWatcher : IFileWatcherService
    {
        public async IAsyncEnumerable<FileChangeEvent> WatchAsync(LibraryDefinition library, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class NoopMetadata : IMetadataExtractionService
    {
        public Task<MediaMetadata> ExtractAsync(string filePath, CancellationToken cancellationToken) =>
            Task.FromResult(new MediaMetadata());
    }

    private sealed class RecordingProgress : IProgress<ScanProgress>
    {
        public List<ScanProgress> Reports { get; } = [];

        public void Report(ScanProgress value)
        {
            lock (Reports)
            {
                Reports.Add(value);
            }
        }
    }

    private sealed class FakeMediaFileRepository : IMediaFileRepository
    {
        public List<MediaFile> Upserted { get; } = [];

        public List<int> UpsertBatchSizes { get; } = [];

        public int DeleteCount { get; private set; }

        public List<string> StoredPaths { get; } = [];

        public List<string> DeletedPaths { get; } = [];

        public Task<MediaFile?> GetByIdAsync(long id, CancellationToken cancellationToken) => Task.FromResult<MediaFile?>(null);

        public Task<MediaFile?> GetByPathAsync(string path, CancellationToken cancellationToken) => Task.FromResult<MediaFile?>(null);

        public Task<IReadOnlyList<MediaFile>> GetPageAsync(long? folderId, long afterId, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MediaFile>>([]);

        public Task UpsertBatchAsync(IReadOnlyList<MediaFile> mediaFiles, CancellationToken cancellationToken)
        {
            lock (Upserted)
            {
                Upserted.AddRange(mediaFiles);
                UpsertBatchSizes.Add(mediaFiles.Count);
            }

            return Task.CompletedTask;
        }

        public Task UpdateThumbnailPathAsync(long mediaFileId, string? thumbnailPath, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteByPathAsync(string path, CancellationToken cancellationToken)
        {
            DeleteCount++;
            DeletedPaths.Add(path);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetPathsUnderRootAsync(string rootPath, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(StoredPaths.ToList());

        public Task<IReadOnlyDictionary<string, MediaFileSignature>> GetSignaturesUnderRootAsync(string rootPath, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, MediaFileSignature>>(
                StoredPaths.ToDictionary(path => path, _ => new MediaFileSignature(-1, null, null), StringComparer.OrdinalIgnoreCase));
    }
}
