using Microsoft.Data.Sqlite;

namespace Garou.Infrastructure.Persistence;

internal static class SqliteDataAccessExtensions
{
    public static async Task<int> ExecuteNonQueryAsync(
        this SqliteConnectionFactory connectionFactory,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<object?> ExecuteScalarAsync(
        this SqliteConnectionFactory connectionFactory,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql, parameters);
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    public static async Task<List<T>> QueryAsync<T>(
        this SqliteConnectionFactory connectionFactory,
        string sql,
        Func<SqliteDataReader, T> map,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var results = new List<T>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(map(reader));
        }

        return results;
    }

    public static async Task<T?> QuerySingleAsync<T>(
        this SqliteConnectionFactory connectionFactory,
        string sql,
        Func<SqliteDataReader, T> map,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters) where T : class
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken) ? map(reader) : null;
    }

    private static SqliteCommand CreateCommand(SqliteConnection connection, string sql, (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.AddValue(name, value);
        }

        return command;
    }
}
