using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;

namespace Garou.UI.Converters;

public sealed class ThumbnailImage : INotifyPropertyChanged, IDisposable
{
    private Bitmap? _bitmap;
    private bool _disposed;

    public ThumbnailImage(string path)
    {
        _ = LoadAsync(path);
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
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[ThumbnailImage] Failed to load '{path}': {exception.GetType().Name} {exception.Message}");
        }
    }
}
