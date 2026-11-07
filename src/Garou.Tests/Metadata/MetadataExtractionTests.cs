using Garou.Domain.Abstractions;
using Garou.Domain.Models;
using Garou.Infrastructure.Metadata;

namespace Garou.Tests.Metadata;

public sealed class MetadataExtractionTests
{
    [Fact]
    public async Task WhenFfprobeIsMissingThenVideoMetadataIsEmpty()
    {
        var provider = new VideoMetadataProvider(Path.Combine(Path.GetTempPath(), $"missing-ffprobe-{Guid.NewGuid():N}"));

        var metadata = await provider.ReadAsync("clip.mp4", CancellationToken.None);

        Assert.Null(metadata.Width);
    }

    [Fact]
    public void WhenVideoExtensionSupportedThenVideoProviderSupportsMp4()
    {
        var provider = new VideoMetadataProvider("ffprobe");

        Assert.True(provider.Supports(".mp4"));
    }

    [Fact]
    public async Task WhenNoProviderSupportsExtensionThenEmptyMetadataIsReturned()
    {
        var service = new MetadataExtractionService([new FakeProvider(".jpg")]);

        var metadata = await service.ExtractAsync("file.xyz", CancellationToken.None);

        Assert.Null(metadata.Width);
    }

    [Fact]
    public async Task WhenProviderSupportsExtensionThenItsMetadataIsReturned()
    {
        var service = new MetadataExtractionService([new FakeProvider(".jpg")]);

        var metadata = await service.ExtractAsync("photo.jpg", CancellationToken.None);

        Assert.Equal(640, metadata.Width);
    }

    private sealed class FakeProvider(string extension) : IMediaMetadataProvider
    {
        public bool Supports(string ext) => string.Equals(ext, extension, StringComparison.OrdinalIgnoreCase);

        public Task<MediaMetadata> ReadAsync(string filePath, CancellationToken cancellationToken) =>
            Task.FromResult(new MediaMetadata { Width = 640 });
    }
}
