using Microsoft.Data.Sqlite;

namespace Garou.Infrastructure.Persistence;

public sealed class DatabaseMaintenanceService(SqliteConnectionFactory connectionFactory)
{
    public async Task OptimizeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "PRAGMA optimize;", cancellationToken);
        await ExecuteAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken);
    }

    public async Task VacuumAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "VACUUM;", cancellationToken);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
