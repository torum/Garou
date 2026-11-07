using Microsoft.Data.Sqlite;

namespace Garou.Infrastructure.Persistence;

internal static class SqliteValueExtensions
{
    public static SqliteParameter AddValue(this SqliteCommand command, string name, object? value)
    {
        return command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    public static string? ToDbDate(this DateTime? value)
    {
        return value?.ToUniversalTime().ToString("O");
    }

    public static string ToDbDate(this DateTime value)
    {
        return value.ToUniversalTime().ToString("O");
    }

    public static DateTime? ReadDate(this SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal)
            ? null
            : DateTime.Parse(reader.GetString(ordinal), null, System.Globalization.DateTimeStyles.RoundtripKind);
    }

    public static T? ReadNullable<T>(this SqliteDataReader reader, int ordinal) where T : struct
    {
        return reader.IsDBNull(ordinal) ? null : (T)Convert.ChangeType(reader.GetValue(ordinal), typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string? ReadNullableString(this SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }
}
