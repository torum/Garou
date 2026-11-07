namespace Garou.Domain.Models;

public sealed record MediaMetadataTag(string Category, string Name, string Value);

public sealed class MediaMetadata
{
    public int? Width { get; init; }

    public int? Height { get; init; }

    public double? DurationSeconds { get; init; }

    public string? Artist { get; init; }

    public string? Album { get; init; }

    public int? Track { get; init; }

    public string? CameraMake { get; init; }

    public string? CameraModel { get; init; }

    public double? Latitude { get; init; }

    public double? Longitude { get; init; }

    public string? Codec { get; init; }

    public string? DateTaken { get; init; }

    public string? IsoSpeed { get; init; }

    public string? Aperture { get; init; }

    public string? ShutterSpeed { get; init; }

    public string? ExposureCompensation { get; init; }

    public string? FocalLength { get; init; }

    public string? FocalLength35mm { get; init; }

    public string? Flash { get; init; }

    public string? ExposureProgram { get; init; }

    public string? MeteringMode { get; init; }

    public string? WhiteBalance { get; init; }

    public string? LensMake { get; init; }

    public string? LensModel { get; init; }

    public string? DigitalZoom { get; init; }

    public IReadOnlyList<MediaMetadataTag> Tags { get; init; } = [];
}
