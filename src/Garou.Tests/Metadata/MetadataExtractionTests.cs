using Garou.Domain.Abstractions;
using Garou.Domain.Models;
using Garou.Infrastructure.Metadata;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

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
    public void WhenImageMetadataContainsTagsThenAllDirectoryTagsAreReturned()
    {
        var exif = new ExifSubIfdDirectory();
        exif.Set(0x9003, "2024:06:01 15:30:45");
        exif.Set(0x8827, 100);

        var metadata = ImageMetadataProvider.ExtractMetadata([exif]);

        Assert.Contains(metadata.Tags, tag => tag.Name == "Date/Time Original" && tag.Value == "2024:06:01 15:30:45");
        Assert.Contains(metadata.Tags, tag => tag.Name == "ISO Speed Ratings" && tag.Value == "100");
    }

    [Fact]
    public void WhenVideoExtensionSupportedThenVideoProviderSupportsMp4()
    {
        var provider = new VideoMetadataProvider("ffprobe");

        Assert.True(provider.Supports(".mp4"));
    }

    [Fact]
    public void WhenAudioExtensionSupportedThenFfprobeProviderSupportsMp3()
    {
        var provider = new VideoMetadataProvider("ffprobe");

        Assert.True(provider.Supports(".mp3"));
    }

    [Fact]
    public void WhenNewVideoExtensionIsIndexedThenFfprobeProviderSupportsItForMetadata()
    {
        var provider = new VideoMetadataProvider("ffprobe");

        Assert.True(provider.Supports(".flv"));
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

    [Fact]
    public void WhenExifCaptureTagsArePresentThenCameraSettingsAreRead()
    {
        var subIfd = new ExifSubIfdDirectory();
        subIfd.Set(0x9003, "2024:06:01 15:30:45");
        subIfd.Set(0x8827, 100);
        subIfd.Set(0x829D, new Rational(28, 10));
        subIfd.Set(0x829A, new Rational(1, 125));
        subIfd.Set(0x9204, new Rational(-1, 3));
        subIfd.Set(0x920A, new Rational(50, 1));
        subIfd.Set(0xA405, 75);
        subIfd.Set(0x9209, 0);
        subIfd.Set(0x8822, 3);
        subIfd.Set(0x9207, 5);
        subIfd.Set(0xA403, 0);
        subIfd.Set(0xA433, "Lens maker");
        subIfd.Set(0xA434, "Lens model");
        subIfd.Set(0xA404, new Rational(2, 1));

        var metadata = ImageMetadataProvider.ExtractMetadata([subIfd]);

        Assert.Equal(
            ("2024:06:01 15:30:45", "100", "f/2.8", "1/125 sec", "50 mm"),
            (metadata.DateTaken, metadata.IsoSpeed, metadata.Aperture, metadata.ShutterSpeed, metadata.FocalLength));
        Assert.All(
            [metadata.ExposureCompensation, metadata.FocalLength35mm, metadata.Flash, metadata.ExposureProgram,
                metadata.MeteringMode, metadata.WhiteBalance, metadata.LensMake, metadata.LensModel, metadata.DigitalZoom],
            value => Assert.False(string.IsNullOrWhiteSpace(value), string.Join(" | ", subIfd.Tags.Select(tag => $"{tag.Type:X4}: {tag.Name} = {tag.Description}"))));
    }

    private sealed class FakeProvider(string extension) : IMediaMetadataProvider
    {
        public bool Supports(string ext) => string.Equals(ext, extension, StringComparison.OrdinalIgnoreCase);

        public Task<MediaMetadata> ReadAsync(string filePath, CancellationToken cancellationToken) =>
            Task.FromResult(new MediaMetadata { Width = 640 });
    }
}
