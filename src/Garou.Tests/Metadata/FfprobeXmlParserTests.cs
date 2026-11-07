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
          <format duration="12.500000" />
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
    public void WhenVideoStreamPresentThenCodecIsParsed()
    {
        var metadata = FfprobeXmlParser.Parse(VideoXml);

        Assert.Equal("h264", metadata.Codec);
    }

    [Fact]
    public void WhenFormatDurationPresentThenDurationIsParsed()
    {
        var metadata = FfprobeXmlParser.Parse(VideoXml);

        Assert.Equal(12.5, metadata.DurationSeconds);
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
}
