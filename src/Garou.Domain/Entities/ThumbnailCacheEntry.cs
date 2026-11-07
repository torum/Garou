namespace Garou.Domain.Entities;

public sealed class ThumbnailCacheEntry
{
    public required string ThumbnailPath { get; init; }

    public long SizeBytes { get; init; }

    public DateTime LastAccessUtc { get; init; }
}
