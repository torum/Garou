using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Garou.Application.Libraries;
using Garou.Application.Queries;
using Garou.Domain.Abstractions;
using Garou.Domain.Entities;
using Garou.Domain.Enums;
using Garou.Infrastructure.Metadata;
using Garou.Infrastructure.Settings;
using Garou.Infrastructure.Sync;
using Garou.ViewModels;
using Garou.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Garou.Tests.Queries;

[Collection("Avalonia UI")]
public sealed class MainWindowStartupCursorTests
{
    [AvaloniaFact]
    public async Task StartupCursorRemainsUntilTreeAndItemsAreLoadedButNotBackgroundStartup()
    {
        var previousServices = App.Services;
        var previousStartup = App.StartupTask;
        var previousBackground = App.BackgroundStartupTask;
        var session = new AppSettingsSession();
        var settings = new SettingsViewModel(session);
        var folder = new Folder { Path = Path.Combine(Path.GetTempPath(), "startup-library") };
        var media = new MediaFile { FolderId = folder.Id, Path = Path.Combine(folder.Path, "photo.jpg"), FileName = "photo.jpg", Extension = ".jpg", MediaType = MediaType.Image };
        var folders = new StartupFolders(folder);
        var search = new GatedSearch(media);
        var indexer = new MediaIndexerService(null!, null!, null!, null!, null!, new LibraryOperationGate(), session, TimeProvider.System);
        var viewModel = new MainViewModel(new MediaQueryService(search, null!, folders), null!, new LibraryOperationGate(), settings, session,
            new NoThumbnailGenerator(), null!, folders, null!, new XnfoIndexService(new XnfoStore(), null!), indexer);
        using var services = new ServiceCollection().AddSingleton(session).AddSingleton(viewModel).BuildServiceProvider();
        var startup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var background = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var databaseShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var phases = new List<string>();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ScanInfoMessage))
            {
                phases.Add(viewModel.ScanInfoMessage);
                if (phases.Count == 1)
                    databaseShown.TrySetResult();
            }
        };
        App.Services = services;
        App.StartupTask = startup.Task;
        App.BackgroundStartupTask = background.Task;
        var window = new MainWindow(session) { DataContext = viewModel };
        var cursorRestored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startingCursor = window.Cursor;
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Input.InputElement.CursorProperty && !ReferenceEquals(window.Cursor, startingCursor))
                cursorRestored.TrySetResult();
        };
        try
        {
            window.Show();
            await databaseShown.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains("Opening the database", viewModel.ScanInfoMessage);
            Assert.True(viewModel.IsScanInfoOpen);
            startup.TrySetResult();
            await folders.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains("Loading saved folders", viewModel.ScanInfoMessage);
            folders.Release.TrySetResult();
            await search.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains("Loading saved media", viewModel.ScanInfoMessage);
            Assert.Same(startingCursor, window.Cursor);
            Assert.Single(viewModel.FolderTree);
            Assert.Empty(viewModel.Items);
            var tree = Assert.IsType<TreeView>(window.FindControl<TreeView>("FolderTreeView"));
            Assert.Same(viewModel.SelectedFolder, tree.SelectedItem);

            search.Release.TrySetResult();
            await cursorRestored.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(viewModel.IsReady);
            Assert.Equal(media.Id, Assert.Single(viewModel.Items).File.Id);
            Assert.Same(viewModel.SelectedFolder, tree.SelectedItem);
            Assert.False(background.Task.IsCompleted);
            Assert.False(viewModel.IsScanInfoOpen);
            Assert.Collection(phases,
                phase => Assert.Contains("Opening the database", phase),
                phase => Assert.Contains("Loading saved folders", phase),
                phase => Assert.Contains("Restoring the selected folder", phase),
                phase => Assert.Contains("Loading saved media", phase),
                phase => Assert.Contains("Sorting media", phase),
                phase => Assert.Contains("Adding media", phase),
                phase => Assert.Contains("Preparing the window layout", phase));
        }
        finally
        {
            startup.TrySetResult();
            folders.Release.TrySetResult();
            search.Release.TrySetResult();
            background.TrySetResult();
            window.Close();
            App.Services = previousServices;
            App.StartupTask = previousStartup;
            App.BackgroundStartupTask = previousBackground;
        }
    }

    private sealed class NoThumbnailGenerator : IThumbnailGeneratorService
    {
        public Task<string?> GenerateAsync(MediaFile mediaFile, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task ClearCacheAsync(MediaFile mediaFile, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool HasFailed(MediaFile mediaFile) => false;
    }

    private sealed class StartupFolders(Folder folder) : IFolderRepository
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyList<Folder>> GetAllAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return [folder];
        }
        public Task<Folder?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<Folder?>(folder);
        public Task<Folder?> GetByPathAsync(string path, CancellationToken cancellationToken) => Task.FromResult<Folder?>(folder);
        public Task<Guid> AddAsync(Folder item, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteSubtreeAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class GatedSearch(MediaFile file) : IMediaSearchRepository
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyList<MediaFile>> SearchAsync(BuiltQuery query, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return [file];
        }
        public Task<long> CountAsync(BuiltQuery query, CancellationToken cancellationToken) => Task.FromResult(1L);
    }

    [AvaloniaFact]
    public async Task StartupFailureRestoresCursorAndReportsError()
    {
        var previousServices = App.Services;
        var previousStartup = App.StartupTask;
        var session = new AppSettingsSession();
        var settings = new SettingsViewModel(session);
        var viewModel = new MainViewModel(null!, null!, null!, settings, session, null!, null!, null!, null!, null!, null!);
        using var services = new ServiceCollection().AddSingleton(session).AddSingleton(viewModel).BuildServiceProvider();
        var startup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errorShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        App.Services = services;
        App.StartupTask = startup.Task;
        var window = new MainWindow(session) { DataContext = viewModel };
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsInfoBarOpen) && viewModel.IsInfoBarOpen)
                errorShown.TrySetResult();
        };
        try
        {
            var startingCursor = window.Cursor;
            Assert.NotNull(startingCursor);
            window.Show();
            Assert.Same(startingCursor, window.Cursor);

            startup.SetException(new InvalidOperationException("Startup failed"));
            await errorShown.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.NotNull(window.Cursor);
            Assert.NotSame(startingCursor, window.Cursor);
            Assert.Equal("Startup failed", viewModel.InfoBarErrorMessage);
        }
        finally
        {
            window.Close();
            App.Services = previousServices;
            App.StartupTask = previousStartup;
        }
    }
}
