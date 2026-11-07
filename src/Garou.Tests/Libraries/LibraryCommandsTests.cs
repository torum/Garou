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
    private readonly LibraryCommands _commands;

    public LibraryCommandsTests()
    {
        _connectionFactory = new SqliteConnectionFactory($"Data Source={_databasePath}");
        _repository = new LibraryRepository(_connectionFactory);
        _commands = new LibraryCommands(_repository, new NoOpLibraryScanner());
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

        await _commands.RemoveLibraryAsync(id, CancellationToken.None);

        Assert.Empty(await _repository.GetAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WhenLibraryIsRefreshedThenItIsScanned()
    {
        await new DatabaseInitializer(_connectionFactory).InitializeAsync(CancellationToken.None);
        var id = await _commands.AddLibraryAsync("C:\\\\Photos", CancellationToken.None);
        var scanner = new RecordingLibraryScanner();
        var commands = new LibraryCommands(_repository, scanner);

        await commands.RefreshLibraryAsync(id, null, CancellationToken.None);

        Assert.Equal(id, Assert.Single(scanner.Scanned).Id);
    }

    [Fact]
    public async Task WhenLibraryIsReindexedThenItIsScannedAndMarkedOnline()
    {
        await new DatabaseInitializer(_connectionFactory).InitializeAsync(CancellationToken.None);
        var id = await _commands.AddLibraryAsync("C:\\\\Photos", CancellationToken.None);
        var scanner = new RecordingLibraryScanner();
        var commands = new LibraryCommands(_repository, scanner);

        await commands.ReindexLibraryAsync(id, null, CancellationToken.None);

        var scanned = Assert.Single(scanner.Scanned);
        Assert.Equal(id, scanned.Id);
        Assert.True(scanned.IsOnline);
    }

    [Fact]
    public async Task WhenUnknownLibraryIsRefreshedThenItThrows()
    {
        await new DatabaseInitializer(_connectionFactory).InitializeAsync(CancellationToken.None);
        var commands = new LibraryCommands(_repository, new RecordingLibraryScanner());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            commands.RefreshLibraryAsync(Guid.CreateVersion7(), null, CancellationToken.None));
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
