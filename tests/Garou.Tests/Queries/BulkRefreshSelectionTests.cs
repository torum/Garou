using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Garou.Application.Libraries;
using Garou.Application.Queries;
using Garou.Domain.Abstractions;
using Garou.Domain.Entities;
using Garou.Domain.Enums;
using Garou.Domain.Models;
using Garou.Infrastructure.Metadata;
using Garou.Infrastructure.Settings;
using Garou.Infrastructure.Sync;
using Garou.UI.Views;
using Garou.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Garou.Tests.Queries;

[Collection("Avalonia UI")]
public sealed class BulkRefreshSelectionTests
{
    [AvaloniaFact]
    public async Task RescanWithManyNewItemsPreservesSelectedItem()
    {
        var previousServices = App.Services;
        var folder = new Folder { Path = Path.GetTempPath() };
        var folders = new TestFolders(folder);
        var original = Enumerable.Range(0, 100).Select(i => CreateFile(folder.Id, $"old{i}.jpg")).ToArray();
        var added = Enumerable.Range(0, 100).Select(i => CreateFile(folder.Id, $"new{i}.jpg")).ToArray();
        var search = new TestSearch { Files = original };
        var session = new AppSettingsSession();
        var gate = new LibraryOperationGate();
        var indexer = new MediaIndexerService(null!, null!, null!, null!, null!, gate, session, TimeProvider.System);
        var commands = new LibraryCommands(new TestLibraries(folder.Path), null!, new TestScanner());
        using var vm = new MainViewModel(new MediaQueryService(search, null!, folders), commands, gate,
            new SettingsViewModel(session), session, new TestGenerator(), null!, folders, null!,
            new XnfoIndexService(new XnfoStore(), null!), indexer);
        using var services = new ServiceCollection().AddSingleton(session).AddSingleton(vm).BuildServiceProvider();
        App.Services = services;
        var window = new Window { Width = 900, Height = 600 };
        try
        {
            var page = new MediaListBoxPage();
            window.Content = page;
            window.Show();
            await vm.InitializeAsync(CancellationToken.None);
            window.UpdateLayout();
            var list = Assert.IsType<ListBox>(page.FindControl<ListBox>("MediaListBox"));
            var selected = vm.Items[20];
            list.SelectedItem = selected;
            Assert.Same(selected, vm.SelectedItem);
            var notifications = new List<NotifyCollectionChangedAction>();
            vm.Items.CollectionChanged += (_, args) => notifications.Add(args.Action);
            search.Files = [.. original, .. added];

            await vm.RescanLibraryAsync(folder.Path, CancellationToken.None);
            window.UpdateLayout();

            Assert.Equal([NotifyCollectionChangedAction.Reset], notifications);
            Assert.Equal(200, vm.Items.Count);
            Assert.Same(selected, vm.SelectedItem);
            Assert.Same(selected, list.SelectedItem);
            Assert.False(vm.IsInfoBarOpen, vm.InfoBarErrorMessage);
        }
        finally
        {
            window.Close();
            App.Services = previousServices;
        }
    }

    private static MediaFile CreateFile(Guid folderId, string name) => new()
    {
        FolderId = folderId, Path = Path.Combine(Path.GetTempPath(), name), FileName = name,
        Extension = ".jpg", MediaType = MediaType.Unknown
    };

    private sealed class TestSearch : IMediaSearchRepository
    {
        public IReadOnlyList<MediaFile> Files { get; set; } = [];
        public Task<IReadOnlyList<MediaFile>> SearchAsync(BuiltQuery query, CancellationToken cancellationToken) => Task.FromResult(Files);
        public Task<long> CountAsync(BuiltQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class TestFolders(Folder folder) : IFolderRepository
    {
        public Task<IReadOnlyList<Folder>> GetAllAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Folder>>([folder]);
        public Task<Folder?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Folder?> GetByPathAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Guid> AddAsync(Folder item, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteSubtreeAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class TestLibraries(string path) : ILibraryRepository
    {
        public Task<IReadOnlyList<LibraryDefinition>> GetAllAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<LibraryDefinition>>([new LibraryDefinition { RootPath = path }]);
        public Task AddAsync(LibraryDefinition library, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetEnabledAsync(Guid id, bool isEnabled, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class TestScanner : ILibraryScanner
    {
        public Task ScanAsync(LibraryDefinition library, IProgress<ScanProgress>? progress, CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class TestGenerator : IThumbnailGeneratorService
    {
        public Task<string?> GenerateAsync(MediaFile file, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task ClearCacheAsync(MediaFile file, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool HasFailed(MediaFile file) => false;
    }
}
