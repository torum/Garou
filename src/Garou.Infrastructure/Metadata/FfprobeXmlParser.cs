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
        var format = document.Root?.Element("format");
        var duration = format?.Attribute("duration");

        return new MediaMetadata
        {
            Width = ParseInt(video?.Attribute("width")?.Value),
            Height = ParseInt(video?.Attribute("height")?.Value),
            Codec = ReadCodecs(streams),
            Artist = ReadMetadataTag(format, streams, "artist"),
            Album = ReadMetadataTag(format, streams, "album"),
            Track = ParseTrack(ReadMetadataTag(format, streams, "track")),
            CameraMake = ReadMetadataTag(format, streams, "make"),
            CameraModel = ReadMetadataTag(format, streams, "model"),
            Tags = ReadAllTags(format, streams),
            DurationSeconds = duration is null ? null
                : double.TryParse(duration.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds
                : null
        };
    }

    private static string? ReadMetadataTag(XElement? format, IReadOnlyList<XElement> streams, string name)
    {
        var value = ReadTag(format, name);
        if (value is not null)
        {
            return value;
        }

        foreach (var stream in streams)
        {
            value = ReadTag(stream, name);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    private static string? ReadCodecs(IEnumerable<XElement> streams)
    {
        var codecs = streams
            .OrderBy(stream => (string?)stream.Attribute("codec_type") == "video" ? 0 : 1)
            .Select(stream => (string?)stream.Attribute("codec_name"))
            .Where(codec => !string.IsNullOrWhiteSpace(codec))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return codecs.Length == 0 ? null : string.Join(", ", codecs);
    }

    private static string? ReadTag(XElement? element, string name)
    {
        var tag = element?.Element("tags")?.Elements("tag")
            .FirstOrDefault(tag => string.Equals((string?)tag.Attribute("key"), name, StringComparison.OrdinalIgnoreCase));
        var value = ((string?)tag?.Attribute("value") ?? tag?.Value)?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static IReadOnlyList<MediaMetadataTag> ReadAllTags(XElement? format, IReadOnlyList<XElement> streams)
    {
        var tags = new List<MediaMetadataTag>();
        AddTags(format, "Format", tags);

        foreach (var stream in streams)
        {
            var index = (string?)stream.Attribute("index") ?? "?";
            var type = (string?)stream.Attribute("codec_type") ?? "unknown";
            AddTags(stream, $"Stream {index} ({type})", tags);
        }

        return tags;
    }

    private static void AddTags(XElement? element, string category, ICollection<MediaMetadataTag> tags)
    {
        foreach (var tag in element?.Element("tags")?.Elements("tag") ?? [])
        {
            var name = (string?)tag.Attribute("key");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var value = ((string?)tag.Attribute("value") ?? tag.Value).Trim();
            tags.Add(new MediaMetadataTag(category, name, value));
        }
    }

    private static int? ParseTrack(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var separator = value.IndexOf('/');
        var trackNumber = separator >= 0 ? value[..separator] : value;
        return int.TryParse(trackNumber.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var track)
            ? track
            : null;
    }

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;
}
