using Microsoft.Data.Sqlite;

namespace Garou.Infrastructure.Persistence;

public sealed class SqliteConnectionFactory(string connectionString)
{
    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA foreign_keys = ON;
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        return connection;
    }
}
