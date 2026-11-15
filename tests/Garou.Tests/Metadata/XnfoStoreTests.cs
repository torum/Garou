using System.Xml;
using System.Xml.Linq;
using Garou.Domain.Models;
using Garou.Infrastructure.Metadata;

namespace Garou.Tests.Metadata;

public sealed class XnfoStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Directory.GetCurrentDirectory(), $"xnfo-tests-{Guid.NewGuid():N}");
    private readonly XnfoStore _store = new();
    private string MediaPath => Path.Combine(_directory, "video.mp4");
    private string SidecarPath => _store.GetPath(MediaPath);

    public XnfoStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task MissingSidecarReturnsNullWithoutCreatingAnything()
    {
        Assert.Equal(MediaPath + ".xnfo", SidecarPath);
        Assert.Null(await _store.LoadAsync(MediaPath, CancellationToken.None));
        Assert.Empty(Directory.GetFileSystemEntries(_directory));
        Assert.Null(await _store.LoadAsync(Path.Combine(_directory, "absent", "file"), CancellationToken.None));
    }

    [Fact]
    public async Task MissingSidecarDoesNotThrowFirstChanceFileNotFound()
    {
        var missingExceptions = 0;
        void OnFirstChanceException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
        {
            if (args.Exception is FileNotFoundException exception && exception.FileName == SidecarPath)
                Interlocked.Increment(ref missingExceptions);
        }

        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
        try
        {
            Assert.Null(await _store.LoadAsync(MediaPath, CancellationToken.None));
            Assert.Equal(0, Volatile.Read(ref missingExceptions));
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChanceException;
        }
    }

    [Fact]
    public async Task RoundTripPreservesAllFieldsAndRepeatedListElements()
    {
        var metadata = new XnfoMetadata
        {
            Title = "title", Subtitle = "subtitle", SortTitle = "sort", Publisher = "publisher",
            StudioLabel = "studio", ReleaseDate = "2026-01-02", ContentRating = "rating",
            Description = "description", Comments = "comments", CoverArt = "cover.jpg",
            Performers = ["first", "second"], Countries = ["Japan", "France"], Tags = ["a", "b"],
            Genres = ["drama", "comedy"], Categories = ["one", "two"]
        };

        await _store.SaveAsync(MediaPath, metadata, CancellationToken.None);
        var result = Assert.IsType<XnfoMetadata>(await _store.LoadAsync(MediaPath, CancellationToken.None));
        Assert.Equal(metadata.Title, result.Title);
        Assert.Equal(metadata.Subtitle, result.Subtitle);
        Assert.Equal(metadata.SortTitle, result.SortTitle);
        Assert.Equal(metadata.Publisher, result.Publisher);
        Assert.Equal(metadata.StudioLabel, result.StudioLabel);
        Assert.Equal(metadata.ReleaseDate, result.ReleaseDate);
        Assert.Equal(metadata.ContentRating, result.ContentRating);
        Assert.Equal(metadata.Description, result.Description);
        Assert.Equal(metadata.Comments, result.Comments);
        Assert.Equal(metadata.CoverArt, result.CoverArt);
        Assert.Equal(metadata.Performers, result.Performers);
        Assert.Equal(metadata.Countries, result.Countries);
        Assert.Equal(metadata.Tags, result.Tags);
        Assert.Equal(metadata.Genres, result.Genres);
        Assert.Equal(metadata.Categories, result.Categories);

        var root = XDocument.Load(SidecarPath).Root!;
        Assert.Equal("xnfo", root.Name.LocalName);
        Assert.Equal("file", (string?)root.Attribute("type"));
        Assert.Equal("1", (string?)root.Attribute("version"));
        Assert.Equal(new[] { "first", "second" }, root.Element("performers")!.Elements("performer").Select(e => e.Value));
        Assert.Equal(2, root.Element("countries")!.Elements("country").Count());
        Assert.Equal(2, root.Element("tags")!.Elements("tag").Count());
        Assert.Equal(2, root.Element("genres")!.Elements("genre").Count());
        Assert.Equal(2, root.Element("categories")!.Elements("category").Count());
    }

    [Fact]
    public async Task SpecialCharactersAndEmptyDefaultsRoundTrip()
    {
        var value = "A & B <C> \"D\" 'E' 日本語\nnext\r\nlast\rend";
        var metadata = new XnfoMetadata { Title = value, Description = value, Tags = [value, ""] };
        await _store.SaveAsync(MediaPath, metadata, CancellationToken.None);

        var result = Assert.IsType<XnfoMetadata>(await _store.LoadAsync(MediaPath, CancellationToken.None));
        Assert.Equal(value, result.Title);
        Assert.Equal(value, result.Description);
        Assert.Equal(metadata.Tags, result.Tags);
        Assert.Equal(string.Empty, result.Subtitle);
        Assert.Empty(result.Performers);
        Assert.Contains("&amp;", await File.ReadAllTextAsync(SidecarPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EditingPreservesUnknownElementsAndAttributesAtEveryLevel()
    {
        const string xml = "<xnfo type=\"file\" version=\"1\" custom=\"root\" xmlns:x=\"urn:extension\"><title custom=\"scalar\">old<x:extra>nested</x:extra></title><performers custom=\"list\"><performer custom=\"item\">old<x:extra>item extension</x:extra></performer><x:extra>list extension</x:extra></performers><x:extra custom=\"yes\">root extension</x:extra></xnfo>";
        await File.WriteAllTextAsync(SidecarPath, xml, TestContext.Current.CancellationToken);
        var metadata = Assert.IsType<XnfoMetadata>(await _store.LoadAsync(MediaPath, CancellationToken.None));
        Assert.Equal("old", metadata.Title);
        metadata.Title = "new";
        metadata.Performers[0] = "new performer";
        metadata.Performers.Add("another");
        await _store.SaveAsync(MediaPath, metadata, CancellationToken.None);

        var root = XDocument.Load(SidecarPath).Root!;
        XNamespace x = "urn:extension";
        Assert.Equal("root", (string?)root.Attribute("custom"));
        Assert.Equal("scalar", (string?)root.Element("title")!.Attribute("custom"));
        Assert.Equal("nested", (string?)root.Element("title")!.Element(x + "extra"));
        var list = root.Element("performers")!;
        Assert.Equal("list", (string?)list.Attribute("custom"));
        Assert.Equal("item", (string?)list.Element("performer")!.Attribute("custom"));
        Assert.Equal("item extension", (string?)list.Element("performer")!.Element(x + "extra"));
        Assert.Equal("list extension", (string?)list.Element(x + "extra"));
        Assert.Equal("root extension", (string?)root.Element(x + "extra"));
        var loaded = Assert.IsType<XnfoMetadata>(await _store.LoadAsync(MediaPath, CancellationToken.None));
        Assert.Equal("new", loaded.Title);
        Assert.Equal(new[] { "new performer", "another" }, loaded.Performers);
    }

    [Fact]
    public async Task LegacySampleLoadsAndConvertsActorsWithoutLosingExtensions()
    {
        const string xml = "<xnfo type=\"file\" xmlns:x=\"urn:extension\"><title>Sample</title><actors custom=\"list\"><actor custom=\"person\">Alice<x:note>detail</x:note></actor><actor>Bob</actor><x:extra>more</x:extra></actors><thumbnail type=\"Uri\" custom=\"keep\">images/cover.jpg</thumbnail><path>untouched</path></xnfo>";
        await File.WriteAllTextAsync(SidecarPath, xml, TestContext.Current.CancellationToken);
        var metadata = Assert.IsType<XnfoMetadata>(await _store.LoadAsync(MediaPath, CancellationToken.None));
        Assert.Equal("Sample", metadata.Title);
        Assert.Equal(new[] { "Alice", "Bob" }, metadata.Performers);
        Assert.Equal("images/cover.jpg", metadata.CoverArt);
        metadata.Performers[0] = "Alicia";
        await _store.SaveAsync(MediaPath, metadata, CancellationToken.None);

        var root = XDocument.Load(SidecarPath).Root!;
        XNamespace x = "urn:extension";
        Assert.Equal("1", (string?)root.Attribute("version"));
        Assert.Null(root.Element("actors"));
        var performers = root.Element("performers")!;
        Assert.Equal("list", (string?)performers.Attribute("custom"));
        Assert.Equal("person", (string?)performers.Element("performer")!.Attribute("custom"));
        Assert.Equal("detail", (string?)performers.Element("performer")!.Element(x + "note"));
        Assert.Equal("more", (string?)performers.Element(x + "extra"));
        Assert.Equal("keep", (string?)root.Element("thumbnail")!.Attribute("custom"));
        Assert.Equal("images/cover.jpg", (string?)root.Element("thumbnail"));
        Assert.Equal("untouched", (string?)root.Element("path"));
        var reloaded = Assert.IsType<XnfoMetadata>(await _store.LoadAsync(MediaPath, CancellationToken.None));
        Assert.Equal(new[] { "Alicia", "Bob" }, reloaded.Performers);
        Assert.Equal("images/cover.jpg", reloaded.CoverArt);
        await _store.SaveAsync(MediaPath, reloaded, CancellationToken.None);
        Assert.Equal(2, XDocument.Load(SidecarPath).Root!.Element("performers")!.Elements("performer").Count());
    }

    [Theory]
    [InlineData("https://example.com/image.jpg", "")]
    [InlineData("http://example.com/image.jpg", "")]
    [InlineData("file:///C:/image.jpg", "")]
    [InlineData("//server/share/image.jpg", "")]
    [InlineData("\\\\server\\share\\image.jpg", "")]
    [InlineData("C:\\image.jpg", "")]
    [InlineData("C:image.jpg", "")]
    [InlineData("/image.jpg", "")]
    [InlineData("folder\\image.jpg", "folder\\image.jpg")]
    [InlineData("images/cover.jpg", "images/cover.jpg")]
    public async Task LegacyThumbnailOnlyMapsLocalRelativePathsAndIsPreserved(string thumbnail, string expected)
    {
        var original = new XElement("thumbnail", new XAttribute("type", "Uri"), new XAttribute("custom", "keep"), thumbnail);
        var root = new XElement("xnfo", new XAttribute("type", "file"), original, new XElement("path", "legacy path"));
        await File.WriteAllTextAsync(SidecarPath, root.ToString(), TestContext.Current.CancellationToken);
        var metadata = Assert.IsType<XnfoMetadata>(await _store.LoadAsync(MediaPath, CancellationToken.None));
        Assert.Equal(expected, metadata.CoverArt);
        await _store.SaveAsync(MediaPath, metadata, CancellationToken.None);
        var saved = XDocument.Load(SidecarPath).Root!;
        Assert.True(XNode.DeepEquals(original, saved.Element("thumbnail")));
        Assert.Equal("legacy path", (string?)saved.Element("path"));
        Assert.Equal(expected, Assert.IsType<XnfoMetadata>(await _store.LoadAsync(MediaPath, CancellationToken.None)).CoverArt);
    }

    [Fact]
    public async Task UnrecognizedThumbnailTypeRemainsOpaque()
    {
        const string xml = "<xnfo type=\"file\"><thumbnail type=\"embedded\"><path>cover.jpg</path></thumbnail><path>other</path></xnfo>";
        await File.WriteAllTextAsync(SidecarPath, xml, TestContext.Current.CancellationToken);
        var metadata = Assert.IsType<XnfoMetadata>(await _store.LoadAsync(MediaPath, CancellationToken.None));
        Assert.Equal("", metadata.CoverArt);
        await _store.SaveAsync(MediaPath, metadata, CancellationToken.None);
        var root = XDocument.Load(SidecarPath).Root!;
        Assert.Equal("cover.jpg", (string?)root.Element("thumbnail")!.Element("path"));
        Assert.Equal("embedded", (string?)root.Element("thumbnail")!.Attribute("type"));
        Assert.Equal("other", (string?)root.Element("path"));
    }

    [Fact]
    public async Task CanonicalFieldsTakePrecedenceAndLegacyDataIsPreserved()
    {
        const string xml = "<xnfo type=\"file\"><performers><performer>Canonical</performer></performers><actors custom=\"keep\"><actor>Legacy</actor></actors><coverart></coverart><thumbnail type=\"Uri\">cover.jpg</thumbnail></xnfo>";
        await File.WriteAllTextAsync(SidecarPath, xml, TestContext.Current.CancellationToken);
        var metadata = Assert.IsType<XnfoMetadata>(await _store.LoadAsync(MediaPath, CancellationToken.None));
        Assert.Equal(new[] { "Canonical" }, metadata.Performers);
        Assert.Equal("", metadata.CoverArt);
        await _store.SaveAsync(MediaPath, metadata, CancellationToken.None);
        var saved = XDocument.Load(SidecarPath).Root!;
        Assert.Equal("keep", (string?)saved.Element("actors")!.Attribute("custom"));
        Assert.Equal("Legacy", (string?)saved.Element("actors")!.Element("actor"));
        Assert.Equal("cover.jpg", (string?)saved.Element("thumbnail"));
        Assert.Equal(new[] { "Canonical" }, Assert.IsType<XnfoMetadata>(await _store.LoadAsync(MediaPath, CancellationToken.None)).Performers);
    }

    [Fact]
    public async Task VersionOneAlsoAcceptsLegacyAliasesWhenCanonicalFieldsAreAbsent()
    {
        const string xml = "<xnfo type=\"file\" version=\"1\"><actors><actor>Actor</actor></actors><thumbnail type=\"Uri\">cover.jpg</thumbnail></xnfo>";
        await File.WriteAllTextAsync(SidecarPath, xml, TestContext.Current.CancellationToken);
        var metadata = Assert.IsType<XnfoMetadata>(await _store.LoadAsync(MediaPath, CancellationToken.None));
        Assert.Equal(new[] { "Actor" }, metadata.Performers);
        Assert.Equal("cover.jpg", metadata.CoverArt);
        await _store.SaveAsync(MediaPath, metadata, CancellationToken.None);
        var saved = XDocument.Load(SidecarPath).Root!;
        Assert.Null(saved.Element("actors"));
        Assert.Equal("Actor", (string?)saved.Element("performers")!.Element("performer"));
        Assert.Equal("cover.jpg", (string?)saved.Element("thumbnail"));
    }

    [Fact]
    public async Task RemovingExtendedLegacyActorFailsWithoutChangingFile()
    {
        const string xml = "<xnfo type=\"file\"><actors><actor id=\"keep\">Alice</actor></actors></xnfo>";
        await File.WriteAllTextAsync(SidecarPath, xml, TestContext.Current.CancellationToken);
        var metadata = Assert.IsType<XnfoMetadata>(await _store.LoadAsync(MediaPath, CancellationToken.None));
        metadata.Performers.Clear();
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.SaveAsync(MediaPath, metadata, CancellationToken.None));
        Assert.Equal(xml, await File.ReadAllTextAsync(SidecarPath, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("<xnfo")]
    [InlineData("<xnfo type=\"file\" version=\"2\" />")]
    [InlineData("<xnfo type=\"file\" version=\"\" />")]
    [InlineData("<xnfo type=\"directory\" version=\"1\" />")]
    [InlineData("<other type=\"file\" version=\"1\" />")]
    public async Task InvalidOrUnsupportedSidecarIsNeverOverwritten(string xml)
    {
        await File.WriteAllTextAsync(SidecarPath, xml, TestContext.Current.CancellationToken);
        var before = await File.ReadAllBytesAsync(SidecarPath, TestContext.Current.CancellationToken);
        if (xml == "<xnfo")
        {
            await Assert.ThrowsAsync<XmlException>(() => _store.LoadAsync(MediaPath, CancellationToken.None));
            await Assert.ThrowsAsync<XmlException>(() => _store.SaveAsync(MediaPath, new XnfoMetadata(), CancellationToken.None));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => _store.LoadAsync(MediaPath, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidDataException>(() => _store.SaveAsync(MediaPath, new XnfoMetadata(), CancellationToken.None));
        }
        Assert.Equal(before, await File.ReadAllBytesAsync(SidecarPath, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task WriteFailureDoesNotDamageExistingSidecarOrLeaveTemporaryFile()
    {
        await _store.SaveAsync(MediaPath, new XnfoMetadata { Title = "original" }, CancellationToken.None);
        var before = await File.ReadAllBytesAsync(SidecarPath, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveAsync(MediaPath, new XnfoMetadata { Title = "bad\u0001text" }, CancellationToken.None));
        Assert.Equal(before, await File.ReadAllBytesAsync(SidecarPath, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task RemovingListItemsPreservesExtensionsOrFailsWithoutDataLoss()
    {
        const string xml = "<xnfo type=\"file\" version=\"1\"><tags><tag>one</tag><tag custom=\"preserve\">two</tag></tags></xnfo>";
        await File.WriteAllTextAsync(SidecarPath, xml, TestContext.Current.CancellationToken);
        var metadata = Assert.IsType<XnfoMetadata>(await _store.LoadAsync(MediaPath, CancellationToken.None));
        metadata.Tags.RemoveAt(1);
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.SaveAsync(MediaPath, metadata, CancellationToken.None));
        Assert.Equal(xml, await File.ReadAllTextAsync(SidecarPath, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task OpenReaderAllowsAtomicReplacementButNotInPlaceWritesOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await _store.SaveAsync(MediaPath, new XnfoMetadata { Title = "before" }, CancellationToken.None);
        await using var reader = XnfoStore.OpenRead(SidecarPath);
        Assert.Throws<IOException>(() => File.Open(SidecarPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete));
        await _store.SaveAsync(MediaPath, new XnfoMetadata { Title = "after" }, CancellationToken.None);
        var original = await XDocument.LoadAsync(reader, LoadOptions.None, TestContext.Current.CancellationToken);
        Assert.Equal("before", (string?)original.Root!.Element("title"));
        Assert.Equal("after", Assert.IsType<XnfoMetadata>(await _store.LoadAsync(MediaPath, CancellationToken.None)).Title);
    }

    [Fact]
    public async Task CancellationDoesNotCreateSidecar()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.LoadAsync(MediaPath, source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.SaveAsync(MediaPath, new XnfoMetadata(), source.Token));
        Assert.Empty(Directory.GetFiles(_directory));
    }
}
