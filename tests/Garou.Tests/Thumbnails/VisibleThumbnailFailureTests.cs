using Avalonia.Headless.XUnit;
using Garou.Domain.Abstractions;
using Garou.Domain.Entities;
using Garou.Domain.Enums;
using Garou.Infrastructure.Settings;
using Garou.UI.Models;
using Garou.ViewModels;

namespace Garou.Tests.Thumbnails;

[Collection("Avalonia UI")]
public sealed class VisibleThumbnailFailureTests
{
    [AvaloniaFact]
    public async Task MovingViewportSkipsFailedItemsAndPreservesFailureState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var session = new AppSettingsSession();
        var generator = new FailingGenerator();
        using var viewModel = new MainViewModel(null!, null!, null!, new SettingsViewModel(session), session,
            generator, null!, null!, null!, null!, null!);
        var first = new MediaFile { Path = "first.jpg", FileName = "first.jpg", Extension = ".jpg", MediaType = MediaType.Image };
        var second = new MediaFile { Path = "second.jpg", FileName = "second.jpg", Extension = ".jpg", MediaType = MediaType.Image };
        var firstItem = new MediaItemViewModel(first);
        viewModel.Items.Add(firstItem);
        viewModel.Items.Add(new MediaItemViewModel(second));
        var firstFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishedCount = 0;
        ThumbnailLoadState? firstStateDuringSecondGeneration = null;
        generator.OnGenerate = file =>
        {
            if (file.Id == second.Id)
                firstStateDuringSecondGeneration = firstItem.ThumbnailState;
        };
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.IsGeneratingThumbnails) && !viewModel.IsGeneratingThumbnails)
            {
                if (++finishedCount == 1)
                    firstFinished.TrySetResult();
                else
                    secondFinished.TrySetResult();
            }
        };

        viewModel.OnVisibleItemsChanged([first]);
        await firstFinished.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        Assert.Equal(string.Empty, viewModel.ThumbnailStatusText);
        Assert.Equal(ThumbnailLoadState.Failed, firstItem.ThumbnailState);

        firstItem.Dispose();
        firstItem = new MediaItemViewModel(first);
        viewModel.Items[0] = firstItem;
        Assert.Equal(ThumbnailLoadState.Pending, firstItem.ThumbnailState);
        viewModel.OnVisibleItemsChanged([first, second]);
        await secondFinished.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        Assert.Equal(string.Empty, viewModel.ThumbnailStatusText);
        Assert.Equal(ThumbnailLoadState.Failed, firstStateDuringSecondGeneration);
        Assert.Equal(ThumbnailLoadState.Failed, firstItem.ThumbnailState);
        Assert.Equal([first.Path, second.Path], generator.Attempts);
    }

    private sealed class FailingGenerator : IThumbnailGeneratorService
    {
        private readonly HashSet<string> _failed = new(StringComparer.Ordinal);
        public List<string> Attempts { get; } = [];
        public Action<MediaFile>? OnGenerate { get; set; }

        public bool HasFailed(MediaFile mediaFile) => _failed.Contains(mediaFile.Path);

        public Task<string?> GenerateAsync(MediaFile mediaFile, CancellationToken cancellationToken)
        {
            Attempts.Add(mediaFile.Path);
            OnGenerate?.Invoke(mediaFile);
            _failed.Add(mediaFile.Path);
            return Task.FromResult<string?>(null);
        }

        public Task ClearCacheAsync(MediaFile mediaFile, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
