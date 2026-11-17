using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Garou.Domain.Entities;
using Garou.Infrastructure.Thumbnails;
using Garou.Localization;

namespace Garou.UI.Views;

public sealed class SlideshowWindow : Window
{
    private readonly IReadOnlyList<MediaFile> _images;
    private readonly ResourceLocalizer _localizer;
    private readonly FfmpegRunner _ffmpeg;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _message = new() { Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _position = new() { Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _pause = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private CancellationTokenSource? _loadCancellation;
    private Bitmap? _bitmap;
    private int _index;
    private bool _paused;
    private bool _closed;

    public SlideshowWindow(IReadOnlyList<MediaFile> images, int startIndex, ResourceLocalizer localizer, FfmpegRunner ffmpeg)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentOutOfRangeException.ThrowIfNegative(startIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(startIndex, images.Count);
        _images = images;
        _index = startIndex;
        _localizer = localizer;
        _ffmpeg = ffmpeg;
        Title = localizer["SlideshowTitle"];
        Background = Brushes.Black;
        WindowState = WindowState.FullScreen;

        var previous = new Button { Content = localizer["SlideshowPrevious"] };
        var next = new Button { Content = localizer["SlideshowNext"] };
        var close = new Button { Content = localizer["CloseLabel"] };
        _pause.Content = localizer["SlideshowPause"];
        previous.Click += (_, _) => Move(-1);
        next.Click += (_, _) => Move(1);
        _pause.Click += (_, _) => TogglePause();
        close.Click += (_, _) => Close();
        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 12,
            Children = { previous, _pause, next, _position, close }
        };
        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        grid.Children.Add(_image);
        grid.Children.Add(_message);
        Grid.SetRow(controls, 1);
        grid.Children.Add(controls);
        Content = grid;

        _timer.Tick += (_, _) => Move(1);
        Opened += (_, _) => _ = ShowCurrentAsync();
        Closed += (_, _) =>
        {
            _closed = true;
            _timer.Stop();
            _loadCancellation?.Cancel();
            _image.Source = null;
            _bitmap?.Dispose();
            _bitmap = null;
        };
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.Left: Move(-1); break;
            case Key.Right: Move(1); break;
            case Key.Space: TogglePause(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Move(int offset)
    {
        _index = (_index + offset + _images.Count) % _images.Count;
        _ = ShowCurrentAsync();
    }

    private void TogglePause()
    {
        _paused = !_paused;
        _pause.Content = _localizer[_paused ? "SlideshowResume" : "SlideshowPause"];
        if (_paused)
        {
            _timer.Stop();
        }
        else if (_loadCancellation is null && _images.Count > 1)
        {
            _timer.Start();
        }
    }

    private async Task ShowCurrentAsync()
    {
        _timer.Stop();
        _loadCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        var file = _images[_index];
        _position.Text = $"{_index + 1} / {_images.Count}  {file.FileName}";
        _image.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
        _message.Text = _localizer["SlideshowLoading"];
        try
        {
            var available = await Task.Run(() => File.Exists(file.Path), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!available)
            {
                _message.Text = string.Format(CultureInfo.CurrentCulture, _localizer["SlideshowLoadError"], file.FileName,
                    string.Format(CultureInfo.CurrentCulture, _localizer["MediaUnavailable"], file.FileName));
                return;
            }
            var bitmap = await LoadBitmapAsync(file.Path, cancellation.Token);
            if (_closed || cancellation.IsCancellationRequested)
            {
                bitmap.Dispose();
                return;
            }

            _bitmap = bitmap;
            _image.Source = bitmap;
            _message.Text = string.Empty;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or Win32Exception or TimeoutException or NotSupportedException)
        {
            if (!_closed && !cancellation.IsCancellationRequested)
            {
                _message.Text = string.Format(CultureInfo.CurrentCulture, _localizer["SlideshowLoadError"], file.FileName, exception.Message);
            }
        }
        finally
        {
            if (_loadCancellation == cancellation)
            {
                _loadCancellation = null;
                if (!_closed && !_paused && _images.Count > 1)
                {
                    _timer.Start();
                }
            }
            cancellation.Dispose();
        }
    }

    private async Task<Bitmap> LoadBitmapAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(() =>
            {
                using var stream = File.OpenRead(path);
                return new Bitmap(stream);
            }, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            var bytes = await _ffmpeg.ReadImageAsync(path, cancellationToken);
            return await Task.Run(() =>
            {
                using var stream = new MemoryStream(bytes);
                return new Bitmap(stream);
            }, cancellationToken);
        }
    }
}
