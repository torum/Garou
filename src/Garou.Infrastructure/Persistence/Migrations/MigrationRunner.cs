using Microsoft.Data.Sqlite;

namespace Garou.Infrastructure.Persistence.Migrations;

public sealed class MigrationRunner(SqliteConnectionFactory connectionFactory)
{
    private static readonly (int Version, string Sql)[] Migrations =
    [
        (1, Migration001.Sql),
        (2, Migration002.Sql)
    ];

    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        await using (var create = connection.CreateCommand())
        {
            create.CommandText =
                """
                CREATE TABLE IF NOT EXISTS SchemaVersion
                (
                    Version INTEGER NOT NULL PRIMARY KEY
                );
                """;
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        var current = await GetCurrentVersionAsync(connection, cancellationToken);

        foreach (var (version, sql) in Migrations.Where(m => m.Version > current).OrderBy(m => m.Version))
        {
            await using var transaction = connection.BeginTransaction();

            await using (var migrate = connection.CreateCommand())
            {
                migrate.Transaction = transaction;
                migrate.CommandText = sql;
                await migrate.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var record = connection.CreateCommand())
            {
                record.Transaction = transaction;
                record.CommandText = "INSERT INTO SchemaVersion (Version) VALUES ($version);";
                record.AddValue("$version", version);
                await record.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
    }

    private static async Task<int> GetCurrentVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(Version), 0) FROM SchemaVersion;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }
}
