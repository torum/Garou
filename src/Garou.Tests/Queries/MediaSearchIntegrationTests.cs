using Garou.Application.Queries;
using Garou.Domain.Entities;
using Garou.Domain.Enums;
using Garou.Infrastructure.Persistence;
using Garou.Infrastructure.Persistence.Migrations;
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

        Assert.Equal(["photo.jpg", "track.mp3"], results.Select(file => file.FileName));
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
        await new MigrationRunner(_connectionFactory).ApplyAsync(CancellationToken.None);
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
            SortField: MediaSortField.Id, Descending: false, AfterId: 0, PageSize: 50);
}
