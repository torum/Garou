using System.Globalization;
using System.Xml.Linq;
using Garou.Domain.Models;

namespace Garou.Infrastructure.Metadata;

internal static class FfprobeXmlParser
{
    public static MediaMetadata Parse(string xml)
    {
        var document = XDocument.Parse(xml);
        var streams = document.Root?.Element("streams")?.Elements("stream").ToList() ?? [];
        var video = streams.FirstOrDefault(s => (string?)s.Attribute("codec_type") == "video");
        var duration = document.Root?.Element("format")?.Attribute("duration");

        return new MediaMetadata
        {
            Width = ParseInt(video?.Attribute("width")?.Value),
            Height = ParseInt(video?.Attribute("height")?.Value),
            Codec = (string?)video?.Attribute("codec_name"),
            DurationSeconds = duration is null ? null
                : double.TryParse(duration.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds
                : null
        };
    }

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;
}
