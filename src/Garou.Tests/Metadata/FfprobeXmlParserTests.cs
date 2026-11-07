using Garou.Infrastructure.Metadata;

namespace Garou.Tests.Metadata;

public sealed class FfprobeXmlParserTests
{
    private const string VideoXml = """
        <ffprobe>
          <streams>
            <stream index="0" codec_type="audio" codec_name="aac" />
            <stream index="1" codec_type="video" codec_name="h264" width="1920" height="1080" />
          </streams>
          <format duration="12.500000">
            <tags>
              <tag key="ARTIST" value="Sample Artist" />
              <tag key="album" value="Sample Album" />
              <tag key="track" value="3/12" />
              <tag key="make" value="Sample Camera" />
              <tag key="model" value="Sample Model" />
            </tags>
          </format>
        </ffprobe>
        """;

    [Fact]
    public void WhenVideoStreamPresentThenWidthIsParsed()
    {
        var metadata = FfprobeXmlParser.Parse(VideoXml);

        Assert.Equal(1920, metadata.Width);
    }

    [Fact]
    public void WhenVideoStreamPresentThenHeightIsParsed()
    {
        var metadata = FfprobeXmlParser.Parse(VideoXml);

        Assert.Equal(1080, metadata.Height);
    }

    [Fact]
    public void WhenVideoAndAudioStreamsArePresentThenBothCodecsAreParsed()
    {
        var metadata = FfprobeXmlParser.Parse(VideoXml);

        Assert.Equal("h264, aac", metadata.Codec);
    }

    [Fact]
    public void WhenFormatDurationPresentThenDurationIsParsed()
    {
        var metadata = FfprobeXmlParser.Parse(VideoXml);

        Assert.Equal(12.5, metadata.DurationSeconds);
    }

    [Fact]
    public void WhenFormatTagsArePresentThenDescriptiveMetadataIsParsed()
    {
        var metadata = FfprobeXmlParser.Parse(VideoXml);

        Assert.Equal(
            ("Sample Artist", "Sample Album", 3, "Sample Camera", "Sample Model"),
            (metadata.Artist, metadata.Album, metadata.Track, metadata.CameraMake, metadata.CameraModel));
    }

    [Fact]
    public void WhenNoVideoStreamPresentThenVideoFieldsAreNull()
    {
        const string audioOnly = """
            <ffprobe>
              <streams>
                <stream index="0" codec_type="audio" codec_name="aac" />
              </streams>
              <format duration="3.0" />
            </ffprobe>
            """;

        var metadata = FfprobeXmlParser.Parse(audioOnly);

        Assert.Null(metadata.Width);
    }

    [Fact]
    public void WhenOnlyAudioStreamIsPresentThenAudioCodecIsParsed()
    {
        const string audioOnly = """
            <ffprobe>
              <streams>
                <stream index="0" codec_type="audio" codec_name="aac" />
              </streams>
              <format duration="3.0" />
            </ffprobe>
            """;

        var metadata = FfprobeXmlParser.Parse(audioOnly);

        Assert.Equal("aac", metadata.Codec);
    }

    [Fact]
    public void WhenFormatAndStreamTagsArePresentThenAllTagsArePreservedWithTheirScope()
    {
        var metadata = FfprobeXmlParser.Parse(VideoXml);

        Assert.Equal(
            [
                ("Format", "ARTIST", "Sample Artist"),
                ("Format", "album", "Sample Album"),
                ("Format", "track", "3/12"),
                ("Format", "make", "Sample Camera"),
                ("Format", "model", "Sample Model")
            ],
            metadata.Tags.Select(tag => (tag.Category, tag.Name, tag.Value)).ToArray());
    }

    [Fact]
    public void WhenSameTagExistsInFormatAndMultipleStreamsThenEveryOccurrenceIsPreserved()
    {
        const string xml = """
            <ffprobe>
              <streams>
                <stream index="0" codec_type="audio">
                  <tags><tag key="language" value="eng" /></tags>
                </stream>
                <stream index="1" codec_type="video">
                  <tags><tag key="language" value="und" /></tags>
                </stream>
              </streams>
              <format><tags><tag key="language" value="format-value" /></tags></format>
            </ffprobe>
            """;

        var metadata = FfprobeXmlParser.Parse(xml);

        Assert.Equal(
            [
                ("Format", "language", "format-value"),
                ("Stream 0 (audio)", "language", "eng"),
                ("Stream 1 (video)", "language", "und")
            ],
            metadata.Tags.Select(tag => (tag.Category, tag.Name, tag.Value)).ToArray());
    }
}
