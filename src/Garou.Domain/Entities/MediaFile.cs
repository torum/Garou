using Garou.Domain.Enums;

namespace Garou.Domain.Entities;

public sealed class MediaFile
{
    public long Id { get; init; }

    public required string Path { get; init; }

    public required string FileName { get; init; }

    public required string Extension { get; init; }

    public MediaType MediaType { get; init; }

    public long? FileSize { get; init; }

    public string? MimeType { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public double? DurationSeconds { get; init; }

    public string? HashValue { get; init; }

    public string? ThumbnailPath { get; init; }

    public DateTime? CreatedDateUtc { get; init; }

    public DateTime? ModifiedDateUtc { get; init; }

    public DateTime? LastIndexedUtc { get; init; }

    public long? FolderId { get; init; }

    public MediaFile WithThumbnailPath(string? thumbnailPath) => new()
    {
        Id = Id,
        Path = Path,
        FileName = FileName,
        Extension = Extension,
        MediaType = MediaType,
        FileSize = FileSize,
        MimeType = MimeType,
        Width = Width,
        Height = Height,
        DurationSeconds = DurationSeconds,
        HashValue = HashValue,
        ThumbnailPath = thumbnailPath,
        CreatedDateUtc = CreatedDateUtc,
        ModifiedDateUtc = ModifiedDateUtc,
        LastIndexedUtc = LastIndexedUtc,
        FolderId = FolderId
    };
}
