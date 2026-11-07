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

        return ExtractMetadata(ImageMetadataReader.ReadMetadata(filePath));
    }

    internal static MediaMetadata ExtractMetadata(IReadOnlyList<MetadataExtractor.Directory> directories)
    {
        var exif = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        var exifDirectories = directories.OfType<ExifDirectoryBase>().ToList();
        var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        var gps = directories.OfType<GpsDirectory>().FirstOrDefault();
        var jpeg = directories.OfType<MetadataExtractor.Formats.Jpeg.JpegDirectory>().FirstOrDefault();

        var width = subIfd?.TryGetInt32(ExifDirectoryBase.TagExifImageWidth, out var w) == true ? w
            : jpeg?.TryGetInt32(MetadataExtractor.Formats.Jpeg.JpegDirectory.TagImageWidth, out var jw) == true ? jw
            : (int?)null;
        var height = subIfd?.TryGetInt32(ExifDirectoryBase.TagExifImageHeight, out var h) == true ? h
            : jpeg?.TryGetInt32(MetadataExtractor.Formats.Jpeg.JpegDirectory.TagImageHeight, out var jh) == true ? jh
            : (int?)null;

        if (exif?.TryGetInt32(ExifDirectoryBase.TagOrientation, out var orientation) == true
            && orientation is 5 or 6 or 7 or 8)
        {
            (width, height) = (height, width);
        }

        var location = gps?.GetGeoLocation();

        return new MediaMetadata
        {
            Width = width,
            Height = height,
            Artist = exif?.GetString(ExifDirectoryBase.TagArtist),
            CameraMake = exif?.GetString(ExifDirectoryBase.TagMake),
            CameraModel = exif?.GetString(ExifDirectoryBase.TagModel),
            Latitude = location?.Latitude,
            Longitude = location?.Longitude,
            DateTaken = ReadExifDescription(exifDirectories, "Date/Time Original"),
            IsoSpeed = ReadExifDescription(exifDirectories, "ISO Speed Ratings", "Photographic Sensitivity"),
            Aperture = ReadExifDescription(exifDirectories, "F-Number", "F Number", "Aperture Value", "Aperture"),
            ShutterSpeed = ReadExifDescription(exifDirectories, "Exposure Time", "Shutter Speed"),
            ExposureCompensation = ReadExifDescription(exifDirectories, "Exposure Bias Value", "Exposure Compensation"),
            FocalLength = ReadExifDescription(exifDirectories, "Focal Length"),
            FocalLength35mm = ReadExifDescription(exifDirectories, "Focal Length 35", "35mm Film Equiv Focal Length", "35 mm Film Equiv Focal Length"),
            Flash = ReadExifDescription(exifDirectories, "Flash"),
            ExposureProgram = ReadExifDescription(exifDirectories, "Exposure Program"),
            MeteringMode = ReadExifDescription(exifDirectories, "Metering Mode"),
            WhiteBalance = ReadExifDescription(exifDirectories, "White Balance Mode", "White Balance"),
            LensMake = ReadExifDescription(exifDirectories, "Lens Make"),
            LensModel = ReadExifDescription(exifDirectories, "Lens Model"),
            DigitalZoom = ReadExifDescription(exifDirectories, "Digital Zoom Ratio"),
            Tags = directories
                .SelectMany(directory => directory.Tags.Select(tag => new MediaMetadataTag(
                    directory.Name,
                    tag.Name,
                    tag.Description ?? string.Empty)))
                .ToArray()
        };
    }

    private static string? ReadExifDescription(IEnumerable<ExifDirectoryBase> directories, params string[] tagNames)
    {
        var names = new HashSet<string>(tagNames, StringComparer.OrdinalIgnoreCase);
        var description = directories
            .SelectMany(directory => directory.Tags)
            .FirstOrDefault(tag => names.Contains(tag.Name))?.Description;

        return string.IsNullOrWhiteSpace(description) ? null : description;
    }
}
