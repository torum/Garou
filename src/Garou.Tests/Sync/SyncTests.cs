using Garou.Domain.Abstractions;
using Garou.Domain.Entities;
using Garou.Domain.Enums;
using Garou.Domain.Models;
using Garou.Infrastructure.Settings;
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
        var library = new LibraryDefinition { RootPath = root };

        try
        {
            await CreateScanService(repository).ScanAsync(library, null, CancellationToken.None);

            Assert.Equal(2, repository.Upserted.Count);
            Assert.DoesNotContain(repository.Upserted, file => file.FileName == "notes.txt");
            Assert.All(repository.Upserted, file => Assert.NotEqual(Guid.Empty, file.Id));
            Assert.All(repository.Upserted, file => Assert.Null(file.ThumbnailPath));
            Assert.True(library.IsOnline);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WhenFlvFileIsScannedThenItIsIndexedAsVideo()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"flv-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(Path.Combine(root, "clip.flv"), [1, 2, 3], cancellationToken);
        var repository = new FakeMediaFileRepository();

        try
        {
            await CreateScanService(repository).ScanAsync(new LibraryDefinition { RootPath = root }, null, cancellationToken);

            Assert.Equal(MediaType.Video, Assert.Single(repository.Upserted).MediaType);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WhenSubfolderScanningIsDisabledThenNestedFilesAreRemovedFromTheIndex()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"no-subfolder-scan-{Guid.NewGuid():N}");
        var nested = Path.Combine(root, "nested");
        Directory.CreateDirectory(nested);
        var rootFile = Path.Combine(root, "root.jpg");
        var nestedFile = Path.Combine(nested, "nested.jpg");
        await File.WriteAllBytesAsync(rootFile, [1, 2, 3], cancellationToken);
        await File.WriteAllBytesAsync(nestedFile, [1, 2, 3], cancellationToken);
        var repository = new FakeMediaFileRepository();
        repository.StoredPaths.Add(nestedFile);
        var settingsSession = new AppSettingsSession();
        settingsSession.Initialize(new AppSettings { ScanSubfolders = false });

        try
        {
            await CreateScanService(repository, settingsSession: settingsSession)
                .ScanAsync(new LibraryDefinition { RootPath = root }, null, cancellationToken);

            Assert.Equal(rootFile, Assert.Single(repository.Upserted).Path);
            Assert.Equal([nestedFile], repository.DeletedPaths);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WhenExtensionAllowListIsConfiguredThenOnlyIncludedExtensionsRemainIndexed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"extension-filter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var includedPath = Path.Combine(root, "clip.mp4");
        var excludedPath = Path.Combine(root, "photo.jpg");
        await File.WriteAllBytesAsync(includedPath, [1, 2, 3], cancellationToken);
        await File.WriteAllBytesAsync(excludedPath, [1, 2, 3], cancellationToken);
        var repository = new FakeMediaFileRepository();
        repository.StoredPaths.Add(excludedPath);
        var settingsSession = CreateSettingsSession(".mp4");

        try
        {
            await CreateScanService(repository, settingsSession: settingsSession)
                .ScanAsync(new LibraryDefinition { RootPath = root }, null, cancellationToken);

            Assert.Equal(includedPath, Assert.Single(repository.Upserted).Path);
            Assert.Equal([excludedPath], repository.DeletedPaths);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WhenSubfolderScanningIsDisabledThenNestedWatcherFilesAreNotIndexed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"watcher-no-subfolder-scan-{Guid.NewGuid():N}");
        var nested = Path.Combine(root, "nested");
        Directory.CreateDirectory(nested);
        var nestedFile = Path.Combine(nested, "clip.mp4");
        await File.WriteAllBytesAsync(nestedFile, [1, 2, 3], cancellationToken);
        var repository = new FakeMediaFileRepository();
        var library = new LibraryDefinition { RootPath = root, IsEnabled = true };
        var settingsSession = new AppSettingsSession();
        settingsSession.Initialize(new AppSettings { ScanSubfolders = false });
        using var stop = new CancellationTokenSource();
        var watcher = new ScriptedWatcher(
            () => { },
            [new FileChangeEvent { LibraryRoot = root, FullPath = nestedFile, Kind = FileChangeKind.Created }],
            stop);

        try
        {
            await CreateService(repository, watcher, library, settingsSession).RunAsync(stop.Token);

            Assert.Empty(repository.Upserted);
            Assert.Equal([nestedFile], repository.DeletedPaths);
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
                .ScanAsync(new LibraryDefinition { RootPath = root }, null, cancellationToken);

            Assert.Equal(root, Assert.Single(await folders.GetAllAsync(cancellationToken)).Path);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WhenWatcherReportsExcludedExtensionThenFileIsNotIndexed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"watcher-extension-filter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var excludedPath = Path.Combine(root, "notes.txt");
        await File.WriteAllTextAsync(excludedPath, "not media", cancellationToken);
        var repository = new FakeMediaFileRepository();
        var library = new LibraryDefinition { RootPath = root, IsEnabled = true };
        using var stop = new CancellationTokenSource();
        var watcher = new ScriptedWatcher(
            () => { },
            [new FileChangeEvent { LibraryRoot = root, FullPath = excludedPath, Kind = FileChangeKind.Created }],
            stop);

        try
        {
            await CreateService(repository, watcher, library, CreateSettingsSession(".jpg")).RunAsync(stop.Token);

            Assert.Empty(repository.Upserted);
            Assert.Equal([excludedPath], repository.DeletedPaths);
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
            await CreateScanService(repository).ScanAsync(new LibraryDefinition { RootPath = root }, null, cancellationToken);

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
                new LibraryDefinition { RootPath = root },
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
            await CreateScanService(repository).ScanAsync(new LibraryDefinition { RootPath = root }, null, CancellationToken.None);

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
            await CreateScanService(repository).ScanAsync(new LibraryDefinition { RootPath = root }, null, CancellationToken.None);

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
            await CreateScanService(repository).ScanAsync(new LibraryDefinition { RootPath = root }, null, CancellationToken.None);

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
        var library = new LibraryDefinition { RootPath = root, IsEnabled = true };
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
        var library = new LibraryDefinition { RootPath = root, IsEnabled = true };
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

    private static MediaIndexerService CreateService(
        FakeMediaFileRepository repository,
        IFileWatcherService watcher,
        LibraryDefinition library,
        AppSettingsSession? settingsSession = null)
    {
        return new MediaIndexerService(
            watcher,
            new ScriptedLibraryRepository(library),
            repository,
            new FakeFolderRepository(),
            settingsSession ?? new AppSettingsSession(),
            TimeProvider.System);
    }

    private static MediaIndexerService CreateScanService(
        FakeMediaFileRepository repository,
        FakeFolderRepository? folders = null,
        AppSettingsSession? settingsSession = null)
    {
        return new MediaIndexerService(
            new NoopWatcher(),
            new EmptyLibraryRepository(),
            repository,
            folders ?? new FakeFolderRepository(),
            settingsSession ?? new AppSettingsSession(),
            TimeProvider.System);
    }

    private static AppSettingsSession CreateSettingsSession(params string[] includedExtensions)
    {
        var settingsSession = new AppSettingsSession();
        settingsSession.Initialize(new AppSettings { IncludedExtensions = [.. includedExtensions] });
        return settingsSession;
    }

    private sealed class FakeFolderRepository : IFolderRepository
    {
        private readonly List<Folder> _folders = [];

        public Task<Folder?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(_folders.FirstOrDefault(folder => folder.Id == id));

        public Task<Folder?> GetByPathAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(_folders.FirstOrDefault(folder => string.Equals(folder.Path, path, StringComparison.OrdinalIgnoreCase)));

        public Task<IReadOnlyList<Folder>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Folder>>(_folders.ToList());

        public Task<Guid> AddAsync(Folder folder, CancellationToken cancellationToken)
        {
            var id = Guid.CreateVersion7();
            _folders.Add(new Folder { Id = id, Path = folder.Path, ParentId = folder.ParentId });
            return Task.FromResult(id);
        }

        public Task DeleteSubtreeAsync(Guid folderId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class EmptyLibraryRepository : ILibraryRepository
    {
        public Task<IReadOnlyList<LibraryDefinition>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LibraryDefinition>>([]);

        public Task AddAsync(LibraryDefinition library, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveAsync(Guid id, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetEnabledAsync(Guid id, bool isEnabled, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ScriptedLibraryRepository(LibraryDefinition library) : ILibraryRepository
    {
        public Task<IReadOnlyList<LibraryDefinition>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LibraryDefinition>>([library]);

        public Task AddAsync(LibraryDefinition library, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveAsync(Guid id, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetEnabledAsync(Guid id, bool isEnabled, CancellationToken cancellationToken) => Task.CompletedTask;
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

        public Task<MediaFile?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<MediaFile?>(null);

        public Task<MediaFile?> GetByPathAsync(string path, CancellationToken cancellationToken) => Task.FromResult<MediaFile?>(null);

        public Task<IReadOnlyList<MediaFile>> GetPageAsync(Guid? folderId, Guid afterId, int pageSize, CancellationToken cancellationToken) =>
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

        public Task UpdateThumbnailPathAsync(Guid mediaFileId, string? thumbnailPath, CancellationToken cancellationToken) => Task.CompletedTask;

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
