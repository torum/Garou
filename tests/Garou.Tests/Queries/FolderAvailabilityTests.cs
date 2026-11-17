using Avalonia.Headless.XUnit;
using Garou.Application.Queries;
using Garou.Domain.Abstractions;
using Garou.Domain.Entities;
using Garou.Domain.Enums;
using Garou.Domain.Models;
using Garou.Infrastructure.Settings;
using Garou.ViewModels;

namespace Garou.Tests.Queries;

[Collection("Avalonia UI")]
public sealed class FolderAvailabilityTests
{
    [AvaloniaFact]
    public async Task MissingFolderAndDescendantsAreDimmedWithoutProbingForMedia()
    {
        var root = new Folder { Path = Path.Combine(Path.GetTempPath(), $"Garou-missing-{Guid.NewGuid():N}") };
        var child = new Folder { Path = Path.Combine(root.Path, "child"), ParentId = root.Id };
        var metadata = new RecordingMetadata();
        using var viewModel = CreateViewModel([root, child], metadata);
        await viewModel.LoadFolderTreeAsync(CancellationToken.None);
        var rootNode = Assert.Single(viewModel.FolderTree);
        var childNode = Assert.Single(rootNode.Children);
        await WaitForAvailabilityAsync(rootNode, false);
        await WaitForAvailabilityAsync(childNode, false);
        Assert.Equal(0.6, rootNode.AvailabilityOpacity);
        Assert.Equal(0.6, childNode.AvailabilityOpacity);

        var media = new MediaFile { FolderId = child.Id, Path = Path.Combine(child.Path, "missing.jpg"), FileName = "missing.jpg", Extension = ".jpg", MediaType = MediaType.Image };
        viewModel.SelectedItem = new MediaItemViewModel(media);
        await viewModel.LoadSelectedMetadataAsync();
        Assert.Equal(0, metadata.Calls);
        Assert.Contains("unavailable", viewModel.StatusText);
        Assert.False(viewModel.IsInfoBarOpen);
        Assert.False(await viewModel.IsMediaAvailableAsync(media, CancellationToken.None));
    }

    [AvaloniaFact]
    public async Task ExistingFolderAndFileRemainAvailableAndMetadataLoads()
    {
        var folder = new Folder { Path = Path.GetTempPath() };
        var path = Path.GetTempFileName();
        try
        {
            var metadata = new RecordingMetadata();
            using var viewModel = CreateViewModel([folder], metadata);
            await viewModel.LoadFolderTreeAsync(CancellationToken.None);
            var node = Assert.Single(viewModel.FolderTree);
            await WaitForAvailabilityAsync(node, true);
            Assert.Equal(1.0, node.AvailabilityOpacity);
            var media = new MediaFile { FolderId = folder.Id, Path = path, FileName = Path.GetFileName(path), Extension = ".jpg", MediaType = MediaType.Image };
            viewModel.SelectedItem = new MediaItemViewModel(media);
            await viewModel.LoadSelectedMetadataAsync();
            Assert.Equal(1, metadata.Calls);
            Assert.True(await viewModel.IsMediaAvailableAsync(media, CancellationToken.None));
            Assert.False(viewModel.IsInfoBarOpen);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task MissingFileDoesNotMarkAnExistingFolderUnavailable()
    {
        var folder = new Folder { Path = Path.GetTempPath() };
        using var viewModel = CreateViewModel([folder], new RecordingMetadata());
        await viewModel.LoadFolderTreeAsync(CancellationToken.None);
        var node = Assert.Single(viewModel.FolderTree);
        await WaitForAvailabilityAsync(node, true);
        var media = new MediaFile { FolderId = folder.Id, Path = Path.Combine(folder.Path, $"Garou-missing-{Guid.NewGuid():N}.jpg"), FileName = "missing.jpg", Extension = ".jpg", MediaType = MediaType.Image };
        Assert.False(await viewModel.IsMediaAvailableAsync(media, CancellationToken.None));
        Assert.True(node.IsAvailable);
    }

    [AvaloniaFact]
    public async Task RebuildingTreeChecksAReconnectedFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Garou-reconnected-{Guid.NewGuid():N}");
        var folder = new Folder { Path = path };
        try
        {
            using var viewModel = CreateViewModel([folder], new RecordingMetadata());
            await viewModel.LoadFolderTreeAsync(CancellationToken.None);
            await WaitForAvailabilityAsync(Assert.Single(viewModel.FolderTree), false);
            Directory.CreateDirectory(path);
            await viewModel.LoadFolderTreeAsync(CancellationToken.None);
            var node = Assert.Single(viewModel.FolderTree);
            await WaitForAvailabilityAsync(node, true);
            Assert.Equal(1.0, node.AvailabilityOpacity);
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task FileRemovedDuringExtractionIsReportedAsUnavailable()
    {
        var path = Path.GetTempFileName();
        try
        {
            using var viewModel = CreateViewModel([], new DisappearingMetadata());
            viewModel.SelectedItem = new MediaItemViewModel(new MediaFile
            {
                Path = path, FileName = Path.GetFileName(path), Extension = ".jpg", MediaType = MediaType.Image
            });
            await viewModel.LoadSelectedMetadataAsync();
            Assert.Contains("unavailable", viewModel.StatusText);
            Assert.False(viewModel.IsInfoBarOpen);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static MainViewModel CreateViewModel(IReadOnlyList<Folder> folders, IMetadataExtractionService metadata)
    {
        var session = new AppSettingsSession();
        var repository = new FolderRepository(folders);
        return new MainViewModel(new MediaQueryService(null!, null!, repository), null!, null!, new SettingsViewModel(session), session,
            null!, null!, repository, metadata, null!, null!);
    }

    private static async Task WaitForAvailabilityAsync(FolderNode node, bool expected)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(FolderNode.IsAvailable) && node.IsAvailable == expected)
                completion.TrySetResult();
        }
        node.PropertyChanged += OnChanged;
        try
        {
            if (node.IsAvailable != expected)
                await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            node.PropertyChanged -= OnChanged;
        }
    }

    private sealed class RecordingMetadata : IMetadataExtractionService
    {
        public int Calls { get; private set; }
        public Task<MediaMetadata> ExtractAsync(string filePath, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new MediaMetadata());
        }
    }

    private sealed class DisappearingMetadata : IMetadataExtractionService
    {
        public Task<MediaMetadata> ExtractAsync(string filePath, CancellationToken cancellationToken)
        {
            File.Delete(filePath);
            throw new FileNotFoundException("File disappeared", filePath);
        }
    }

    private sealed class FolderRepository(IReadOnlyList<Folder> folders) : IFolderRepository
    {
        public Task<IReadOnlyList<Folder>> GetAllAsync(CancellationToken cancellationToken) => Task.FromResult(folders);
        public Task<Folder?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Folder?> GetByPathAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Guid> AddAsync(Folder folder, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteSubtreeAsync(Guid folderId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
