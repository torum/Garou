using Garou.Domain.Abstractions;
using Garou.Domain.Models;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace Garou.Infrastructure.Metadata;

public sealed class ImageMetadataProvider : IMediaMetadataProvider
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic"
    };

    public bool Supports(string extension) => SupportedExtensions.Contains(extension);

    public Task<MediaMetadata> ReadAsync(string filePath, CancellationToken cancellationToken) =>
        Task.Run(() => Read(filePath, cancellationToken), cancellationToken);

    private static MediaMetadata Read(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var directories = ImageMetadataReader.ReadMetadata(filePath);
        var exif = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        var gps = directories.OfType<GpsDirectory>().FirstOrDefault();
        var jpeg = directories.OfType<MetadataExtractor.Formats.Jpeg.JpegDirectory>().FirstOrDefault();

        var width = subIfd?.TryGetInt32(ExifDirectoryBase.TagExifImageWidth, out var w) == true ? w
            : jpeg?.TryGetInt32(MetadataExtractor.Formats.Jpeg.JpegDirectory.TagImageWidth, out var jw) == true ? jw
            : (int?)null;
        var height = subIfd?.TryGetInt32(ExifDirectoryBase.TagExifImageHeight, out var h) == true ? h
            : jpeg?.TryGetInt32(MetadataExtractor.Formats.Jpeg.JpegDirectory.TagImageHeight, out var jh) == true ? jh
            : (int?)null;

        var location = gps?.GetGeoLocation();

        return new MediaMetadata
        {
            Width = width,
            Height = height,
            CameraMake = exif?.GetString(ExifDirectoryBase.TagMake),
            CameraModel = exif?.GetString(ExifDirectoryBase.TagModel),
            Latitude = location?.Latitude,
            Longitude = location?.Longitude
        };
    }
}
