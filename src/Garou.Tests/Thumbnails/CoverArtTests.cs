using System.ComponentModel;
using Avalonia.Headless;
using Garou.Domain.Entities;
using Garou.UI.Models;
using Garou.ViewModels;

namespace Garou.Tests.Thumbnails;

public sealed class CoverArtTests
{
    [Fact]
    public void MissingRelativeCoverReportsUnavailableWithoutTreatingItAsCache()
    {
        var root = Path.Combine(Path.GetTempPath(), $"Garou-cover-{Guid.NewGuid():N}");
        var file = new MediaFile
        {
            Path = Path.Combine(root, "movie.mp4"), FileName = "movie.mp4", Extension = ".mp4",
            CoverArt = "poster.jpg"
        };
        using var item = new MediaItemViewModel(file);
        Assert.True(item.IsCoverArtUnavailable);
        Assert.Null(item.Thumbnail);
        Assert.Equal("poster.jpg", file.WithThumbnailPath("generated.jpg").CoverArt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrUnreadableCoverFallsBackToGeneratedThumbnail(bool lockCover)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"Garou-cover-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var coverPath = Path.Combine(root, "poster.jpg");
        var thumbnailPath = Path.Combine(root, "generated.jpg");
        try
        {
            await File.WriteAllBytesAsync(thumbnailPath, [1], cancellationToken);
            using var lockedCover = lockCover ? new FileStream(coverPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None) : null;
            await using var session = HeadlessUnitTestSession.StartNew(typeof(Avalonia.Application));
            await session.Dispatch(async () =>
            {
                using var item = new MediaItemViewModel(new MediaFile
                {
                    Path = Path.Combine(root, "movie.mp4"), FileName = "movie.mp4", Extension = ".mp4",
                    CoverArt = "poster.jpg", ThumbnailPath = thumbnailPath
                });
                var image = Assert.IsType<ThumbnailImage>(item.Thumbnail);
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnPropertyChanged(object? sender, PropertyChangedEventArgs args)
                {
                    if (args.PropertyName == nameof(ThumbnailImage.State) && image.State != ThumbnailLoadState.Loading)
                        completion.TrySetResult();
                }
                image.PropertyChanged += OnPropertyChanged;
                try
                {
                    if (image.State == ThumbnailLoadState.Loading)
                        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                    Assert.Equal(ThumbnailLoadState.Loaded, image.State);
                    Assert.NotNull(image.Bitmap);
                    Assert.True(item.IsCoverArtUnavailable);
                    Assert.Equal(lockCover, image.PrimaryFailed);
                }
                finally
                {
                    image.PropertyChanged -= OnPropertyChanged;
                }
                return true;
            }, cancellationToken);
        }
        finally
        {
            if (File.Exists(coverPath)) File.Delete(coverPath);
            File.Delete(thumbnailPath);
            Directory.Delete(root);
        }
    }
}
