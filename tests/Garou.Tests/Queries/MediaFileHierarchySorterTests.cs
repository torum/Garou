using Garou.Application.Queries;
using Garou.Domain.Entities;
using Garou.Domain.Enums;

namespace Garou.Tests.Queries;

public sealed class MediaFileHierarchySorterTests
{
    [Fact]
    public void SortKeepsFilesInEachFolderTogetherInHierarchyOrder()
    {
        var paths = new[]
        {
            new[] { "folder10", "a.jpg" },
            new[] { "folder2", "nested10", "b.jpg" },
            new[] { "folder2", "z10.jpg" },
            new[] { "folder2-extra", "a.jpg" },
            new[] { "folder2", "nested2", "a.jpg" },
            new[] { "folder2", "z2.jpg" },
            new[] { "root10.jpg" },
            new[] { "root2.jpg" },
            new[] { "folder2", "nested2", "z.jpg" }
        };
        var root = Path.Combine(Path.GetTempPath(), "library");
        var files = paths.Select(parts => CreateFile(root, parts)).ToArray();

        var result = MediaFileHierarchySorter.Sort(files, sortByFolder: true, TestContext.Current.CancellationToken).Select(file => file.Path).ToArray();

        Assert.Equal(new[] { files[7], files[6], files[5], files[2], files[4], files[8], files[1], files[3], files[0] }
            .Select(file => file.Path), result);
    }

    [Fact]
    public void SortKeepsDistinctCaseEquivalentFoldersTogether()
    {
        var root = Path.Combine(Path.GetTempPath(), "library");
        var upperA = CreateFile(root, ["Album", "a.jpg"]);
        var upperZ = CreateFile(root, ["Album", "z.jpg"]);
        var lowerA = CreateFile(root, ["album", "a.jpg"]);
        var lowerZ = CreateFile(root, ["album", "z.jpg"]);

        var result = MediaFileHierarchySorter.Sort([lowerZ, upperZ, lowerA, upperA], sortByFolder: true, TestContext.Current.CancellationToken).ToArray();

        Assert.Equal([upperA, upperZ, lowerA, lowerZ], result);
    }

    [Fact]
    public void SortWithinOneFolderUsesNaturalFileNameOrdering()
    {
        var root = Path.Combine(Path.GetTempPath(), "library");
        var files = new[] { "photo10.jpg", "photo2.jpg", "photo1.jpg" }
            .Select(name => CreateFile(root, [name]));

        Assert.Equal(["photo1.jpg", "photo2.jpg", "photo10.jpg"],
            MediaFileHierarchySorter.Sort(files, sortByFolder: true, TestContext.Current.CancellationToken).Select(file => file.FileName));
    }

    [Fact]
    public void SortGloballyIgnoresFoldersAndPreservesInputOrderForEqualNames()
    {
        var root = Path.Combine(Path.GetTempPath(), "library");
        var first = CreateFile(root, ["folder10", "photo2.jpg"]);
        var second = CreateFile(root, ["folder2", "photo2.jpg"]);
        var third = CreateFile(root, ["folder10", "photo1.jpg"]);
        var fourth = CreateFile(root, ["folder2", "photo10.jpg"]);

        var result = MediaFileHierarchySorter.Sort([first, second, fourth, third], sortByFolder: false, TestContext.Current.CancellationToken);

        Assert.Equal([third, first, second, fourth], result);
    }

    private static MediaFile CreateFile(string root, string[] parts) => new()
    {
        Path = Path.Combine([root, .. parts]),
        FileName = parts[^1],
        Extension = Path.GetExtension(parts[^1]),
        MediaType = MediaType.Image
    };
}
