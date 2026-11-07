using Garou.Domain.Abstractions;
using Garou.Domain.Models;
using Microsoft.Data.Sqlite;

namespace Garou.Infrastructure.Persistence.Repositories;

public sealed class LibraryRepository(SqliteConnectionFactory connectionFactory) : ILibraryRepository
{
    public async Task<IReadOnlyList<LibraryDefinition>> GetAllAsync(CancellationToken cancellationToken) =>
        await connectionFactory.QueryAsync(
            "SELECT Id, RootPath, IsEnabled FROM Library ORDER BY Id;",
            reader => new LibraryDefinition
            {
                Id = Guid.Parse(reader.GetString(0)),
                RootPath = reader.GetString(1),
                IsEnabled = reader.GetInt32(2) != 0
            },
            cancellationToken);

    public async Task AddAsync(LibraryDefinition library, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(library);

        await connectionFactory.ExecuteNonQueryAsync(
            "INSERT INTO Library (Id, RootPath, IsEnabled) VALUES ($id, $rootPath, $isEnabled);",
            cancellationToken,
            ("$id", library.Id.ToString("N")),
            ("$rootPath", library.RootPath),
            ("$isEnabled", library.IsEnabled ? 1 : 0));
    }

    public async Task RemoveAsync(Guid id, CancellationToken cancellationToken) =>
        await connectionFactory.ExecuteNonQueryAsync("DELETE FROM Library WHERE Id = $id;", cancellationToken, ("$id", id.ToString("N")));

    public async Task SetEnabledAsync(Guid id, bool isEnabled, CancellationToken cancellationToken) =>
        await connectionFactory.ExecuteNonQueryAsync(
            "UPDATE Library SET IsEnabled = $isEnabled WHERE Id = $id;",
            cancellationToken,
            ("$id", id.ToString("N")),
            ("$isEnabled", isEnabled ? 1 : 0));
}
