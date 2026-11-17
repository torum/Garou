using Garou.Application.Libraries;
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
    public async Task WhenLibraryContainsApplicationCacheThenScansAndEventsIgnoreIt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"storage-filter-{Guid.NewGuid():N}");
        var cache = Path.Combine(root, "cache", "thumbnails");
        var cacheFolder = Path.Combine(cache, "generated");
        Directory.CreateDirectory(cacheFolder);
        var mediaPath = Path.Combine(root, "photo.jpg");
        var thumbnailPath = Path.Combine(cacheFolder, "thumbnail.jpg");
        await File.WriteAllBytesAsync(mediaPath, [1, 2, 3], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(thumbnailPath, [1, 2, 3], TestContext.Current.CancellationToken);
        var databasePath = Path.Combine(root, "library.db");
        var filter = new ApplicationStorageFilter(cache, databasePath, Path.Combine(root, "Garou.config"));
        var repository = new FakeMediaFileRepository();
        repository.StoredPaths.Add(thumbnailPath);
        var library = new LibraryDefinition { RootPath = root, IsEnabled = true };
        using var stop = new CancellationTokenSource();
        var watcher = new ScriptedWatcher(() => { },
        [
            new FileChangeEvent { LibraryRoot = root, FullPath = cacheFolder, Kind = FileChangeKind.Created },
            new FileChangeEvent { LibraryRoot = root, FullPath = thumbnailPath, Kind = FileChangeKind.Created },
            new FileChangeEvent { LibraryRoot = root, FullPath = databasePath + "-wal", Kind = FileChangeKind.Modified }
        ], stop);
        var service = CreateService(repository, watcher, library, CreateSettingsSession(".jpg"));
        service.StorageFilter = filter;
        var scans = 0;
        service.AutomaticScanStarted += _ => scans++;

        try
        {
            await service.RunAsync(stop.Token);
            Assert.Equal(1, scans);
            Assert.Equal(mediaPath, Assert.Single(repository.Upserted).Path);
            Assert.Equal([thumbnailPath], repository.DeletedPaths);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WhenFileMovesAcrossApplicationCacheBoundaryThenChangeIsConverted()
    {
        var root = Path.Combine(Path.GetTempPath(), "storage-filter-test");
        var cache = Path.Combine(root, "cache");
        var filter = new ApplicationStorageFilter(cache, Path.Combine(root, "library.db"), Path.Combine(root, "Garou.config"));
        var outside = Path.Combine(root, "photo.jpg");
        var inside = Path.Combine(cache, "photo.jpg");
        var intoCache = filter.Filter(new FileChangeEvent { LibraryRoot = root, FullPath = inside, OldFullPath = outside, Kind = FileChangeKind.Renamed });
        var outOfCache = filter.Filter(new FileChangeEvent { LibraryRoot = root, FullPath = outside, OldFullPath = inside, Kind = FileChangeKind.Renamed });

        Assert.NotNull(intoCache);
        Assert.Equal(FileChangeKind.Deleted, intoCache.Kind);
        Assert.Equal(outside, intoCache.FullPath);
        Assert.NotNull(outOfCache);
        Assert.Equal(FileChangeKind.Created, outOfCache.Kind);
        Assert.Equal(outside, outOfCache.FullPath);
        Assert.False(filter.IsIgnored(Path.Combine(root, "cache-other", "photo.jpg")));
        Assert.True(filter.IsIgnored(Path.Combine(root, "library.db-wal")));
        Assert.False(filter.IsIgnored(Path.Combine(root, "library.db-wal-backup")));
        Assert.Null(filter.Filter(new FileChangeEvent { LibraryRoot = root, FullPath = inside, Kind = FileChangeKind.Created }));
    }

    [Fact]
    public async Task WhenRealWatcherSeesCacheChangesThenItStillReportsLibraryChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), $"storage-watcher-{Guid.NewGuid():N}");
        var cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(cache);
        var watcher = new FileWatcherService
        {
            StorageFilter = new ApplicationStorageFilter(cache, Path.Combine(root, "library.db"), Path.Combine(root, "Garou.config"))
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var library = new LibraryDefinition { RootPath = root };

        try
        {
            await using var events = watcher.WatchAsync(library, stop.Token).GetAsyncEnumerator(TestContext.Current.CancellationToken);
            Assert.True(await events.MoveNextAsync());
            Assert.True(events.Current.WatcherReady);
            var next = events.MoveNextAsync().AsTask();
            var generated = Path.Combine(cache, "generated");
            Directory.CreateDirectory(generated);
            await File.WriteAllBytesAsync(Path.Combine(generated, "thumbnail.jpg"), [1, 2, 3], stop.Token);
            var mediaPath = Path.Combine(root, "photo.jpg");
            await File.WriteAllBytesAsync(mediaPath, [1, 2, 3], stop.Token);

            Assert.True(await next);
            Assert.Equal(mediaPath, events.Current.FullPath);
            Assert.False(events.Current.RequiresFullRescan);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WhenWatcherSetupFailsThenOnlyRecoveryTriggersAnotherScan()
    {
        var root = Path.Combine(Path.GetTempPath(), $"watcher-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var lateFile = Path.Combine(root, "late.jpg");
        using var stop = new CancellationTokenSource();
        var watcher = new RetryingWatcher(lateFile, stop);
        var repository = new FakeMediaFileRepository();
        var service = CreateService(repository, watcher, new LibraryDefinition { RootPath = root, IsEnabled = true }, CreateSettingsSession(".jpg"));
        service.RetryInterval = TimeSpan.Zero;
        var scans = 0;
        var issues = 0;
        service.AutomaticScanStarted += _ => scans++;
        service.IndexingIssue += (_, _) => issues++;

        try
        {
            await service.RunAsync(stop.Token);
            Assert.Equal(3, watcher.Attempts);
            Assert.Equal(2, issues);
            Assert.Equal(2, scans);
            Assert.Equal(lateFile, Assert.Single(repository.Upserted).Path);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WhenWatcherStopsUnexpectedlyThenIndexerDoesNotImmediatelyRescan()
    {
        var root = Path.Combine(Path.GetTempPath(), $"watcher-stopped-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var stop = new CancellationTokenSource();
        var service = CreateService(new FakeMediaFileRepository(), new NoopWatcher(), new LibraryDefinition { RootPath = root, IsEnabled = true });
        var scans = 0;
        var issues = 0;
        service.AutomaticScanStarted += _ => scans++;
        service.IndexingIssue += (_, _) => { issues++; stop.Cancel(); };

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync(stop.Token));
            Assert.Equal(1, scans);
            Assert.Equal(1, issues);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WhenRemovalHoldsOperationGateThenIndexerScanWaits()
    {
        var gate = new LibraryOperationGate();
        var operation = await gate.EnterAsync(CancellationToken.None);
        var library = new LibraryDefinition
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}")
        };
        var scan = CreateScanService(new FakeMediaFileRepository(), operationGate: gate)
            .ScanAsync(library, null, CancellationToken.None);

        try
        {
            Assert.False(scan.IsCompleted);
        }
        finally
        {
            operation.Dispose();
        }

        await scan;
        Assert.False(library.IsOnline);
    }

    [Fact]
    public async Task WhenWatcherEventTargetsSuppressedFolderThenFileIsNotIndexed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"suppressed-folder-{Guid.NewGuid():N}");
        var nested = Path.Combine(root, "nested");
        Directory.CreateDirectory(nested);
        var path = Path.Combine(nested, "photo.jpg");
        await File.WriteAllBytesAsync(path, [1, 2, 3], cancellationToken);
        var operationGate = new LibraryOperationGate();
        operationGate.SuppressPath(nested);
        var repository = new FakeMediaFileRepository();

        try
        {
            var service = CreateScanService(repository, settingsSession: CreateSettingsSession(".jpg"), operationGate: operationGate);
            var library = new LibraryDefinition { RootPath = root };
            await service.HandleAsync(
                library,
                new FileChangeEvent { LibraryRoot = root, FullPath = path, Kind = FileChangeKind.Created },
                cancellationToken);
            await service.HandleAsync(
                library,
                new FileChangeEvent { LibraryRoot = root, FullPath = root, RequiresFullRescan = true },
                cancellationToken);

            Assert.Empty(repository.Upserted);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WhenWatcherEventTargetsPersistentlyExcludedFolderThenFileIsNotIndexed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"persistently-excluded-{Guid.NewGuid():N}");
        var excluded = Path.Combine(root, "excluded");
        Directory.CreateDirectory(excluded);
        var path = Path.Combine(excluded, "photo.jpg");
        await File.WriteAllBytesAsync(path, [1, 2, 3], cancellationToken);
        var repository = new FakeMediaFileRepository();
        var library = new LibraryDefinition { RootPath = root };
        var excludedFolders = new FakeLibraryExcludedFolderRepository();
        await excludedFolders.AddAsync(library.Id, excluded, cancellationToken);
        using var stop = new CancellationTokenSource();
        var watcher = new ScriptedWatcher(
            () => { },
            [new FileChangeEvent { LibraryRoot = root, FullPath = path, Kind = FileChangeKind.Created }],
            stop);

        try
        {
            await CreateService(repository, watcher, library, excludedFolders: excludedFolders).RunAsync(stop.Token);

            Assert.Empty(repository.Upserted);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WhenFullScanRunsThenSuppressedFolderCanBeIndexedAgain()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"rescan-suppressed-folder-{Guid.NewGuid():N}");
        var nested = Path.Combine(root, "nested");
        Directory.CreateDirectory(nested);
        await File.WriteAllBytesAsync(Path.Combine(nested, "photo.jpg"), [1, 2, 3], cancellationToken);
        var operationGate = new LibraryOperationGate();
        operationGate.SuppressPath(nested);
        var repository = new FakeMediaFileRepository();

        try
        {
            await CreateScanService(repository, settingsSession: CreateSettingsSession(".jpg"), operationGate: operationGate)
                .ScanAsync(new LibraryDefinition { RootPath = root }, null, cancellationToken);

            Assert.Single(repository.Upserted);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WhenInitialScanRunsThenPersistentlyExcludedSubtreesAreNotIndexed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"scan-excluded-{Guid.NewGuid():N}");
        var excluded = Path.Combine(root, "excluded");
        Directory.CreateDirectory(excluded);
        var includedFile = Path.Combine(root, "photo.jpg");
        var excludedFile = Path.Combine(excluded, "clip.mp4");
        await File.WriteAllBytesAsync(includedFile, [1, 2, 3], cancellationToken);
        await File.WriteAllBytesAsync(excludedFile, [1, 2, 3], cancellationToken);
        var repository = new FakeMediaFileRepository();
        var folders = new FakeFolderRepository();
        var library = new LibraryDefinition { RootPath = root };
        var excludedFolders = new FakeLibraryExcludedFolderRepository();
        await excludedFolders.AddAsync(library.Id, excluded, cancellationToken);

        try
        {
            await CreateScanService(repository, folders: folders, excludedFolders: excludedFolders)
                .ScanAsync(library, null, cancellationToken);

            Assert.Equal(includedFile, Assert.Single(repository.Upserted).Path);
            Assert.DoesNotContain(await folders.GetAllAsync(cancellationToken), folder => folder.Path == excluded);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WhenExcludedFolderIsRestoredThenItsMediaIsIndexedButExcludedChildrenRemainExcluded()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"restore-scan-{Guid.NewGuid():N}");
        var excluded = Path.Combine(root, "excluded");
        var child = Path.Combine(excluded, "child");
        Directory.CreateDirectory(child);
        var restoredFile = Path.Combine(excluded, "photo.jpg");
        var excludedFile = Path.Combine(child, "hidden.jpg");
        await File.WriteAllBytesAsync(restoredFile, [1, 2, 3], cancellationToken);
        await File.WriteAllBytesAsync(excludedFile, [1, 2, 3], cancellationToken);
        var library = new LibraryDefinition { RootPath = root };
        var exclusions = new FakeLibraryExcludedFolderRepository();
        await exclusions.AddAsync(library.Id, excluded, cancellationToken);
        await exclusions.AddAsync(library.Id, child, cancellationToken);
        var media = new FakeMediaFileRepository();
        var gate = new LibraryOperationGate();
        gate.SuppressPath(excluded);
        var scanner = CreateScanService(media, settingsSession: CreateSettingsSession(".jpg"), operationGate: gate, excludedFolders: exclusions);
        var commands = new LibraryCommands(new ScriptedLibraryRepository(library), exclusions, scanner);

        try
        {
            await scanner.ScanAsync(library, null, cancellationToken);
            Assert.Empty(media.Upserted);

            await commands.RestoreExcludedFolderAsync(excluded, null, cancellationToken);

            Assert.Equal(restoredFile, Assert.Single(media.Upserted).Path);
            Assert.Equal(child, Assert.Single(await exclusions.GetPathsAsync(library.Id, cancellationToken)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

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
    public async Task WhenAutomaticScansRunThenLifecycleAndProgressAreReported()
    {
        var root = Path.Combine(Path.GetTempPath(), $"automatic-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var stop = new CancellationTokenSource();
        var library = new LibraryDefinition { RootPath = root, IsEnabled = true };
        var watcher = new ScriptedWatcher(() => { },
            [new FileChangeEvent { LibraryRoot = root, FullPath = root, RequiresFullRescan = true }], stop);
        var service = CreateService(new FakeMediaFileRepository(), watcher, library);
        var events = new List<string>();
        service.AutomaticScanStarted += path => events.Add($"start:{path}");
        service.AutomaticScanProgress += progress => events.Add(progress.Phase);
        service.AutomaticScanFinished += (path, online, error) => events.Add($"finish:{path}:{online}:{error is null}");

        try
        {
            await service.RunAsync(stop.Token);
            Assert.Equal(2, events.Count(value => value == $"start:{root}"));
            Assert.Equal(2, events.Count(value => value == "ScanEnumerating"));
            Assert.Equal(2, events.Count(value => value == $"finish:{root}:True:True"));
            Assert.Equal($"start:{root}", events[0]);
            Assert.Equal($"finish:{root}:True:True", events[^1]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WhenWatcherReportsRepeatedDirectoryModificationsThenLibraryIsNotRescanned()
    {
        var root = Path.Combine(Path.GetTempPath(), $"directory-modifications-{Guid.NewGuid():N}");
        var directory = Path.Combine(root, "existing");
        Directory.CreateDirectory(directory);
        using var stop = new CancellationTokenSource();
        var library = new LibraryDefinition { RootPath = root, IsEnabled = true };
        var watcher = new ScriptedWatcher(() => { },
        [
            new FileChangeEvent { LibraryRoot = root, FullPath = directory, Kind = FileChangeKind.Modified },
            new FileChangeEvent { LibraryRoot = root, FullPath = directory, Kind = FileChangeKind.Modified }
        ], stop);
        var service = CreateService(new FakeMediaFileRepository(), watcher, library);
        var scans = 0;
        service.AutomaticScanStarted += _ => scans++;

        try
        {
            await service.RunAsync(stop.Token);

            Assert.Equal(1, scans);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WhenWatcherReportsNewDirectoryThenExistingFilesInItAreIndexed()
    {
        var root = Path.Combine(Path.GetTempPath(), $"directory-created-{Guid.NewGuid():N}");
        var directory = Path.Combine(root, "new");
        var file = Path.Combine(directory, "photo.jpg");
        Directory.CreateDirectory(root);
        using var stop = new CancellationTokenSource();
        var library = new LibraryDefinition { RootPath = root, IsEnabled = true };
        var watcher = new ScriptedWatcher(() =>
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(file, [1, 2, 3]);
        }, [new FileChangeEvent { LibraryRoot = root, FullPath = directory, Kind = FileChangeKind.Created }], stop);
        var repository = new FakeMediaFileRepository();
        var service = CreateService(repository, watcher, library);
        var scans = 0;
        service.AutomaticScanStarted += _ => scans++;

        try
        {
            await service.RunAsync(stop.Token);

            Assert.Equal(2, scans);
            Assert.Equal(file, Assert.Single(repository.Upserted).Path);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WhenAutomaticLibraryIsUnavailableThenCompletionReportsOffline()
    {
        var root = Path.Combine(Path.GetTempPath(), $"missing-automatic-{Guid.NewGuid():N}");
        using var stop = new CancellationTokenSource();
        var library = new LibraryDefinition { RootPath = root, IsEnabled = true };
        var service = CreateService(new FakeMediaFileRepository(), new NoopWatcher(), library);
        (string Path, bool Online, Exception? Error)? result = null;
        service.AutomaticScanFinished += (path, online, error) =>
        {
            result = (path, online, error);
            stop.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync(stop.Token));
        Assert.NotNull(result);
        Assert.Equal(root, result.Value.Path);
        Assert.False(result.Value.Online);
        Assert.Null(result.Value.Error);
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
            Assert.Equal("ScanEnumerating", progress.Reports[0].Phase);
            Assert.Contains(progress.Reports, report => report.Phase == "ScanReadingIndex");
            Assert.Contains(progress.Reports, report => report.Phase == "ScanPreparingFolders");
            Assert.Contains(progress.Reports, report => report.Phase == "ScanCheckingFiles");
            Assert.Contains(progress.Reports, report => report.Phase == "ScanIndexingMedia" && report.Processed == 405 && report.Total == 405);
            Assert.Equal("ScanRemovingStale", progress.Reports[^1].Phase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WhenFolderEnumerationIsDeniedThenReadableFoldersAreScannedAndUnreadableRecordsArePreserved()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"unreadable-scan-{Guid.NewGuid():N}");
        var unreadable = Path.Combine(root, "unreadable");
        var readable = Path.Combine(root, "readable");
        Directory.CreateDirectory(unreadable);
        Directory.CreateDirectory(readable);
        var readableFile = Path.Combine(readable, "photo.jpg");
        var retainedFile = Path.Combine(unreadable, "nested", "retained.jpg");
        var staleFile = Path.Combine(readable, "removed.jpg");
        var siblingStaleFile = Path.Combine(root, "unreadable-other", "removed.jpg");
        var partialEntry = Path.Combine(unreadable, "notes.txt");
        await File.WriteAllBytesAsync(readableFile, [1, 2, 3], cancellationToken);
        await File.WriteAllTextAsync(partialEntry, "not media", cancellationToken);
        var repository = new FakeMediaFileRepository();
        repository.StoredPaths.AddRange([retainedFile, staleFile, siblingStaleFile]);
        var service = CreateScanService(repository);
        service.EnumerateDirectoryEntries = path => path == unreadable
            ? DeniedEntries(partialEntry)
            : Directory.EnumerateFileSystemEntries(path);

        try
        {
            await service.ScanAsync(new LibraryDefinition { RootPath = root }, null, cancellationToken);

            Assert.Equal(readableFile, Assert.Single(repository.Upserted).Path);
            Assert.Equal([staleFile, siblingStaleFile], repository.DeletedPaths);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        static IEnumerable<string> DeniedEntries(string partialEntry)
        {
            yield return partialEntry;
            throw new UnauthorizedAccessException("Permission denied");
        }
    }

    [Theory]
    [InlineData("lost+found")]
    [InlineData("System Volume Information")]
    [InlineData("private")]
    public async Task WhenFolderEnumerationIsDeniedThenNoIssueIsReportedAndRecordsArePreserved(string folderName)
    {
        var root = Path.Combine(Path.GetTempPath(), $"denied-scan-{Guid.NewGuid():N}");
        var deniedFolder = Path.Combine(root, folderName);
        Directory.CreateDirectory(deniedFolder);
        var retainedFile = Path.Combine(deniedFolder, "retained.jpg");
        var repository = new FakeMediaFileRepository();
        repository.StoredPaths.Add(retainedFile);
        var service = CreateScanService(repository);
        service.EnumerateDirectoryEntries = path => path == deniedFolder
            ? throw new UnauthorizedAccessException("Permission denied")
            : Directory.EnumerateFileSystemEntries(path);
        var issues = new List<(string Path, int Count, string Example)>();
        service.ItemsSkipped += (path, count, example) => issues.Add((path, count, example));

        try
        {
            await service.ScanAsync(new LibraryDefinition { RootPath = root }, null, TestContext.Current.CancellationToken);

            Assert.Empty(issues);
            Assert.Empty(repository.DeletedPaths);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WhenFoldersHaveDifferentEnumerationFailuresThenOnlyIoFailuresAreReported()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mixed-unreadable-scan-{Guid.NewGuid():N}");
        var denied = Path.Combine(root, "private");
        var other = Path.Combine(root, "other");
        Directory.CreateDirectory(denied);
        Directory.CreateDirectory(other);
        var repository = new FakeMediaFileRepository();
        var service = CreateScanService(repository);
        service.EnumerateDirectoryEntries = path => path == denied
            ? throw new UnauthorizedAccessException("Permission denied")
            : path == other
                ? throw new IOException("I/O error")
                : Directory.EnumerateFileSystemEntries(path);
        var issues = new List<(string Path, int Count, string Example)>();
        service.ItemsSkipped += (path, count, example) => issues.Add((path, count, example));

        try
        {
            await service.ScanAsync(new LibraryDefinition { RootPath = root }, null, TestContext.Current.CancellationToken);

            Assert.Equal((root, 1, other), Assert.Single(issues));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WhenRootEnumerationIsDeniedThenLibraryIsOfflineAndRecordsArePreserved()
    {
        var root = Path.Combine(Path.GetTempPath(), $"unreadable-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var repository = new FakeMediaFileRepository();
        repository.StoredPaths.Add(Path.Combine(root, "retained.jpg"));
        var service = CreateScanService(repository);
        service.EnumerateDirectoryEntries = _ => throw new UnauthorizedAccessException("Permission denied");
        var library = new LibraryDefinition { RootPath = root };

        try
        {
            await service.ScanAsync(library, null, TestContext.Current.CancellationToken);

            Assert.False(library.IsOnline);
            Assert.Empty(repository.DeletedPaths);
            Assert.Empty(repository.Upserted);
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
    public async Task WhenWatcherReportsRenameOfUnknownFileThenNewPathIsIndexed()
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

            Assert.Empty(repository.DeletedPaths);
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
        AppSettingsSession? settingsSession = null,
        FakeLibraryExcludedFolderRepository? excludedFolders = null)
    {
        return new MediaIndexerService(
            watcher,
            new ScriptedLibraryRepository(library),
            repository,
            new FakeFolderRepository(),
            excludedFolders ?? new FakeLibraryExcludedFolderRepository(),
            new LibraryOperationGate(),
            settingsSession ?? new AppSettingsSession(),
            TimeProvider.System);
    }

    private static MediaIndexerService CreateScanService(
        FakeMediaFileRepository repository,
        FakeFolderRepository? folders = null,
        AppSettingsSession? settingsSession = null,
        LibraryOperationGate? operationGate = null,
        FakeLibraryExcludedFolderRepository? excludedFolders = null)
    {
        return new MediaIndexerService(
            new NoopWatcher(),
            new EmptyLibraryRepository(),
            repository,
            folders ?? new FakeFolderRepository(),
            excludedFolders ?? new FakeLibraryExcludedFolderRepository(),
            operationGate ?? new LibraryOperationGate(),
            settingsSession ?? new AppSettingsSession(),
            TimeProvider.System);
    }

    private sealed class FakeLibraryExcludedFolderRepository : ILibraryExcludedFolderRepository
    {
        private readonly Dictionary<Guid, List<string>> _paths = [];

        public Task<IReadOnlyList<string>> GetPathsAsync(Guid libraryId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(_paths.GetValueOrDefault(libraryId, []));

        public Task AddAsync(Guid libraryId, string path, CancellationToken cancellationToken)
        {
            if (!_paths.TryGetValue(libraryId, out var paths))
            {
                paths = [];
                _paths.Add(libraryId, paths);
            }

            paths.Add(path);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(Guid libraryId, string path, CancellationToken cancellationToken)
        {
            if (_paths.TryGetValue(libraryId, out var paths))
            {
                paths.RemoveAll(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
            }

            return Task.CompletedTask;
        }
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

    private sealed class RetryingWatcher(string lateFile, CancellationTokenSource stop) : IFileWatcherService
    {
        public int Attempts { get; private set; }

        public async IAsyncEnumerable<FileChangeEvent> WatchAsync(LibraryDefinition library, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Attempts++;
            if (Attempts < 3)
                throw new IOException("watcher unavailable");

            await File.WriteAllBytesAsync(lateFile, [1, 2, 3], cancellationToken);
            yield return new FileChangeEvent { LibraryRoot = library.RootPath, FullPath = library.RootPath, WatcherReady = true };
            stop.Cancel();
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

        public Task<bool> MoveAsync(string oldPath, MediaFile updatedFile, CancellationToken cancellationToken) => Task.FromResult(false);

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
