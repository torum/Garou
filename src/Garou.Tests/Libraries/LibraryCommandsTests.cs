using Garou.Application.Libraries;
using Garou.Domain.Abstractions;
using Garou.Domain.Models;
using Garou.Infrastructure.Persistence;
using Garou.Infrastructure.Persistence.Migrations;
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
        await new MigrationRunner(_connectionFactory).ApplyAsync(CancellationToken.None);

        await _commands.AddLibraryAsync("photos", "C:\\Photos", CancellationToken.None);

        var library = Assert.Single(await _repository.GetAllAsync(CancellationToken.None));
        Assert.Equal("photos", library.Id);
        Assert.Equal(Path.GetFullPath("C:\\Photos"), library.RootPath);
        Assert.True(library.IsEnabled);
    }

    [Fact]
    public async Task WhenLibraryIdIsDuplicatedThenAddIsRejected()
    {
        await new MigrationRunner(_connectionFactory).ApplyAsync(CancellationToken.None);
        await _commands.AddLibraryAsync("photos", "C:\\Photos", CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _commands.AddLibraryAsync("photos", "C:\\Other", CancellationToken.None));
    }

    [Fact]
    public async Task WhenLibraryIsDisabledThenIsEnabledIsFalse()
    {
        await new MigrationRunner(_connectionFactory).ApplyAsync(CancellationToken.None);
        await _commands.AddLibraryAsync("photos", "C:\\Photos", CancellationToken.None);

        await _commands.DisableLibraryAsync("photos", CancellationToken.None);

        var library = Assert.Single(await _repository.GetAllAsync(CancellationToken.None));
        Assert.False(library.IsEnabled);
    }

    [Fact]
    public async Task WhenLibraryIsRemovedThenNoLibrariesRemain()
    {
        await new MigrationRunner(_connectionFactory).ApplyAsync(CancellationToken.None);
        await _commands.AddLibraryAsync("photos", "C:\\Photos", CancellationToken.None);

        await _commands.RemoveLibraryAsync("photos", CancellationToken.None);

        Assert.Empty(await _repository.GetAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WhenLibraryIsRefreshedThenItIsScanned()
    {
        await new MigrationRunner(_connectionFactory).ApplyAsync(CancellationToken.None);
        await _commands.AddLibraryAsync("photos", "C:\\\\Photos", CancellationToken.None);
        var scanner = new RecordingLibraryScanner();
        var commands = new LibraryCommands(_repository, scanner);

        await commands.RefreshLibraryAsync("photos", null, CancellationToken.None);

        Assert.Equal("photos", Assert.Single(scanner.Scanned).Id);
    }

    [Fact]
    public async Task WhenLibraryIsReindexedThenItIsScannedAndMarkedOnline()
    {
        await new MigrationRunner(_connectionFactory).ApplyAsync(CancellationToken.None);
        await _commands.AddLibraryAsync("photos", "C:\\\\Photos", CancellationToken.None);
        var scanner = new RecordingLibraryScanner();
        var commands = new LibraryCommands(_repository, scanner);

        await commands.ReindexLibraryAsync("photos", null, CancellationToken.None);

        var scanned = Assert.Single(scanner.Scanned);
        Assert.Equal("photos", scanned.Id);
        Assert.True(scanned.IsOnline);
    }

    [Fact]
    public async Task WhenUnknownLibraryIsRefreshedThenItThrows()
    {
        await new MigrationRunner(_connectionFactory).ApplyAsync(CancellationToken.None);
        var commands = new LibraryCommands(_repository, new RecordingLibraryScanner());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            commands.RefreshLibraryAsync("missing", null, CancellationToken.None));
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
