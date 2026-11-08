using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;

namespace Garou.UI.Models;

public sealed class ThumbnailImage : INotifyPropertyChanged, IDisposable
{
    private Bitmap? _bitmap;
    private ThumbnailLoadState _state = ThumbnailLoadState.Loading;
    private bool _disposed;

    public ThumbnailImage(string path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            _ = LoadAsync(path);
        }
    }

    public ThumbnailLoadState State
    {
        get => _state;
        private set
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Bitmap? Bitmap
    {
        get => _bitmap;
        private set
        {
            _bitmap = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Bitmap)));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _bitmap?.Dispose();
        _bitmap = null;
    }

    private async Task LoadAsync(string path)
    {
        try
        {
            var bitmap = await Task.Run(() =>
            {
                using var stream = File.OpenRead(path);
                return new Bitmap(stream);
            }).ConfigureAwait(true);

            if (_disposed)
            {
                bitmap.Dispose();
                return;
            }

            Bitmap = bitmap;
            State = ThumbnailLoadState.Loaded;
        }
        catch (Exception exception)
        {
            State = ThumbnailLoadState.Failed;
            Debug.WriteLine($"[ThumbnailImage] Failed to load '{path}': {exception.GetType().Name} {exception.Message}");
        }
    }
}
