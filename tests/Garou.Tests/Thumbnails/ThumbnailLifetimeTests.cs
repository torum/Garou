using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Garou.Domain.Entities;
using Garou.UI.Models;
using Garou.ViewModels;

namespace Garou.Tests.Thumbnails;

[Collection("Avalonia UI")]
public sealed class ThumbnailLifetimeTests
{
    [AvaloniaFact]
    public async Task DisposingMediaItemKeepsDisplayedBitmapUsableDuringLayout()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = Path.GetTempFileName();
        try
        {
            Avalonia.Application.Current!.Styles.Add(new FluentTheme());
            var item = new MediaItemViewModel(new MediaFile
            {
                Path = path,
                FileName = Path.GetFileName(path),
                Extension = Path.GetExtension(path),
                ThumbnailPath = path
            });
            var thumbnail = Assert.IsType<ThumbnailImage>(item.Thumbnail);
            await WaitForThumbnailAsync(thumbnail, cancellationToken);

            var bitmap = Assert.IsAssignableFrom<Bitmap>(thumbnail.Bitmap);
            Image? outgoingImage = null;
            var transition = new TransitioningContentControl
            {
                ContentTemplate = new FuncDataTemplate<Bitmap>((value, _) => outgoingImage = new Image { Source = value }),
                PageTransition = new CrossFade(TimeSpan.FromSeconds(1)),
                Content = bitmap
            };
            var window = new Window { Content = transition, Width = 100, Height = 100 };
            try
            {
                window.Show();
                window.UpdateLayout();
                var image = Assert.IsType<Image>(outgoingImage);

                item.Dispose();
                Assert.Null(thumbnail.Bitmap);
                transition.Content = null;
                window.UpdateLayout();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                image.InvalidateMeasure();
                image.Measure(new Size(100, 100));

                Assert.Same(bitmap, image.Source);
                Assert.Equal(new Size(1, 1), bitmap.Size);
                Assert.True(image.DesiredSize.Width > 0);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DisposedThumbnailCanBeCollectedAfterItsLastReferenceIsReleased()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = Path.GetTempFileName();
        try
        {
            WeakReference bitmapReference;
            await using (var session = HeadlessUnitTestSession.StartNew(typeof(Garou.App)))
            {
                bitmapReference = await session.Dispatch(async () =>
                {
                    var thumbnail = new ThumbnailImage(path);
                    await WaitForThumbnailAsync(thumbnail, cancellationToken);
                    var reference = new WeakReference(Assert.IsAssignableFrom<Bitmap>(thumbnail.Bitmap), trackResurrection: true);
                    thumbnail.Dispose();
                    return reference;
                }, cancellationToken);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.False(bitmapReference.IsAlive);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task WaitForThumbnailAsync(ThumbnailImage thumbnail, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPropertyChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(ThumbnailImage.State) && thumbnail.State != ThumbnailLoadState.Loading)
            {
                completion.TrySetResult();
            }
        }

        thumbnail.PropertyChanged += OnPropertyChanged;
        try
        {
            if (thumbnail.State == ThumbnailLoadState.Loading)
            {
                await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }

            Assert.Equal(ThumbnailLoadState.Loaded, thumbnail.State);
        }
        finally
        {
            thumbnail.PropertyChanged -= OnPropertyChanged;
        }
    }
}
