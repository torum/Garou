using Avalonia.Headless.XUnit;
using Garou.Domain.Abstractions;
using Garou.Domain.Entities;
using Garou.Domain.Enums;
using Garou.Infrastructure.Settings;
using Garou.ViewModels;

namespace Garou.Tests.Thumbnails;

[Collection("Avalonia UI")]
public sealed class VisibleThumbnailStatusTests
{
    [AvaloniaFact]
    public async Task CompletedGenerationClearsProgressWithoutAnotherViewportChange()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var session = new AppSettingsSession();
        var generator = new GatedGenerator();
        var repository = new ThumbnailRepository();
        using var viewModel = new MainViewModel(null!, null!, null!, new SettingsViewModel(session), session,
            generator, repository, null!, null!, null!, null!);
        var media = CreateMedia();
        var finished = ObserveBatchCompletion(viewModel);

        try
        {
            viewModel.OnVisibleItemsChanged([media]);
            await generator.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            Assert.True(viewModel.IsGeneratingThumbnails);
            Assert.Equal("Updating thumbnails: Generating photo.jpg", viewModel.ThumbnailStatusText);

            generator.Completion.SetResult("thumbnail.jpg");
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

            Assert.False(viewModel.IsGeneratingThumbnails);
            Assert.Equal(string.Empty, viewModel.ThumbnailStatusText);
            Assert.Equal((media.Id, "thumbnail.jpg"), repository.Updated);
        }
        finally
        {
            generator.Completion.TrySetCanceled();
        }
    }

    [AvaloniaFact]
    public async Task SupersededGenerationClearsProgressBeforeDebouncingNewViewport()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var session = new AppSettingsSession();
        var generator = new GatedGenerator();
        using var viewModel = new MainViewModel(null!, null!, null!, new SettingsViewModel(session), session,
            generator, null!, null!, null!, null!, null!);
        var finished = ObserveBatchCompletion(viewModel);

        try
        {
            viewModel.OnVisibleItemsChanged([CreateMedia()]);
            await generator.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            Assert.NotEmpty(viewModel.ThumbnailStatusText);
            viewModel.OnVisibleItemsChanged([]);
            generator.Completion.SetResult(null);
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

            Assert.False(viewModel.IsGeneratingThumbnails);
            Assert.Equal(string.Empty, viewModel.ThumbnailStatusText);
        }
        finally
        {
            generator.Completion.TrySetCanceled();
        }
    }

    private static MediaFile CreateMedia() => new()
    {
        Path = "photo.jpg", FileName = "photo.jpg", Extension = ".jpg", MediaType = MediaType.Image
    };

    private static TaskCompletionSource ObserveBatchCompletion(MainViewModel viewModel)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.IsGeneratingThumbnails) && !viewModel.IsGeneratingThumbnails)
                finished.TrySetResult();
        };
        return finished;
    }

    private sealed class GatedGenerator : IThumbnailGeneratorService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HasFailed(MediaFile mediaFile) => false;
        public Task ClearCacheAsync(MediaFile mediaFile, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string?> GenerateAsync(MediaFile mediaFile, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return Completion.Task;
        }
    }

    private sealed class ThumbnailRepository : IMediaFileRepository
    {
        public (Guid Id, string? Path) Updated { get; private set; }
        public Task UpdateThumbnailPathAsync(Guid mediaFileId, string? thumbnailPath, CancellationToken cancellationToken)
        {
            Updated = (mediaFileId, thumbnailPath);
            return Task.CompletedTask;
        }
        public Task<MediaFile?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MediaFile?> GetByPathAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MediaFile>> GetPageAsync(Guid? folderId, Guid afterId, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpsertBatchAsync(IReadOnlyList<MediaFile> mediaFiles, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> MoveAsync(string oldPath, MediaFile updatedFile, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteByPathAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetPathsUnderRootAsync(string rootPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, MediaFileSignature>> GetSignaturesUnderRootAsync(string rootPath, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
