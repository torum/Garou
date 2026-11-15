using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;

namespace Garou.UI.Models;

public sealed class ThumbnailImage : INotifyPropertyChanged, IDisposable
{
    private Bitmap? _bitmap;
    private ThumbnailLoadState _state = ThumbnailLoadState.Loading;
    private bool _disposed;
    private bool _primaryFailed;

    public ThumbnailImage(string path, string? fallbackPath = null)
    {
        if (!string.IsNullOrEmpty(path))
        {
            _ = LoadAsync(path, fallbackPath);
        }
    }

    public bool PrimaryFailed
    {
        get => _primaryFailed;
        private set
        {
            if (_primaryFailed != value)
            {
                _primaryFailed = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PrimaryFailed)));
            }
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
        Bitmap = null;
    }

    private async Task LoadAsync(string path, string? fallbackPath)
    {
        try
        {
            var bitmap = await Task.Run(() =>
            {
                using var stream = File.OpenRead(path);
                return new ThumbnailBitmap(stream);
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
            Debug.WriteLine($"[ThumbnailImage] Failed to load '{path}': {exception.GetType().Name} {exception.Message}");
            if (!_disposed && !string.IsNullOrEmpty(fallbackPath) && File.Exists(fallbackPath))
            {
                PrimaryFailed = true;
                await LoadAsync(fallbackPath, null);
            }
            else
            {
                State = ThumbnailLoadState.Failed;
            }
        }
    }

    // A transitioning or recycled visual can retain the bitmap after its model is disposed.
    private sealed class ThumbnailBitmap : Bitmap
    {
        private readonly bool _initialized;

        public ThumbnailBitmap(Stream stream) : base(stream)
        {
            _initialized = true;
        }

        ~ThumbnailBitmap()
        {
            if (_initialized)
            {
                base.Dispose();
            }
        }

        public override void Dispose()
        {
            base.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
