using Garou.Application.Queries;
using Garou.Domain.Entities;
using Garou.Domain.Enums;
using Garou.Domain.Models;
using Garou.Infrastructure.Metadata;
using Garou.Infrastructure.Persistence;
using Garou.Infrastructure.Persistence.Repositories;
using Microsoft.Data.Sqlite;

namespace Garou.Tests.Queries;

public sealed class MediaSearchIntegrationTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"MediaSearch-{Guid.NewGuid():N}.db");
    private readonly SqliteConnectionFactory _connectionFactory;

    public MediaSearchIntegrationTests()
    {
        _connectionFactory = new SqliteConnectionFactory($"Data Source={_databasePath}");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    [Fact]
    public async Task WhenSearchTextMatchesThenOnlyMatchingFilesAreReturned()
    {
        var repository = await SeedAsync(
            CreateFile("holiday.jpg", MediaType.Image),
            CreateFile("work.jpg", MediaType.Image));

        var results = await repository.SearchAsync(
            MediaQueryBuilder.Build(CreateQuery() with { SearchText = "holiday" }),
            CancellationToken.None);

        Assert.Equal(["holiday.jpg"], results.Select(file => file.FileName));
    }

    [Fact]
    public async Task EachSearchFieldMatchesOnlyItsOwnValueAcrossAllQueryShapes()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), $"Garou-field-search-{Guid.NewGuid():N}.mp4");
        var store = new XnfoStore();
        try
        {
            var file = new MediaFile { Path = path, FileName = "needle01.mp4", Extension = ".mp4", MediaType = MediaType.Video };
            var repository = await SeedAsync(file, CreateFile("unrelated.mp4", MediaType.Video));
            await store.SaveAsync(path, new XnfoMetadata
            {
                Title = "needle02", Subtitle = "needle03", SortTitle = "needle04", Publisher = "needle05",
                StudioLabel = "needle06", ReleaseDate = "needle07", Performers = ["needle08"], Countries = ["needle09"],
                Tags = ["needle10"], Genres = ["needle11"], Categories = ["needle12"], ContentRating = "needle13",
                Description = "needle14", Comments = "needle15", CoverArt = "needle16"
            }, ct);
            await new XnfoIndexService(store, _connectionFactory).SyncAsync(path, ct);
            var fields = Enum.GetValues<MediaSearchField>().Where(field => field != MediaSearchField.All).ToArray();
            Assert.Equal(16, fields.Length);
            for (var index = 0; index < fields.Length; index++)
            {
                var term = $"needle{index + 1:D2}";
                var query = CreateQuery() with { SearchText = term, SearchField = fields[index] };
                Assert.Equal(path, Assert.Single(await repository.SearchAsync(MediaQueryBuilder.BuildAll(query), ct)).Path);
                Assert.Equal(path, Assert.Single(await repository.SearchAsync(MediaQueryBuilder.Build(query), ct)).Path);
                Assert.Equal(1, await repository.CountAsync(MediaQueryBuilder.BuildCount(query), ct));
                Assert.Equal(path, Assert.Single(await repository.SearchAsync(MediaQueryBuilder.BuildAll(query with { SearchField = MediaSearchField.All }), ct)).Path);
                foreach (var other in fields.Where(field => field != fields[index]))
                {
                    Assert.Empty(await repository.SearchAsync(MediaQueryBuilder.BuildAll(query with { SearchField = other }), ct));
                }
            }
            foreach (var field in fields)
            {
                Assert.Equal(2, (await repository.SearchAsync(MediaQueryBuilder.BuildAll(CreateQuery() with { SearchField = field }), ct)).Count);
            }
        }
        finally
        {
            if (File.Exists(store.GetPath(path))) File.Delete(store.GetPath(path));
        }
    }

    [Fact]
    public async Task WhenMediaTypeFilterIsVideoThenImagesAreExcluded()
    {
        var repository = await SeedAsync(
            CreateFile("photo.jpg", MediaType.Image),
            CreateFile("clip.mp4", MediaType.Video));

        var results = await repository.SearchAsync(
            MediaQueryBuilder.Build(CreateQuery() with { MediaType = MediaTypeFilter.Video }),
            CancellationToken.None);

        Assert.Equal(["clip.mp4"], results.Select(file => file.FileName));
    }

    [Fact]
    public async Task WhenMultipleMediaTypesAreSelectedThenOnlySelectedTypesAreReturned()
    {
        var repository = await SeedAsync(
            CreateFile("photo.jpg", MediaType.Image),
            CreateFile("clip.mp4", MediaType.Video),
            CreateFile("track.mp3", MediaType.Audio));

        var results = await repository.SearchAsync(
            MediaQueryBuilder.Build(CreateQuery() with { MediaType = MediaTypeFilter.Image | MediaTypeFilter.Audio }),
            CancellationToken.None);

        Assert.Equal(["photo.jpg", "track.mp3"], results.Select(file => file.FileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task WhenNoMediaTypesAreSelectedThenNoFilesAreReturned()
    {
        var repository = await SeedAsync(CreateFile("photo.jpg", MediaType.Image));

        var results = await repository.SearchAsync(
            MediaQueryBuilder.Build(CreateQuery() with { MediaType = MediaTypeFilter.None }),
            CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task WhenInjectionTextIsSearchedThenNoRowsAreDeleted()
    {
        var repository = await SeedAsync(CreateFile("photo.jpg", MediaType.Image));

        var results = await repository.SearchAsync(
            MediaQueryBuilder.Build(CreateQuery() with { SearchText = "'; DELETE FROM MediaFile; --" }),
            CancellationToken.None);

        Assert.Empty(results);
        Assert.Single(await repository.SearchAsync(
            MediaQueryBuilder.Build(CreateQuery()),
            CancellationToken.None));
    }

    private async Task<MediaFileRepository> SeedAsync(params MediaFile[] files)
    {
        await new DatabaseInitializer(_connectionFactory).InitializeAsync(CancellationToken.None);
        var repository = new MediaFileRepository(_connectionFactory);
        await repository.UpsertBatchAsync(files, CancellationToken.None);
        return repository;
    }

    private static MediaFile CreateFile(string fileName, MediaType mediaType) => new()
    {
        Path = Path.Combine("C:\\library", fileName),
        FileName = fileName,
        Extension = Path.GetExtension(fileName),
        MediaType = mediaType,
        CreatedDateUtc = DateTime.UtcNow,
        ModifiedDateUtc = DateTime.UtcNow,
        LastIndexedUtc = DateTime.UtcNow
    };

    private static MediaQuery CreateQuery() =>
        new(FolderId: null, SearchText: null, MediaType: MediaTypeFilter.Any,
            SortField: MediaSortField.Id, Descending: false, AfterId: Guid.Empty, PageSize: 50);
}
