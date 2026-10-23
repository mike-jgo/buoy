using Microsoft.Data.Sqlite;

namespace Buoy.Core.Storage;

internal static class SqliteExtensions
{
    public static int Execute(this SqliteConnection connection, string sql, params object?[] args)
    {
        using var command = connection.Prepare(sql, args);
        return command.ExecuteNonQuery();
    }

    public static T? Scalar<T>(this SqliteConnection connection, string sql, params object?[] args)
    {
        using var command = connection.Prepare(sql, args);
        var value = command.ExecuteScalar();
        if (value is null or DBNull) return default;
        var type = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(value, type);
    }

    public static List<T> Query<T>(this SqliteConnection connection, string sql, Func<SqliteDataReader, T> map, params object?[] args)
    {
        using var command = connection.Prepare(sql, args);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(map(reader));
        return rows;
    }

    /// <summary>
    /// Runs <paramref name="body"/> in one transaction, rolled back if it throws.
    /// Plain <c>BEGIN</c>/<c>COMMIT</c> rather than <c>BeginTransaction()</c>, which
    /// would make every command inside have to carry the transaction object.
    /// </summary>
    public static void InTransaction(this SqliteConnection connection, Action body)
    {
        connection.Execute("BEGIN IMMEDIATE");
        try
        {
            body();
            connection.Execute("COMMIT");
        }
        catch
        {
            connection.Execute("ROLLBACK");
            throw;
        }
    }

    /// <summary>Binds <c>$1</c>, <c>$2</c>, … to <paramref name="args"/> in order.</summary>
    private static SqliteCommand Prepare(this SqliteConnection connection, string sql, object?[] args)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < args.Length; i++)
            command.Parameters.AddWithValue($"${i + 1}", args[i] ?? DBNull.Value);
        return command;
    }

    public static string? NullableString(this SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    public static long? NullableInt64(this SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    public static bool Boolean(this SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return !reader.IsDBNull(ordinal) && reader.GetInt64(ordinal) != 0;
    }

    public static byte[] Blob(this SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.GetValue(ordinal) switch
        {
            byte[] bytes => bytes,
            // SQLite is dynamically typed; a blob column can still hold text.
            string text => System.Text.Encoding.UTF8.GetBytes(text),
            _ => [],
        };
    }
}
