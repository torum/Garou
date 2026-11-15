using Garou.Application.Libraries;
using Garou.Domain.Abstractions;
using Garou.Domain.Models;
using Garou.Infrastructure.Persistence;
using Garou.Infrastructure.Persistence.Repositories;
using Microsoft.Data.Sqlite;

namespace Garou.Tests.Libraries;

public sealed class LibraryCommandsTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"Libraries-{Guid.NewGuid():N}.db");
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly LibraryRepository _repository;
    private readonly LibraryExcludedFolderRepository _excludedFolders;
    private readonly LibraryCommands _commands;

    public LibraryCommandsTests()
    {
        _connectionFactory = new SqliteConnectionFactory($"Data Source={_databasePath}");
        _repository = new LibraryRepository(_connectionFactory);
        _excludedFolders = new LibraryExcludedFolderRepository(_connectionFactory);
        _commands = new LibraryCommands(_repository, _excludedFolders, new NoOpLibraryScanner());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    private sealed class NoOpLibraryScanner : ILibraryScanner
    {
        public Task ScanAsync(LibraryDefinition library, IProgress<ScanProgress>? progress, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task WhenLibraryIsAddedThenItIsStoredEnabledWithFullPath()
    {
        await new DatabaseInitializer(_connectionFactory).InitializeAsync(CancellationToken.None);

        var id = await _commands.AddLibraryAsync("C:\\Photos", CancellationToken.None);

        var library = Assert.Single(await _repository.GetAllAsync(CancellationToken.None));
        Assert.Equal(id, library.Id);
        Assert.Equal(7, id.Version);
        Assert.Equal(Path.GetFullPath("C:\\Photos"), library.RootPath);
        Assert.True(library.IsEnabled);
    }

    [Fact]
    public async Task WhenLibraryRootIsDuplicatedThenAddIsRejected()
    {
        await new DatabaseInitializer(_connectionFactory).InitializeAsync(CancellationToken.None);
        await _commands.AddLibraryAsync("C:\\Photos", CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _commands.AddLibraryAsync("C:\\Photos", CancellationToken.None));
    }

    [Fact]
    public async Task WhenLibraryIsDisabledThenIsEnabledIsFalse()
    {
        await new DatabaseInitializer(_connectionFactory).InitializeAsync(CancellationToken.None);
        var id = await _commands.AddLibraryAsync("C:\\Photos", CancellationToken.None);

        await _commands.DisableLibraryAsync(id, CancellationToken.None);

        var library = Assert.Single(await _repository.GetAllAsync(CancellationToken.None));
        Assert.False(library.IsEnabled);
    }

    [Fact]
    public async Task WhenLibraryIsRemovedThenNoLibrariesRemain()
    {
        await new DatabaseInitializer(_connectionFactory).InitializeAsync(CancellationToken.None);
        var id = await _commands.AddLibraryAsync("C:\\Photos", CancellationToken.None);
        await _excludedFolders.AddAsync(id, Path.Combine(Path.GetTempPath(), "Excluded"), CancellationToken.None);

        await _commands.RemoveLibraryAsync(id, CancellationToken.None);

        Assert.Empty(await _repository.GetAllAsync(CancellationToken.None));
        Assert.Empty(await _excludedFolders.GetPathsAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task WhenLibraryRootIsRemovedThenOnlyTheExactRootIsRemoved()
    {
        await new DatabaseInitializer(_connectionFactory).InitializeAsync(CancellationToken.None);
        var rootPath = Path.Combine(Path.GetTempPath(), $"Library-{Guid.NewGuid():N}");
        var nestedPath = Path.Combine(rootPath, "Nested");
        var rootId = await _commands.AddLibraryAsync(rootPath, CancellationToken.None);
        var nestedId = await _commands.AddLibraryAsync(nestedPath, CancellationToken.None);

        var removed = await _commands.RemoveLibraryByRootAsync(rootPath + Path.DirectorySeparatorChar, CancellationToken.None);

        Assert.True(removed);
        Assert.Equal(nestedId, Assert.Single(await _repository.GetAllAsync(CancellationToken.None)).Id);
        Assert.NotEqual(rootId, nestedId);
    }

    [Fact]
    public async Task WhenFolderIsExcludedThenContainingLibrariesStoreItAndNestedLibrariesAreRemoved()
    {
        await new DatabaseInitializer(_connectionFactory).InitializeAsync(CancellationToken.None);
        var rootPath = Path.Combine(Path.GetTempPath(), $"Library-{Guid.NewGuid():N}");
        var excludedPath = Path.Combine(rootPath, "Removed");
        var nestedLibraryPath = Path.Combine(excludedPath, "NestedLibrary");
        var unrelatedLibraryPath = Path.Combine(Path.GetTempPath(), $"Unrelated-{Guid.NewGuid():N}");
        var rootId = await _commands.AddLibraryAsync(rootPath, CancellationToken.None);
        var nestedLibraryId = await _commands.AddLibraryAsync(nestedLibraryPath, CancellationToken.None);
        var unrelatedLibraryId = await _commands.AddLibraryAsync(unrelatedLibraryPath, CancellationToken.None);

        await _commands.ExcludeFolderAsync(excludedPath, CancellationToken.None);

        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(excludedPath)),
            Assert.Single(await _excludedFolders.GetPathsAsync(rootId, CancellationToken.None)));
        var remainingLibraries = await _repository.GetAllAsync(CancellationToken.None);
        Assert.Contains(remainingLibraries, library => library.Id == rootId);
        Assert.Contains(remainingLibraries, library => library.Id == unrelatedLibraryId);
        Assert.DoesNotContain(remainingLibraries, library => library.Id == nestedLibraryId);
    }

    [Fact]
    public async Task WhenExcludedFolderIsRestoredThenEveryAffectedLibraryIsScannedAndOtherExclusionsRemain()
    {
        await new DatabaseInitializer(_connectionFactory).InitializeAsync(CancellationToken.None);
        var root = Path.Combine(Path.GetTempPath(), $"restore-{Guid.NewGuid():N}");
        var nestedRoot = Path.Combine(root, "nested");
        var excluded = Path.Combine(nestedRoot, "excluded");
        var child = Path.Combine(excluded, "child");
        Directory.CreateDirectory(child);
        try
        {
            var rootId = await _commands.AddLibraryAsync(root, CancellationToken.None);
            var nestedId = await _commands.AddLibraryAsync(nestedRoot, CancellationToken.None);
            await _commands.ExcludeFolderAsync(excluded, CancellationToken.None);
            await _commands.ExcludeFolderAsync(child, CancellationToken.None);
            var listed = await _commands.GetExcludedFoldersAsync(CancellationToken.None);
            Assert.Equal(2, listed.Count);
            Assert.Contains(excluded, listed);
            Assert.Contains(child, listed);
            var scanner = new RecordingLibraryScanner();
            var commands = new LibraryCommands(_repository, _excludedFolders, scanner);

            await commands.RestoreExcludedFolderAsync(excluded + Path.DirectorySeparatorChar, null, CancellationToken.None);

            Assert.Equal(new[] { rootId, nestedId }.OrderBy(id => id), scanner.Scanned.Select(library => library.Id).OrderBy(id => id));
            Assert.Equal(child, Assert.Single(await _excludedFolders.GetPathsAsync(rootId, CancellationToken.None)));
            Assert.Equal(child, Assert.Single(await _excludedFolders.GetPathsAsync(nestedId, CancellationToken.None)));
            Assert.Equal(child, Assert.Single(await commands.GetExcludedFoldersAsync(CancellationToken.None)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WhenRestoreScanFailsThenExclusionRemainsForRetry()
    {
        await new DatabaseInitializer(_connectionFactory).InitializeAsync(CancellationToken.None);
        var root = Path.Combine(Path.GetTempPath(), $"restore-failure-{Guid.NewGuid():N}");
        var excluded = Path.Combine(root, "excluded");
        Directory.CreateDirectory(excluded);
        try
        {
            var id = await _commands.AddLibraryAsync(root, CancellationToken.None);
            await _commands.ExcludeFolderAsync(excluded, CancellationToken.None);
            var commands = new LibraryCommands(_repository, _excludedFolders, new FailingLibraryScanner());

            await Assert.ThrowsAsync<IOException>(() => commands.RestoreExcludedFolderAsync(excluded, null, CancellationToken.None));

            Assert.Equal(excluded, Assert.Single(await _excludedFolders.GetPathsAsync(id, CancellationToken.None)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WhenLibraryIsRefreshedThenItIsScanned()
    {
        await new DatabaseInitializer(_connectionFactory).InitializeAsync(CancellationToken.None);
        var id = await _commands.AddLibraryAsync("C:\\\\Photos", CancellationToken.None);
        var scanner = new RecordingLibraryScanner();
        var commands = new LibraryCommands(_repository, _excludedFolders, scanner);

        await commands.RefreshLibraryAsync(id, null, CancellationToken.None);

        Assert.Equal(id, Assert.Single(scanner.Scanned).Id);
    }

    [Fact]
    public async Task WhenLibraryIsReindexedThenItIsScannedAndMarkedOnline()
    {
        await new DatabaseInitializer(_connectionFactory).InitializeAsync(CancellationToken.None);
        var id = await _commands.AddLibraryAsync("C:\\\\Photos", CancellationToken.None);
        var scanner = new RecordingLibraryScanner();
        var commands = new LibraryCommands(_repository, _excludedFolders, scanner);

        await commands.ReindexLibraryAsync(id, null, CancellationToken.None);

        var scanned = Assert.Single(scanner.Scanned);
        Assert.Equal(id, scanned.Id);
        Assert.True(scanned.IsOnline);
    }

    [Fact]
    public async Task WhenUnknownLibraryIsRefreshedThenItThrows()
    {
        await new DatabaseInitializer(_connectionFactory).InitializeAsync(CancellationToken.None);
        var commands = new LibraryCommands(_repository, _excludedFolders, new RecordingLibraryScanner());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            commands.RefreshLibraryAsync(Guid.CreateVersion7(), null, CancellationToken.None));
    }

    private sealed class FailingLibraryScanner : ILibraryScanner
    {
        public Task ScanAsync(LibraryDefinition library, IProgress<ScanProgress>? progress, CancellationToken cancellationToken) =>
            throw new IOException("Scan failed");
    }

    private sealed class RecordingLibraryScanner : ILibraryScanner
    {
        public List<LibraryDefinition> Scanned { get; } = [];

        public Task ScanAsync(LibraryDefinition library, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
        {
            Scanned.Add(library);
            return Task.CompletedTask;
        }
    }
}
