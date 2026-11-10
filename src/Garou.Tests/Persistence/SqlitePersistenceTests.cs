using Garou.Application.Queries;
using Garou.Domain.Entities;
using Garou.Domain.Enums;
using Garou.Infrastructure.Persistence;
using Garou.Infrastructure.Persistence.Repositories;
using Microsoft.Data.Sqlite;

namespace Garou.Tests.Persistence;

public sealed class SqlitePersistenceTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"Garou-{Guid.NewGuid():N}.db");
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqlitePersistenceTests()
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
    public async Task WhenDatabaseIsInitializedThenCurrentTablesAreCreated()
    {
        await new DatabaseInitializer(_connectionFactory).InitializeAsync(CancellationToken.None);

        await using var connection = await _connectionFactory.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT GROUP_CONCAT(name, ',') FROM (SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name);";

        Assert.Equal(
            "Folder,Library,LibraryExcludedFolder,MediaFile,MediaFileTag,Tag,ThumbnailCacheEntry",
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WhenDatabaseIsInitializedThenOnlyApprovedMediaFileColumnsAreCreated()
    {
        await new DatabaseInitializer(_connectionFactory).InitializeAsync(CancellationToken.None);

        await using var connection = await _connectionFactory.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT GROUP_CONCAT(name, ',') FROM pragma_table_info('MediaFile');";

        Assert.Equal("Id,Path,FileName,Extension,MediaType,FileSize,HashValue,ThumbnailPath,CreatedDateUtc,ModifiedDateUtc,LastIndexedUtc,FolderId", await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WhenDatabaseIsInitializedTwiceThenExistingDataIsPreserved()
    {
        var initializer = new DatabaseInitializer(_connectionFactory);
        await initializer.InitializeAsync(CancellationToken.None);

        await using (var connection = await _connectionFactory.OpenAsync(CancellationToken.None))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO Library (Id, RootPath) VALUES ($id, 'C:\\Photos');";
            command.Parameters.AddWithValue("$id", Guid.CreateVersion7().ToString("N"));
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await initializer.InitializeAsync(CancellationToken.None);

        await using var verifyConnection = await _connectionFactory.OpenAsync(CancellationToken.None);
        await using var verifyCommand = verifyConnection.CreateCommand();
        verifyCommand.CommandText = "SELECT COUNT(*) FROM Library;";

        Assert.Equal(1L, await verifyCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WhenFolderAddedThenGetByPathReturnsIt()
    {
        await new DatabaseInitializer(_connectionFactory).InitializeAsync(CancellationToken.None);
        var repository = new FolderRepository(_connectionFactory);

        var id = await repository.AddAsync(new Folder { Path = @"C:\Photos" }, CancellationToken.None);
        var folder = await repository.GetByPathAsync(@"C:\Photos", CancellationToken.None);

        Assert.Equal(id, folder?.Id);
        Assert.Equal(7, id.Version);
    }

    [Fact]
    public async Task WhenFolderSubtreeIsDeletedThenIndexedDataIsRemovedAndSourceFilesRemain()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await new DatabaseInitializer(_connectionFactory).InitializeAsync(CancellationToken.None);
        var sourceDirectory = Path.Combine(Path.GetTempPath(), $"Garou-source-{Guid.NewGuid():N}");
        var childDirectory = Path.Combine(sourceDirectory, "child");
        var unrelatedDirectory = Path.Combine(Path.GetTempPath(), $"Garou-unrelated-{Guid.NewGuid():N}");
        Directory.CreateDirectory(childDirectory);
        Directory.CreateDirectory(unrelatedDirectory);

        var rootMediaPath = Path.Combine(sourceDirectory, "root.jpg");
        var childMediaPath = Path.Combine(childDirectory, "child.jpg");
        var unrelatedMediaPath = Path.Combine(unrelatedDirectory, "unrelated.jpg");
        await File.WriteAllBytesAsync(rootMediaPath, [1], cancellationToken);
        await File.WriteAllBytesAsync(childMediaPath, [1], cancellationToken);
        await File.WriteAllBytesAsync(unrelatedMediaPath, [1], cancellationToken);

        try
        {
            var folders = new FolderRepository(_connectionFactory);
            var rootId = await folders.AddAsync(new Folder { Path = sourceDirectory }, CancellationToken.None);
            var childId = await folders.AddAsync(new Folder { Path = childDirectory, ParentId = rootId }, CancellationToken.None);
            var unrelatedFolderId = await folders.AddAsync(new Folder { Path = unrelatedDirectory }, CancellationToken.None);
            var mediaFiles = new MediaFileRepository(_connectionFactory);
            await mediaFiles.UpsertBatchAsync(
                [
                    CreateMediaFile(rootMediaPath, 1, folderId: rootId),
                    CreateMediaFile(childMediaPath, 1, folderId: childId),
                    CreateMediaFile(unrelatedMediaPath, 1, folderId: unrelatedFolderId)
                ],
                CancellationToken.None);

            var indexedFiles = new[]
            {
                await mediaFiles.GetByPathAsync(rootMediaPath, CancellationToken.None),
                await mediaFiles.GetByPathAsync(childMediaPath, CancellationToken.None),
                await mediaFiles.GetByPathAsync(unrelatedMediaPath, CancellationToken.None)
            };
            await using (var connection = await _connectionFactory.OpenAsync(CancellationToken.None))
            await using (var command = connection.CreateCommand())
            {
                var tagId = Guid.CreateVersion7();
                command.CommandText = "INSERT INTO Tag (Id, Name) VALUES ($tagId, 'test');";
                command.Parameters.AddWithValue("$tagId", tagId.ToString("N"));
                await command.ExecuteNonQueryAsync(cancellationToken);
                command.CommandText = "INSERT INTO MediaFileTag (MediaFileId, TagId) VALUES ($mediaFileId, $tagId);";
                var mediaFileId = command.Parameters.AddWithValue("$mediaFileId", string.Empty);
                foreach (var indexedFile in indexedFiles)
                {
                    mediaFileId.Value = indexedFile!.Id.ToString("N");
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            await folders.DeleteSubtreeAsync(rootId, CancellationToken.None);

            Assert.Null(await mediaFiles.GetByPathAsync(rootMediaPath, CancellationToken.None));
            Assert.Null(await mediaFiles.GetByPathAsync(childMediaPath, CancellationToken.None));
            Assert.NotNull(await mediaFiles.GetByPathAsync(unrelatedMediaPath, CancellationToken.None));
            Assert.Equal([unrelatedFolderId], (await folders.GetAllAsync(CancellationToken.None)).Select(folder => folder.Id));
            Assert.True(File.Exists(rootMediaPath));
            Assert.True(File.Exists(childMediaPath));
            Assert.True(File.Exists(unrelatedMediaPath));

            await using var verifyConnection = await _connectionFactory.OpenAsync(CancellationToken.None);
            await using var verifyCommand = verifyConnection.CreateCommand();
            verifyCommand.CommandText = "SELECT COUNT(*) FROM MediaFileTag;";
            Assert.Equal(1L, await verifyCommand.ExecuteScalarAsync(cancellationToken));
        }
        finally
        {
            Directory.Delete(sourceDirectory, recursive: true);
            Directory.Delete(unrelatedDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task WhenMediaFileUpsertedTwiceThenSingleRowIsUpdated()
    {
        await new DatabaseInitializer(_connectionFactory).InitializeAsync(CancellationToken.None);
        var repository = new MediaFileRepository(_connectionFactory);

        await repository.UpsertBatchAsync([CreateMediaFile("a.jpg", 10)], CancellationToken.None);
        await repository.UpsertBatchAsync([CreateMediaFile("a.jpg", 20)], CancellationToken.None);

        var file = await repository.GetByPathAsync("a.jpg", CancellationToken.None);

        Assert.Equal(20L, file?.FileSize);
        Assert.Equal(7, file!.Id.Version);
    }

    [Fact]
    public async Task WhenMediaFileRoundTripsThenDatesArePreserved()
    {
        await new DatabaseInitializer(_connectionFactory).InitializeAsync(CancellationToken.None);
        var repository = new MediaFileRepository(_connectionFactory);
        var modified = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);

        var mediaFile = CreateMediaFile("b.jpg", 1, modified);

        await repository.UpsertBatchAsync([mediaFile], CancellationToken.None);
        var file = await repository.GetByPathAsync("b.jpg", CancellationToken.None);

        Assert.Equal(modified, file?.ModifiedDateUtc);
    }

    [Fact]
    public async Task WhenMediaFileIsStoredThenOnlyApprovedFieldsAreReturned()
    {
        await new DatabaseInitializer(_connectionFactory).InitializeAsync(CancellationToken.None);
        var repository = new MediaFileRepository(_connectionFactory);
        var mediaFile = new MediaFile
        {
            Path = "metadata.mp4",
            FileName = "metadata.mp4",
            Extension = ".mp4",
            MediaType = MediaType.Video,
            FileSize = 42,
            HashValue = "hash-value"
        };

        await repository.UpsertBatchAsync([mediaFile], CancellationToken.None);
        var query = MediaQueryBuilder.Build(new MediaQuery(null, null, MediaTypeFilter.Any, MediaSortField.Id, false, Guid.Empty, 10));
        var result = Assert.Single(await repository.SearchAsync(query, CancellationToken.None));

        Assert.Equal(mediaFile.Id, result.Id);
        Assert.Equal(mediaFile.FileSize, result.FileSize);
        Assert.Equal(mediaFile.HashValue, result.HashValue);
    }

    [Fact]
    public async Task WhenThumbnailPathIsUpdatedThenNewPathIsPersisted()
    {
        await new DatabaseInitializer(_connectionFactory).InitializeAsync(CancellationToken.None);
        var repository = new MediaFileRepository(_connectionFactory);
        await repository.UpsertBatchAsync([CreateMediaFile("thumbnail-update.jpg", 1)], CancellationToken.None);
        var media = await repository.GetByPathAsync("thumbnail-update.jpg", CancellationToken.None);

        await repository.UpdateThumbnailPathAsync(media!.Id, "thumbnail-280.jpg", CancellationToken.None);

        var updated = await repository.GetByIdAsync(media.Id, CancellationToken.None);
        Assert.Equal("thumbnail-280.jpg", updated?.ThumbnailPath);
    }

    [Fact]
    public async Task WhenThumbnailPathIsClearedThenNullIsPersisted()
    {
        await new DatabaseInitializer(_connectionFactory).InitializeAsync(CancellationToken.None);
        var repository = new MediaFileRepository(_connectionFactory);
        await repository.UpsertBatchAsync([CreateMediaFile("thumbnail-clear.jpg", 1)], CancellationToken.None);
        var media = await repository.GetByPathAsync("thumbnail-clear.jpg", CancellationToken.None);

        await repository.UpdateThumbnailPathAsync(media!.Id, null, CancellationToken.None);

        var updated = await repository.GetByIdAsync(media.Id, CancellationToken.None);
        Assert.Null(updated?.ThumbnailPath);
    }

    [Fact]
    public async Task WhenPagedThenKeysetPagesCoverAllFilesWithoutOverlap()
    {
        await new DatabaseInitializer(_connectionFactory).InitializeAsync(CancellationToken.None);
        var repository = new MediaFileRepository(_connectionFactory);
        await repository.UpsertBatchAsync(Enumerable.Range(1, 5).Select(i => CreateMediaFile($"f{i}.jpg", i)).ToList(), CancellationToken.None);

        var first = await repository.GetPageAsync(null, Guid.Empty, 2, CancellationToken.None);
        var second = await repository.GetPageAsync(null, first[^1].Id, 2, CancellationToken.None);
        var third = await repository.GetPageAsync(null, second[^1].Id, 2, CancellationToken.None);

        Assert.Equal(new[] { 2, 2, 1 }, new[] { first.Count, second.Count, third.Count });
    }

    [Fact]
    public async Task WhenBatchWriterWritesMoreThanBatchSizeThenAllFilesAreStored()
    {
        await new DatabaseInitializer(_connectionFactory).InitializeAsync(CancellationToken.None);
        var repository = new MediaFileRepository(_connectionFactory);
        var files = Enumerable.Range(1, 1201).Select(i => CreateMediaFile($"batch{i}.jpg", i)).ToList();

        await new BatchWriter(repository).UpsertMediaFilesAsync(files, CancellationToken.None);

        var page = await repository.GetPageAsync(null, Guid.Empty, 2000, CancellationToken.None);

        Assert.Equal(1201, page.Count);
    }

    [Fact]
    public async Task WhenThumbnailEntryUpsertedThenTotalSizeMatches()
    {
        await new DatabaseInitializer(_connectionFactory).InitializeAsync(CancellationToken.None);
        var repository = new ThumbnailCacheRepository(_connectionFactory);

        await repository.UpsertAsync(new ThumbnailCacheEntry
        {
            ThumbnailPath = "t1.jpg",
            SizeBytes = 300,
            LastAccessUtc = DateTime.UtcNow
        }, CancellationToken.None);

        Assert.Equal(300L, await repository.GetTotalSizeBytesAsync(CancellationToken.None));
    }

    private static MediaFile CreateMediaFile(string path, long size, DateTime? modifiedDateUtc = null, Guid? folderId = null) => new()
    {
        Path = path,
        FileName = path,
        Extension = ".jpg",
        MediaType = MediaType.Image,
        FileSize = size,
        ModifiedDateUtc = modifiedDateUtc,
        LastIndexedUtc = DateTime.UtcNow,
        FolderId = folderId
    };
}
