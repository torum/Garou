using System.Text;
using System.Xml;
using System.Xml.Linq;
using Garou.Domain.Models;

namespace Garou.Infrastructure.Metadata;

public sealed class XnfoStore
{
    public string GetPath(string mediaPath) => mediaPath + ".xnfo";

    public async Task<XnfoMetadata?> LoadAsync(string mediaPath, CancellationToken cancellationToken)
    {
        var document = await ReadAsync(GetPath(mediaPath), cancellationToken);
        if (document is null)
        {
            return null;
        }

        var root = Validate(document);
        return new XnfoMetadata
        {
            Title = ReadValue(root, "title"),
            Subtitle = ReadValue(root, "subtitle"),
            SortTitle = ReadValue(root, "sorttitle"),
            Publisher = ReadValue(root, "publisher"),
            StudioLabel = ReadValue(root, "studiolabel"),
            ReleaseDate = ReadValue(root, "releasedate"),
            ContentRating = ReadValue(root, "contentrating"),
            Description = ReadValue(root, "description"),
            Comments = ReadValue(root, "comments"),
            CoverArt = ReadCoverArt(root),
            Performers = root.Element("performers") is null
                ? ReadList(root, "actors", "actor")
                : ReadList(root, "performers", "performer"),
            Countries = ReadList(root, "countries", "country"),
            Tags = ReadList(root, "tags", "tag"),
            Genres = ReadList(root, "genres", "genre"),
            Categories = ReadList(root, "categories", "category")
        };
    }

    public async Task SaveAsync(string mediaPath, XnfoMetadata metadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var path = GetPath(mediaPath);
        var document = await ReadAsync(path, cancellationToken);
        var existing = document is not null;
        XElement root;
        if (document is null)
        {
            root = new XElement("xnfo", new XAttribute("type", "file"), new XAttribute("version", "1"));
            document = new XDocument(root);
        }
        else
        {
            root = Validate(document);
            ConvertLegacyActors(root);
            if (root.Attribute("version") is null)
            {
                root.SetAttributeValue("version", "1");
            }
        }

        WriteValue(root, "title", metadata.Title);
        WriteValue(root, "subtitle", metadata.Subtitle);
        WriteValue(root, "sorttitle", metadata.SortTitle);
        WriteValue(root, "publisher", metadata.Publisher);
        WriteValue(root, "studiolabel", metadata.StudioLabel);
        WriteValue(root, "releasedate", metadata.ReleaseDate);
        WriteValue(root, "contentrating", metadata.ContentRating);
        WriteValue(root, "description", metadata.Description);
        WriteValue(root, "comments", metadata.Comments);
        WriteValue(root, "coverart", metadata.CoverArt);
        WriteList(root, "performers", "performer", metadata.Performers);
        WriteList(root, "countries", "country", metadata.Countries);
        WriteList(root, "tags", "tag", metadata.Tags);
        WriteList(root, "genres", "genre", metadata.Genres);
        WriteList(root, "categories", "category", metadata.Categories);

        var fullPath = Path.GetFullPath(path);
        var temporaryPath = Path.Combine(Path.GetDirectoryName(fullPath)!, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        var created = false;
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                created = true;
                var settings = new XmlWriterSettings { Async = true, Encoding = new UTF8Encoding(false), Indent = false, NewLineHandling = NewLineHandling.Entitize };
                using var writer = XmlWriter.Create(stream, settings);
                await document.SaveAsync(writer, cancellationToken);
                await writer.FlushAsync();
                await stream.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (existing)
            {
                File.Replace(temporaryPath, fullPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, fullPath);
            }
        }
        finally
        {
            if (created && File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    internal static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);

    private static async Task<XDocument?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path) && !SidecarEntryExists(path))
        {
            return null;
        }

        FileStream stream;
        try
        {
            stream = OpenRead(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        await using (stream)
        {
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit });
            return await XDocument.LoadAsync(reader, LoadOptions.PreserveWhitespace, cancellationToken);
        }
    }

    private static bool SidecarEntryExists(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        var fileName = Path.GetFileName(fullPath);
        var pattern = fileName.Contains('*') || fileName.Contains('?') ? "*" : fileName;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        try
        {
            return Directory.EnumerateFiles(directory, pattern, new EnumerationOptions
            {
                AttributesToSkip = 0,
                IgnoreInaccessible = false,
                MatchType = MatchType.Simple
            }).Any(entry => string.Equals(Path.GetFileName(entry), fileName, comparison));
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static XElement Validate(XDocument document)
    {
        var root = document.Root;
        if (root is null || root.Name != "xnfo" || (string?)root.Attribute("type") != "file" ||
            (root.Attribute("version") is { } version && version.Value != "1"))
        {
            throw new InvalidDataException("Unsupported or invalid xnfo format; expected xnfo type=file with version 1 or no version.");
        }

        return root;
    }

    private static string ReadCoverArt(XElement root)
    {
        if (root.Element("coverart") is { } coverArt)
        {
            return ReadText(coverArt);
        }

        if (root.Element("thumbnail") is not { } thumbnail ||
            (thumbnail.Attribute("type") is { } type && type.Value != "Uri"))
        {
            return string.Empty;
        }

        var value = ReadText(thumbnail);
        return IsLocalRelativePath(value) ? value : string.Empty;
    }

    private static bool IsLocalRelativePath(string value) =>
        !string.IsNullOrWhiteSpace(value) && value == value.Trim() &&
        value[0] != '/' && value[0] != '\\' && !value.Contains(':') &&
        !value.Any(char.IsControl) && !Path.IsPathRooted(value) &&
        !Uri.TryCreate(value, UriKind.Absolute, out _);

    private static void ConvertLegacyActors(XElement root)
    {
        if (root.Element("performers") is not null || root.Element("actors") is not { } actors)
        {
            return;
        }

        actors.Name = "performers";
        foreach (var actor in actors.Elements("actor"))
        {
            actor.Name = "performer";
        }
    }

    private static string ReadValue(XElement parent, string name) =>
        parent.Element(name) is { } element ? ReadText(element) : string.Empty;

    private static string ReadText(XElement element) => string.Concat(element.Nodes().OfType<XText>().Select(text => text.Value));

    private static List<string> ReadList(XElement root, string containerName, string itemName) =>
        root.Element(containerName)?.Elements(itemName).Select(ReadText).ToList() ?? [];

    private static void WriteValue(XElement parent, string name, string value)
    {
        var element = parent.Element(name);
        if (element is null)
        {
            parent.Add(new XElement(name, value));
            return;
        }

        SetText(element, value);
    }

    private static void SetText(XElement element, string value)
    {
        var texts = element.Nodes().OfType<XText>().ToList();
        if (texts.Count == 0)
        {
            element.AddFirst(new XText(value));
            return;
        }

        texts[0].ReplaceWith(new XText(value));
        foreach (var text in texts.Skip(1))
        {
            text.Remove();
        }
    }

    private static void WriteList(XElement root, string containerName, string itemName, List<string> values)
    {
        var container = root.Element(containerName);
        if (container is null)
        {
            container = new XElement(containerName);
            root.Add(container);
        }

        var items = container.Elements(itemName).ToList();
        for (var index = 0; index < values.Count; index++)
        {
            if (index < items.Count)
            {
                SetText(items[index], values[index]);
            }
            else
            {
                container.Add(new XElement(itemName, values[index]));
            }
        }

        foreach (var item in items.Skip(values.Count))
        {
            if (item.HasAttributes || item.Elements().Any())
            {
                throw new InvalidDataException($"Cannot remove {itemName} with unrecognized attributes or elements.");
            }

            item.Remove();
        }
    }
}
