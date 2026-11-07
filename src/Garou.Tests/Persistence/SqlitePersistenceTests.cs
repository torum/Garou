using Garou.Domain.Entities;
using Garou.Domain.Enums;
using Garou.Infrastructure.Persistence;
using Garou.Infrastructure.Persistence.Migrations;
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
    public async Task WhenMigrationsApplyThenSchemaVersionIsTwo()
    {
        await new MigrationRunner(_connectionFactory).ApplyAsync(CancellationToken.None);

        await using var connection = await _connectionFactory.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(Version) FROM SchemaVersion;";

        Assert.Equal(2L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WhenMigrationsApplyTwiceThenSchemaVersionHasSingleRow()
    {
        var runner = new MigrationRunner(_connectionFactory);
        await runner.ApplyAsync(CancellationToken.None);
        await runner.ApplyAsync(CancellationToken.None);

        await using var connection = await _connectionFactory.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM SchemaVersion;";

        Assert.Equal(2L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WhenFolderAddedThenGetByPathReturnsIt()
    {
        await new MigrationRunner(_connectionFactory).ApplyAsync(CancellationToken.None);
        var repository = new FolderRepository(_connectionFactory);

        var id = await repository.AddAsync(new Folder { Id = 0, Path = @"C:\Photos" }, CancellationToken.None);
        var folder = await repository.GetByPathAsync(@"C:\Photos", CancellationToken.None);

        Assert.Equal(id, folder?.Id);
    }

    [Fact]
    public async Task WhenFolderSubtreeIsDeletedThenIndexedDataIsRemovedAndSourceFilesRemain()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await new MigrationRunner(_connectionFactory).ApplyAsync(CancellationToken.None);
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
            var rootId = await folders.AddAsync(new Folder { Id = 0, Path = sourceDirectory }, CancellationToken.None);
            var childId = await folders.AddAsync(new Folder { Id = 0, Path = childDirectory, ParentId = rootId }, CancellationToken.None);
            var unrelatedFolderId = await folders.AddAsync(new Folder { Id = 0, Path = unrelatedDirectory }, CancellationToken.None);
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
                command.CommandText = "INSERT INTO Tag (Name) VALUES ('test'); SELECT last_insert_rowid();";
                var tagId = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
                command.CommandText = "INSERT INTO MediaFileTag (MediaFileId, TagId) VALUES ($mediaFileId, $tagId);";
                var mediaFileId = command.Parameters.AddWithValue("$mediaFileId", 0L);
                command.Parameters.AddWithValue("$tagId", tagId);
                foreach (var indexedFile in indexedFiles)
                {
                    mediaFileId.Value = indexedFile!.Id;
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
        await new MigrationRunner(_connectionFactory).ApplyAsync(CancellationToken.None);
        var repository = new MediaFileRepository(_connectionFactory);

        await repository.UpsertBatchAsync([CreateMediaFile("a.jpg", 10)], CancellationToken.None);
        await repository.UpsertBatchAsync([CreateMediaFile("a.jpg", 20)], CancellationToken.None);

        var file = await repository.GetByPathAsync("a.jpg", CancellationToken.None);

        Assert.Equal(20L, file?.FileSize);
    }

    [Fact]
    public async Task WhenMediaFileRoundTripsThenDatesArePreserved()
    {
        await new MigrationRunner(_connectionFactory).ApplyAsync(CancellationToken.None);
        var repository = new MediaFileRepository(_connectionFactory);
        var modified = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);

        var mediaFile = CreateMediaFile("b.jpg", 1, modified);

        await repository.UpsertBatchAsync([mediaFile], CancellationToken.None);
        var file = await repository.GetByPathAsync("b.jpg", CancellationToken.None);

        Assert.Equal(modified, file?.ModifiedDateUtc);
    }

    [Fact]
    public async Task WhenThumbnailPathIsUpdatedThenNewPathIsPersisted()
    {
        await new MigrationRunner(_connectionFactory).ApplyAsync(CancellationToken.None);
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
        await new MigrationRunner(_connectionFactory).ApplyAsync(CancellationToken.None);
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
        await new MigrationRunner(_connectionFactory).ApplyAsync(CancellationToken.None);
        var repository = new MediaFileRepository(_connectionFactory);
        await repository.UpsertBatchAsync(Enumerable.Range(1, 5).Select(i => CreateMediaFile($"f{i}.jpg", i)).ToList(), CancellationToken.None);

        var first = await repository.GetPageAsync(null, 0, 2, CancellationToken.None);
        var second = await repository.GetPageAsync(null, first[^1].Id, 2, CancellationToken.None);
        var third = await repository.GetPageAsync(null, second[^1].Id, 2, CancellationToken.None);

        Assert.Equal(new[] { 2, 2, 1 }, new[] { first.Count, second.Count, third.Count });
    }

    [Fact]
    public async Task WhenBatchWriterWritesMoreThanBatchSizeThenAllFilesAreStored()
    {
        await new MigrationRunner(_connectionFactory).ApplyAsync(CancellationToken.None);
        var repository = new MediaFileRepository(_connectionFactory);
        var files = Enumerable.Range(1, 1201).Select(i => CreateMediaFile($"batch{i}.jpg", i)).ToList();

        await new BatchWriter(repository).UpsertMediaFilesAsync(files, CancellationToken.None);

        var page = await repository.GetPageAsync(null, 0, 2000, CancellationToken.None);

        Assert.Equal(1201, page.Count);
    }

    [Fact]
    public async Task WhenThumbnailEntryUpsertedThenTotalSizeMatches()
    {
        await new MigrationRunner(_connectionFactory).ApplyAsync(CancellationToken.None);
        var repository = new ThumbnailCacheRepository(_connectionFactory);

        await repository.UpsertAsync(new ThumbnailCacheEntry
        {
            ThumbnailPath = "t1.jpg",
            SizeBytes = 300,
            LastAccessUtc = DateTime.UtcNow
        }, CancellationToken.None);

        Assert.Equal(300L, await repository.GetTotalSizeBytesAsync(CancellationToken.None));
    }

    private static MediaFile CreateMediaFile(string path, long size, DateTime? modifiedDateUtc = null, long? folderId = null) => new()
    {
        Id = 0,
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
