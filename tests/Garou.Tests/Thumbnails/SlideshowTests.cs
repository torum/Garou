using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Themes.Fluent;
using Avalonia.Headless.XUnit;
using Garou.Domain.Entities;
using Garou.Infrastructure.Thumbnails;
using Garou.Localization;
using Garou.UI.Views;

namespace Garou.Tests.Thumbnails;

[Collection("Avalonia UI")]
public sealed class SlideshowTests
{
    [AvaloniaFact]
    public async Task PlaybackStartsAtSelectedImageAndNavigatesWithWraparound()
    {
        var firstPath = Path.GetTempFileName();
        var secondPath = Path.GetTempFileName();
        try
        {
            Avalonia.Application.Current!.Styles.Add(new FluentTheme());
            var images = new[]
            {
                new MediaFile { Path = firstPath, FileName = "first.jpg", Extension = ".jpg" },
                new MediaFile { Path = secondPath, FileName = "second.jpg", Extension = ".jpg" }
            };
            var window = new SlideshowWindow(images, 1, new ResourceLocalizer(CultureInfo.GetCultureInfo("en")),
                new FfmpegRunner("missing-ffmpeg", TimeSpan.FromSeconds(1)));
            try
            {
                window.Show();
                var grid = Assert.IsType<Grid>(window.Content);
                var controls = Assert.IsType<StackPanel>(grid.Children[2]);
                var position = Assert.IsType<TextBlock>(controls.Children[3]);
                Assert.Equal("2 / 2  second.jpg", position.Text);

                var next = Assert.IsType<Button>(controls.Children[2]);
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("1 / 2  first.jpg", position.Text);

                next.Focus();
                window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
                Assert.Equal("2 / 2  second.jpg", position.Text);

                var pause = Assert.IsType<Button>(controls.Children[1]);
                pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("Resume", pause.Content);
                window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
                Assert.Equal("Pause", pause.Content);
                await Task.Yield();
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    [AvaloniaFact]
    public async Task PlaybackAdvancesAutomaticallyAndShowsFileErrors()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = Path.GetTempFileName();
        try
        {
            Avalonia.Application.Current!.Styles.Add(new FluentTheme());
            var images = new[]
            {
                new MediaFile { Path = path, FileName = "first.jpg", Extension = ".jpg" },
                new MediaFile { Path = path + ".missing", FileName = "missing.jpg", Extension = ".jpg" }
            };
            var window = new SlideshowWindow(images, 0, new ResourceLocalizer(CultureInfo.GetCultureInfo("en")),
                new FfmpegRunner("missing-ffmpeg", TimeSpan.FromSeconds(1)));
            try
            {
                window.Show();
                var grid = Assert.IsType<Grid>(window.Content);
                var message = Assert.IsType<TextBlock>(grid.Children[1]);
                var controls = Assert.IsType<StackPanel>(grid.Children[2]);
                var position = Assert.IsType<TextBlock>(controls.Children[3]);
                var deadline = DateTime.UtcNow.AddSeconds(12);
                while ((position.Text != "2 / 2  missing.jpg" || message.Text == "Loading...") && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(50, cancellationToken);
                }
                Assert.Equal("2 / 2  missing.jpg", position.Text);
                Assert.StartsWith("Could not display missing.jpg:", message.Text);
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
    public async Task FfmpegDecodesAnImageAndReportsInvalidInput()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        Assert.SkipWhen(!File.Exists(executable), "The bundled FFmpeg executable is not available on this platform.");
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7"), TestContext.Current.CancellationToken);
            var runner = new FfmpegRunner(executable, TimeSpan.FromSeconds(10));
            var output = await runner.ReadImageAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, output[..8]);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => runner.ReadImageAsync(path + ".missing", TestContext.Current.CancellationToken));
            Assert.Contains("FFmpeg could not decode", error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MissingFfmpegIsReportedToTheCaller()
    {
        var runner = new FfmpegRunner(Path.Combine(Path.GetTempPath(), $"missing-ffmpeg-{Guid.NewGuid():N}"), TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<Win32Exception>(() => runner.ReadImageAsync("image.jpg", TestContext.Current.CancellationToken));
    }
}
