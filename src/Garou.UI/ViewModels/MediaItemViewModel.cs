using Garou.Domain.Entities;
using Garou.Domain.Enums;
using Garou.UI.Converters;

namespace Garou.ViewModels;

public sealed class MediaItemViewModel : IDisposable
{
    public MediaItemViewModel(MediaFile file)
    {
        File = file;
        Thumbnail = file.ThumbnailPath is { } thumbnailPath && System.IO.File.Exists(thumbnailPath)
            ? new ThumbnailImage(thumbnailPath)
            : null;
    }

    public MediaFile File { get; }

    public ThumbnailImage? Thumbnail { get; }

    public string FileName => File.FileName;

    public MediaType MediaType => File.MediaType;

    public void Dispose() => Thumbnail?.Dispose();
}
