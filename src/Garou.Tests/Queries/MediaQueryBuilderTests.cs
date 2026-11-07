using Garou.Application.Queries;

namespace Garou.Tests.Queries;

public sealed class MediaQueryBuilderTests
{
    [Fact]
    public void WhenNoFiltersThenOnlyKeysetAndLimitAreApplied()
    {
        var built = MediaQueryBuilder.Build(CreateQuery());

        Assert.Contains("WHERE Id > $afterId", built.Sql);
        Assert.DoesNotContain("FolderId = $folderId", built.Sql);
        Assert.DoesNotContain("LIKE", built.Sql);
    }

    [Fact]
    public void WhenFolderIdIsSetThenFolderFilterIsParameterized()
    {
        var built = MediaQueryBuilder.Build(CreateQuery() with { FolderId = 7 });

        Assert.Contains("WITH RECURSIVE", built.Sql);
        Assert.Contains("$folderId", built.Sql);
        Assert.Equal(7L, built.Parameters["$folderId"]);
    }

    [Fact]
    public void WhenDescendantsAreExcludedThenOnlySelectedFolderIsQueried()
    {
        var built = MediaQueryBuilder.Build(CreateQuery() with { FolderId = 7, IncludeDescendants = false });

        Assert.Contains("FolderId = $folderId", built.Sql);
        Assert.DoesNotContain("WITH RECURSIVE", built.Sql);
        Assert.Equal(7L, built.Parameters["$folderId"]);
    }

    [Fact]
    public void WhenSearchTextContainsWildcardsThenTheyAreEscaped()
    {
        var built = MediaQueryBuilder.Build(CreateQuery() with { SearchText = "50%_off" });

        Assert.Equal("%50\\%\\_off%", built.Parameters["$search"]);
    }

    [Fact]
    public void WhenSearchTextIsInjectionAttemptThenItIsOnlyAParameter()
    {
        var built = MediaQueryBuilder.Build(CreateQuery() with { SearchText = "'; DROP TABLE MediaFile; --" });

        Assert.DoesNotContain("DROP", built.Sql);
        Assert.Contains("DROP TABLE", (string)built.Parameters["$search"]!);
    }

    [Fact]
    public void WhenVideoFilterIsSetThenMediaTypeIsParameterized()
    {
        var built = MediaQueryBuilder.Build(CreateQuery() with { MediaType = MediaTypeFilter.Video });

        Assert.Contains("MediaType IN ($mediaType0)", built.Sql);
        Assert.Equal(2, built.Parameters["$mediaType0"]);
    }

    [Fact]
    public void WhenMultipleMediaTypesAreSelectedThenEachTypeIsParameterized()
    {
        var built = MediaQueryBuilder.Build(CreateQuery() with { MediaType = MediaTypeFilter.Image | MediaTypeFilter.Audio });

        Assert.Contains("MediaType IN ($mediaType0, $mediaType1)", built.Sql);
        Assert.Equal(1, built.Parameters["$mediaType0"]);
        Assert.Equal(3, built.Parameters["$mediaType1"]);
    }

    [Fact]
    public void WhenNoMediaTypesAreSelectedThenNoItemsAreReturned()
    {
        var built = MediaQueryBuilder.Build(CreateQuery() with { MediaType = MediaTypeFilter.None });

        Assert.Contains("AND 1 = 0", built.Sql);
    }

    [Fact]
    public void WhenSortIsDescendingThenOrderUsesDescending()
    {
        var built = MediaQueryBuilder.Build(CreateQuery() with { SortField = MediaSortField.FileSize, Descending = true });

        Assert.Contains("ORDER BY FileSize DESC, Id ASC", built.Sql);
    }

    [Fact]
    public void WhenQueryIsPagedByIdThenResultsUseIdOrder()
    {
        var built = MediaQueryBuilder.Build(CreateQuery() with { SortField = MediaSortField.Id });

        Assert.Contains("ORDER BY Id ASC", built.Sql);
    }

    [Fact]
    public void WhenFileNamesContainNumbersThenLogicalComparerSortsNumerically()
    {
        var sorted = new[] { "photo10.jpg", "photo2.jpg", "photo1.jpg" }
            .OrderBy(name => name, LogicalFileNameComparer.Instance);

        Assert.Equal(["photo1.jpg", "photo2.jpg", "photo10.jpg"], sorted);
    }

    [Fact]
    public void WhenComparingFileNamesThenLogicalComparerIgnoresCase()
    {
        var result = LogicalFileNameComparer.Instance.Compare("Photo2.jpg", "photo10.jpg");

        Assert.True(result < 0);
    }

    [Fact]
    public void WhenPageSizeExceedsLimitThenItIsClamped()
    {
        var built = MediaQueryBuilder.Build(CreateQuery() with { PageSize = 100_000 });

        Assert.Equal(500, built.Parameters["$pageSize"]);
    }

    private static MediaQuery CreateQuery() =>
        new(FolderId: null, SearchText: null, MediaType: MediaTypeFilter.Any,
            SortField: MediaSortField.Id, Descending: false, AfterId: 0, PageSize: 50);
}
