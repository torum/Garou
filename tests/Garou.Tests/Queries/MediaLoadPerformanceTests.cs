using Garou.Application.Queries;
using Garou.Domain.Entities;
using Garou.Domain.Enums;
using Garou.ViewModels;

namespace Garou.Tests.Queries;

public sealed class MediaLoadPerformanceTests
{
    [Theory]
    [InlineData(MediaListSortOrder.None)]
    [InlineData(MediaListSortOrder.FileName)]
    [InlineData(MediaListSortOrder.FileSize)]
    [InlineData(MediaListSortOrder.FileType)]
    [InlineData(MediaListSortOrder.DateModified)]
    [InlineData(MediaListSortOrder.FileExtension)]
    public void SortingStopsWhenSourceIsCancelled(MediaListSortOrder order)
    {
        using var cancellation = new CancellationTokenSource();
        IEnumerable<MediaFile> Files()
        {
            for (var index = 0; index < 1000; index++)
            {
                if (index == 10)
                    cancellation.Cancel();
                yield return CreateFile(Guid.NewGuid(), $"{index}.jpg");
            }
        }

        var exception = Assert.Throws<OperationCanceledException>(() =>
            MainViewModel.SortMediaFiles(Files(), order, true, false, cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Theory]
    [InlineData(MediaListSortOrder.None, false, "ABC")]
    [InlineData(MediaListSortOrder.None, true, "ABC")]
    [InlineData(MediaListSortOrder.FileName, false, "CBA")]
    [InlineData(MediaListSortOrder.FileName, true, "ABC")]
    [InlineData(MediaListSortOrder.FileSize, false, "BAC")]
    [InlineData(MediaListSortOrder.FileSize, true, "ABC")]
    [InlineData(MediaListSortOrder.FileType, false, "ABC")]
    [InlineData(MediaListSortOrder.FileType, true, "BAC")]
    [InlineData(MediaListSortOrder.DateModified, false, "BAC")]
    [InlineData(MediaListSortOrder.DateModified, true, "ABC")]
    [InlineData(MediaListSortOrder.FileExtension, false, "ABC")]
    [InlineData(MediaListSortOrder.FileExtension, true, "CBA")]
    public void SortModesPreserveExpectedOrder(MediaListSortOrder order, bool descending, string expected)
    {
        var files = new[]
        {
            new MediaFile { Path = "A", FileName = "photo10", Extension = ".jpg", MediaType = MediaType.Image, FileSize = 30, ModifiedDateUtc = new DateTime(2024, 1, 1) },
            new MediaFile { Path = "B", FileName = "photo2", Extension = ".mp4", MediaType = MediaType.Video, FileSize = 10, ModifiedDateUtc = new DateTime(2022, 1, 1) },
            new MediaFile { Path = "C", FileName = "photo1", Extension = ".png", MediaType = MediaType.Unknown }
        };

        var result = MainViewModel.SortMediaFiles(files, order, false, descending, TestContext.Current.CancellationToken);

        Assert.Equal(expected, string.Concat(result.Select(file => file.Path)));
    }

    [Fact]
    public void SortingUnwrapsCancellationRaisedDuringComparisons()
    {
        using var cancellation = new CancellationTokenSource();
        var comparisons = 0;
        var inner = Comparer<int>.Create((left, right) =>
        {
            if (++comparisons == 100)
                cancellation.Cancel();
            return left.CompareTo(right);
        });
        var ordered = Enumerable.Range(0, 10000).Reverse()
            .OrderBy(value => value, CancellableSort.Comparer(inner, cancellation.Token));

        var exception = Assert.Throws<OperationCanceledException>(() => CancellableSort.ToArray(ordered, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.InRange(comparisons, 100, 256);
    }

    [Fact]
    public void ItemLookupTracksChangesAndBulkReplacement()
    {
        var first = new MediaItemViewModel(CreateFile(Guid.NewGuid(), "a.jpg"));
        var second = new MediaItemViewModel(CreateFile(Guid.NewGuid(), "b.jpg"));
        var replacement = new MediaItemViewModel(CreateFile(first.File.Id, "new.jpg"));
        var items = new MediaItemCollection { first, second };

        Assert.Same(first, items.FindById(first.File.Id));
        items.Move(0, 1);
        Assert.Same(first, items.FindById(first.File.Id));
        items.Remove(first);
        Assert.Null(items.FindById(first.File.Id));
        items.Insert(0, first);
        Assert.Same(first, items.FindById(first.File.Id));
        items[0] = replacement;
        Assert.Same(replacement, items.FindById(first.File.Id));
        items.ReplaceAll([second]);
        Assert.Null(items.FindById(first.File.Id));
        Assert.Same(second, items.FindById(second.File.Id));
        items.Clear();
        Assert.Null(items.FindById(second.File.Id));
    }

    [Fact]
    public void ItemLookupPreservesFirstMatchForDuplicateIds()
    {
        var id = Guid.NewGuid();
        var first = new MediaItemViewModel(CreateFile(id, "a.jpg"));
        var second = new MediaItemViewModel(CreateFile(id, "b.jpg"));
        var items = new MediaItemCollection { first, second };

        Assert.Same(first, items.FindById(id));
        items.Remove(first);
        Assert.Same(second, items.FindById(id));
    }

    private static MediaFile CreateFile(Guid id, string name) => new()
    {
        Id = id,
        Path = Path.Combine(Path.GetTempPath(), name),
        FileName = name,
        Extension = Path.GetExtension(name),
        MediaType = MediaType.Image
    };
}
