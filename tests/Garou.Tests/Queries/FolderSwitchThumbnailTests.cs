using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Garou.Application.Queries;
using Garou.Domain.Abstractions;
using Garou.Domain.Entities;
using Garou.Domain.Enums;
using Garou.Infrastructure.Settings;
using Garou.UI.Views;
using Garou.ViewModels;
using Garou.Views.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace Garou.Tests.Queries;

[Collection("Avalonia UI")]
public sealed class FolderSwitchThumbnailTests
{
    [AvaloniaFact]
    public async Task SwitchingFoldersWhileThumbnailGenerationIsInProgressDisplaysNewItems()
    {
        var previousServices = App.Services;
        var firstFolder = new Folder { Path = Path.Combine(Path.GetTempPath(), "first") };
        var secondFolder = new Folder { Path = Path.Combine(Path.GetTempPath(), "second") };
        var first = CreateMedia(firstFolder.Id, "first.jpg");
        var second = CreateMedia(secondFolder.Id, "second.jpg");
        var search = new SearchRepository(first, second);
        var generator = new GatedGenerator();
        var session = new AppSettingsSession();
        using var viewModel = new MainViewModel(new MediaQueryService(search, null!, null!), null!, null!,
            new SettingsViewModel(session), session, generator, null!, null!, null!, null!, null!);
        using var services = new ServiceCollection().AddSingleton(session).AddSingleton(viewModel).BuildServiceProvider();
        App.Services = services;
        var window = new Window { Width = 900, Height = 600 };
        try
        {
            var page = new MediaListBoxPage();
            window.Content = page;
            window.Show();
            window.UpdateLayout();
            var list = Assert.IsType<ListBox>(page.FindControl<ListBox>("MediaListBox"));
            viewModel.SelectedFolder = new FolderNode(firstFolder, [], true);
            await WaitForItemAsync(viewModel, first.Id);
            await SettleAsync(window);
            await generator.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            viewModel.SelectedFolder = new FolderNode(secondFolder, [], true);
            await generator.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            search.ContinueSecondSearch.TrySetResult();
            await WaitForItemAsync(viewModel, second.Id);
            await SettleAsync(window);

            Assert.Equal(second.Id, Assert.Single(viewModel.Items).File.Id);
            var panel = list.GetVisualDescendants().OfType<VirtualizingUniformPanel>().Single();
            Assert.Contains(panel.Children, child => child.DataContext is MediaItemViewModel item && item.File.Id == second.Id);
            Assert.Equal(second.Id, await generator.NextStarted.Task.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.False(viewModel.IsInfoBarOpen, viewModel.InfoBarErrorMessage);
        }
        finally
        {
            search.ContinueSecondSearch.TrySetResult();
            generator.Completion.TrySetResult(null);
            window.Close();
            App.Services = previousServices;
        }
    }

    [AvaloniaFact]
    public async Task SwitchingFoldersCancelsPreviousResultPreparation()
    {
        var firstFolder = new Folder { Path = Path.Combine(Path.GetTempPath(), "first") };
        var secondFolder = new Folder { Path = Path.Combine(Path.GetTempPath(), "second") };
        var first = CreateMedia(firstFolder.Id, "first.jpg");
        var second = CreateMedia(secondFolder.Id, "second.jpg");
        using var search = new GatedSortRepository(first, second);
        var session = new AppSettingsSession();
        using var viewModel = new MainViewModel(new MediaQueryService(search, null!, null!), null!, null!,
            new SettingsViewModel(session), session, null!, null!, null!, null!, null!, null!);
        try
        {
            viewModel.SelectedFolder = new FolderNode(firstFolder, [], true);
            await search.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            viewModel.SelectedFolder = new FolderNode(secondFolder, [], true);
            search.Release();
            await WaitForItemAsync(viewModel, second.Id);

            Assert.Equal(second.Id, Assert.Single(viewModel.Items).File.Id);
            Assert.False(viewModel.IsInfoBarOpen, viewModel.InfoBarErrorMessage);
        }
        finally
        {
            search.Release();
        }
    }

    private static MediaFile CreateMedia(Guid folderId, string name) => new()
    {
        FolderId = folderId, Path = name, FileName = name, Extension = ".jpg", MediaType = MediaType.Image
    };

    private static async Task WaitForItemAsync(MainViewModel viewModel, Guid id)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
        {
            if (viewModel.Items.Any(item => item.File.Id == id)) completion.TrySetResult();
        }
        viewModel.Items.CollectionChanged += OnChanged;
        try
        {
            if (!viewModel.Items.Any(item => item.File.Id == id))
                await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            viewModel.Items.CollectionChanged -= OnChanged;
        }
    }

    private static async Task SettleAsync(Window window)
    {
        window.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
    }

    private sealed class SearchRepository(MediaFile first, MediaFile second) : IMediaSearchRepository
    {
        public TaskCompletionSource ContinueSecondSearch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<MediaFile>> SearchAsync(BuiltQuery query, CancellationToken cancellationToken)
        {
            var id = (string)query.Parameters["$folderId"]!;
            if (id == second.FolderId!.Value.ToString("N"))
            {
                await ContinueSecondSearch.Task.WaitAsync(cancellationToken);
                return [second];
            }
            return id == first.FolderId!.Value.ToString("N") ? [first] : [];
        }
        public Task<long> CountAsync(BuiltQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class GatedSortRepository(MediaFile first, MediaFile second) : IMediaSearchRepository, IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<MediaFile>> SearchAsync(BuiltQuery query, CancellationToken cancellationToken)
        {
            var id = (string)query.Parameters["$folderId"]!;
            return Task.FromResult<IReadOnlyList<MediaFile>>(id == first.FolderId!.Value.ToString("N")
                ? new GatedFiles(first, Started, _release)
                : [second]);
        }

        public Task<long> CountAsync(BuiltQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void Release() => _release.Set();
        public void Dispose() => _release.Dispose();
    }

    private sealed class GatedFiles(MediaFile file, TaskCompletionSource started, ManualResetEventSlim release) : IReadOnlyList<MediaFile>
    {
        public int Count => 1;
        public MediaFile this[int index] => index == 0 ? file : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<MediaFile> GetEnumerator()
        {
            started.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The test did not release result preparation.");
            yield return file;
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class GatedGenerator : IThumbnailGeneratorService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Guid> NextStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HasFailed(MediaFile mediaFile) => false;
        public Task ClearCacheAsync(MediaFile mediaFile, CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task<string?> GenerateAsync(MediaFile mediaFile, CancellationToken cancellationToken)
        {
            if (!Started.TrySetResult())
                NextStarted.TrySetResult(mediaFile.Id);
            try
            {
                return await Completion.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }
    }
}
