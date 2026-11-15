using Garou.Application.Libraries;
using Garou.Domain.Abstractions;
using Garou.Domain.Entities;
using Garou.Domain.Models;
using Garou.Infrastructure.Persistence;
using Garou.Infrastructure.Persistence.Repositories;
using Garou.Infrastructure.Settings;
using Garou.Infrastructure.Sync;
using Microsoft.Data.Sqlite;

namespace Garou.Tests.Sync;

public sealed class MoveIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObservedRenamePreservesMediaIdentityAndTags(bool moveFolder)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"Garou-move-{Guid.NewGuid():N}");
        var libraryRoot = Path.Combine(root, "library");
        var oldFolder = Path.Combine(libraryRoot, "old");
        var oldNested = Path.Combine(oldFolder, "nested");
        Directory.CreateDirectory(oldNested);
        var oldFile = Path.Combine(oldNested, "photo.jpg");
        await File.WriteAllBytesAsync(oldFile, [1, 2, 3], cancellationToken);
        var factory = new SqliteConnectionFactory(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "test.db") }.ToString());

        try
        {
            await new DatabaseInitializer(factory).InitializeAsync(cancellationToken);
            var media = new MediaFileRepository(factory);
            var folders = new FolderRepository(factory);
            var libraries = new LibraryRepository(factory);
            var exclusions = new LibraryExcludedFolderRepository(factory);
            var indexer = new MediaIndexerService(new FileWatcherService(), libraries, media, folders, exclusions,
                new LibraryOperationGate(), new AppSettingsSession(), TimeProvider.System);
            var library = new LibraryDefinition { RootPath = libraryRoot };
            await indexer.ScanAsync(library, null, cancellationToken);
            var original = Assert.IsType<MediaFile>(await media.GetByPathAsync(oldFile, cancellationToken));
            var tags = new TagRepository(factory);
            var tagId = await tags.AddAsync(new Tag { Name = "favorite" }, cancellationToken);
            await tags.AssignAsync(new MediaFileTag { MediaFileId = original.Id, TagId = tagId }, cancellationToken);
            await media.UpdateThumbnailPathAsync(original.Id, "old-thumbnail.jpg", cancellationToken);

            var newFolder = Path.Combine(libraryRoot, "new");
            var newFile = moveFolder ? Path.Combine(newFolder, "nested", "photo.jpg") : Path.Combine(oldNested, "renamed.jpg");
            if (moveFolder)
                Directory.Move(oldFolder, newFolder);
            else
                File.Move(oldFile, newFile);

            await indexer.HandleAsync(library, new FileChangeEvent
            {
                LibraryRoot = libraryRoot,
                Kind = FileChangeKind.Renamed,
                OldFullPath = moveFolder ? oldFolder : oldFile,
                FullPath = moveFolder ? newFolder : newFile
            }, cancellationToken);

            var moved = Assert.IsType<MediaFile>(await media.GetByPathAsync(newFile, cancellationToken));
            Assert.Equal(original.Id, moved.Id);
            Assert.Null(moved.ThumbnailPath);
            Assert.Null(await media.GetByPathAsync(oldFile, cancellationToken));
            Assert.Equal(Path.GetDirectoryName(newFile), (await folders.GetByIdAsync(moved.FolderId!.Value, cancellationToken))!.Path);
            if (moveFolder)
                Assert.Null(await folders.GetByPathAsync(oldFolder, cancellationToken));
            await using var connection = await factory.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM MediaFileTag WHERE MediaFileId = $id AND TagId = $tag;";
            command.Parameters.AddWithValue("$id", original.Id.ToString("N"));
            command.Parameters.AddWithValue("$tag", tagId.ToString("N"));
            Assert.Equal(1L, await command.ExecuteScalarAsync(cancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ManualRescanReconcilesSelectedLibraryAndPreservesOfflineLibrary()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"Garou-manual-rescan-{Guid.NewGuid():N}");
        var firstRoot = Path.Combine(root, "first");
        var secondRoot = Path.Combine(root, "second");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        var factory = new SqliteConnectionFactory(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "test.db") }.ToString());
        try
        {
            await new DatabaseInitializer(factory).InitializeAsync(cancellationToken);
            var media = new MediaFileRepository(factory);
            var libraries = new LibraryRepository(factory);
            var exclusions = new LibraryExcludedFolderRepository(factory);
            var indexer = new MediaIndexerService(new FileWatcherService(), libraries, media,
                new FolderRepository(factory), exclusions, new LibraryOperationGate(), new AppSettingsSession(), TimeProvider.System);
            var commands = new LibraryCommands(libraries, exclusions, indexer);
            await commands.AddLibraryAsync(firstRoot, cancellationToken);
            await commands.AddLibraryAsync(secondRoot, cancellationToken);
            var firstFile = Path.Combine(firstRoot, "old.jpg");
            var secondFile = Path.Combine(secondRoot, "other.jpg");
            await File.WriteAllBytesAsync(firstFile, [1], cancellationToken);
            await File.WriteAllBytesAsync(secondFile, [2], cancellationToken);
            await commands.RescanLibraryByRootAsync(firstRoot, null, cancellationToken);
            await commands.RescanLibraryByRootAsync(secondRoot, null, cancellationToken);
            Assert.True(await commands.IsLibraryRootAsync(firstRoot + Path.DirectorySeparatorChar, cancellationToken));
            Assert.False(await commands.IsLibraryRootAsync(Path.Combine(firstRoot, "child"), cancellationToken));
            File.Delete(firstFile);
            var replacement = Path.Combine(firstRoot, "new.jpg");
            await File.WriteAllBytesAsync(replacement, [3], cancellationToken);

            await commands.RescanLibraryByRootAsync(firstRoot, null, cancellationToken);

            Assert.Null(await media.GetByPathAsync(firstFile, cancellationToken));
            Assert.NotNull(await media.GetByPathAsync(replacement, cancellationToken));
            Assert.NotNull(await media.GetByPathAsync(secondFile, cancellationToken));
            Directory.Move(secondRoot, Path.Combine(root, "offline"));
            await Assert.ThrowsAsync<DirectoryNotFoundException>(() => commands.RescanLibraryByRootAsync(secondRoot, null, cancellationToken));
            Assert.NotNull(await media.GetByPathAsync(secondFile, cancellationToken));
            Assert.True(await commands.IsLibraryRootAsync(secondRoot, cancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreatedDirectoryIndexesNestedFiles()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"Garou-created-folder-{Guid.NewGuid():N}");
        var libraryRoot = Path.Combine(root, "library");
        Directory.CreateDirectory(libraryRoot);
        var factory = new SqliteConnectionFactory(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "test.db") }.ToString());
        try
        {
            await new DatabaseInitializer(factory).InitializeAsync(cancellationToken);
            var media = new MediaFileRepository(factory);
            var indexer = new MediaIndexerService(new FileWatcherService(), new LibraryRepository(factory), media,
                new FolderRepository(factory), new LibraryExcludedFolderRepository(factory), new LibraryOperationGate(),
                new AppSettingsSession(), TimeProvider.System);
            var library = new LibraryDefinition { RootPath = libraryRoot };
            await indexer.ScanAsync(library, null, cancellationToken);
            var addedFolder = Path.Combine(libraryRoot, "added");
            var nestedFolder = Path.Combine(addedFolder, "nested");
            Directory.CreateDirectory(nestedFolder);
            var file = Path.Combine(nestedFolder, "photo.jpg");
            await File.WriteAllBytesAsync(file, [1], cancellationToken);

            await indexer.HandleAsync(library, new FileChangeEvent
            {
                LibraryRoot = libraryRoot, FullPath = addedFolder, Kind = FileChangeKind.Created
            }, cancellationToken);

            Assert.NotNull(await media.GetByPathAsync(file, cancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
