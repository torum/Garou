using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Garou.Application.Libraries;
using Garou.Application.Queries;
using Garou.Domain.Abstractions;
using Garou.Domain.Entities;
using Garou.Domain.Models;
using Garou.Infrastructure.Metadata;
using Garou.Infrastructure.Persistence;
using Garou.Infrastructure.Persistence.Repositories;
using Garou.Infrastructure.Settings;
using Garou.Infrastructure.Sync;
using Garou.ViewModels;
using Microsoft.Data.Sqlite;

namespace Garou.Tests.Queries;

[Collection("Avalonia UI")]
public sealed class ScanRefreshMessageTests
{
    [AvaloniaFact]
    public async Task OfflineRetriesAndRecoveryDoNotShowScanInfoBar()
    {
        var root = Path.Combine(Path.GetTempPath(), $"Garou-offline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var unavailablePath = Path.Combine(root, "offline-library");
        var factory = new SqliteConnectionFactory(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "library.db") }.ToString());
        using var stop = new CancellationTokenSource();
        Task? automaticScan = null;
        try
        {
            var ct = TestContext.Current.CancellationToken;
            await new DatabaseInitializer(factory).InitializeAsync(ct);
            var folders = new FolderRepository(factory);
            var libraries = new LibraryRepository(factory);
            await libraries.AddAsync(new LibraryDefinition { RootPath = unavailablePath, IsEnabled = true }, ct);
            var excluded = new LibraryExcludedFolderRepository(factory);
            var media = new MediaFileRepository(factory);
            var session = new AppSettingsSession();
            session.Initialize(new AppSettings());
            var gate = new LibraryOperationGate();
            var indexer = new MediaIndexerService(new FileWatcherService(), libraries, media, folders, excluded, gate, session, TimeProvider.System)
            {
                RetryInterval = TimeSpan.FromMilliseconds(50)
            };
            var gatedFolders = new GatedFolders(folders);
            using var viewModel = new MainViewModel(new MediaQueryService(media, media, gatedFolders),
                new LibraryCommands(libraries, excluded, indexer), gate, new SettingsViewModel(session), session,
                null!, media, folders, null!, new XnfoIndexService(new XnfoStore(), factory), indexer);
            await viewModel.InitializeAsync(ct);

            var automaticFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var retriesFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var retryCount = 0;
            var infoBarOpens = 0;
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.IsScanning) && !viewModel.IsScanning)
                    automaticFinished.TrySetResult();
                if (args.PropertyName == nameof(MainViewModel.IsScanInfoOpen) && viewModel.IsScanInfoOpen)
                    infoBarOpens++;
            };
            indexer.AutomaticScanFinished += (_, online, _, isRetry) =>
            {
                if (isRetry && !online && Interlocked.Increment(ref retryCount) >= 2)
                    retriesFinished.TrySetResult();
            };
            automaticScan = Task.Run(() => indexer.RunAsync(stop.Token));
            await automaticFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, infoBarOpens);
            await retriesFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Dispatcher.UIThread.InvokeAsync(() => { });

            Assert.Equal(1, infoBarOpens);
            Assert.False(viewModel.IsScanInfoOpen);
            Assert.False(viewModel.IsInfoBarOpen);
            Assert.Empty(viewModel.InfoBarErrorMessage);

            var folderRestored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            viewModel.FolderRestored += _ => folderRestored.TrySetResult();
            gatedFolders.BlockNext();
            Directory.CreateDirectory(unavailablePath);
            await gatedFolders.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, infoBarOpens);
            Assert.False(viewModel.IsScanInfoOpen);
            Assert.False(viewModel.IsScanning);
            gatedFolders.Release.TrySetResult();
            await folderRestored.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(unavailablePath, Assert.Single(viewModel.FolderTree).PathText);
        }
        finally
        {
            stop.Cancel();
            if (automaticScan is not null)
            {
                try { await automaticScan; }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task AutomaticAndManualScansDescribeSelectedViewRatherThanScannedLibrary()
    {
        var root = Path.Combine(Path.GetTempPath(), $"Garou-refresh-{Guid.NewGuid():N}");
        var scannedPath = Path.Combine(root, "scanned");
        var selectedPath = Path.Combine(root, "selected");
        Directory.CreateDirectory(scannedPath);
        Directory.CreateDirectory(selectedPath);
        var factory = new SqliteConnectionFactory(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "library.db") }.ToString());
        using var stop = new CancellationTokenSource();
        Task? automaticScan = null;
        try
        {
            var ct = TestContext.Current.CancellationToken;
            await new DatabaseInitializer(factory).InitializeAsync(ct);
            var folders = new FolderRepository(factory);
            await folders.AddAsync(new Folder { Path = scannedPath }, ct);
            var selectedId = await folders.AddAsync(new Folder { Path = selectedPath }, ct);
            var libraries = new LibraryRepository(factory);
            await libraries.AddAsync(new LibraryDefinition { RootPath = scannedPath, IsEnabled = true }, ct);
            var excluded = new LibraryExcludedFolderRepository(factory);
            var media = new MediaFileRepository(factory);
            var session = new AppSettingsSession();
            session.Initialize(new AppSettings { SelectedFolderId = selectedId });
            var gate = new LibraryOperationGate();
            var indexer = new MediaIndexerService(new FileWatcherService(), libraries, media, folders, excluded, gate, session, TimeProvider.System);
            var gatedFolders = new GatedFolders(folders);
            using var viewModel = new MainViewModel(new MediaQueryService(media, media, gatedFolders),
                new LibraryCommands(libraries, excluded, indexer), gate, new SettingsViewModel(session), session,
                null!, media, folders, null!, new XnfoIndexService(new XnfoStore(), factory), indexer);
            await viewModel.InitializeAsync(ct);
            Assert.Equal(selectedPath, viewModel.SelectedFolder?.PathText);

            var automaticFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.IsScanning) && !viewModel.IsScanning)
                    automaticFinished.TrySetResult();
            };
            gatedFolders.BlockNext();
            automaticScan = Task.Run(() => indexer.RunAsync(stop.Token));
            await gatedFolders.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            AssertRefreshMessage(viewModel, scannedPath, selectedPath);
            gatedFolders.Release.TrySetResult();
            await automaticFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));

            gatedFolders.BlockNext();
            var manualScan = viewModel.RescanLibraryAsync(scannedPath, ct);
            await gatedFolders.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            AssertRefreshMessage(viewModel, scannedPath, selectedPath);
            gatedFolders.Release.TrySetResult();
            await manualScan.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(selectedPath, viewModel.SelectedFolder?.PathText);
        }
        finally
        {
            stop.Cancel();
            if (automaticScan is not null)
            {
                try { await automaticScan; }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertRefreshMessage(MainViewModel viewModel, string scannedPath, string selectedPath)
    {
        Assert.True(viewModel.IsScanInfoOpen);
        Assert.Equal("Updating the library view", viewModel.ScanInfoTitle);
        Assert.Equal("Refreshing the folder tree and the selected folder's media...", viewModel.ScanInfoMessage);
        Assert.DoesNotContain(scannedPath, viewModel.ScanInfoMessage);
        Assert.Equal(selectedPath, viewModel.SelectedFolder?.PathText);
    }

    private sealed class GatedFolders(IFolderRepository inner) : IFolderRepository
    {
        private bool _blockNext;
        public TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void BlockNext()
        {
            Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _blockNext = true;
        }

        public async Task<IReadOnlyList<Folder>> GetAllAsync(CancellationToken cancellationToken)
        {
            if (_blockNext)
            {
                _blockNext = false;
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return await inner.GetAllAsync(cancellationToken);
        }

        public Task<Folder?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => inner.GetByIdAsync(id, cancellationToken);
        public Task<Folder?> GetByPathAsync(string path, CancellationToken cancellationToken) => inner.GetByPathAsync(path, cancellationToken);
        public Task<Guid> AddAsync(Folder folder, CancellationToken cancellationToken) => inner.AddAsync(folder, cancellationToken);
        public Task DeleteSubtreeAsync(Guid id, CancellationToken cancellationToken) => inner.DeleteSubtreeAsync(id, cancellationToken);
    }
}
