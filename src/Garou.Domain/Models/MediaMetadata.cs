namespace Garou.Domain.Models;

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
}
