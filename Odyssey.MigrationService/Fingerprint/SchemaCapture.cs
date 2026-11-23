using System.Data.Common;

namespace Odyssey.MigrationService.Fingerprint;

/// <summary>
/// The one reader of what a schema is (issue #581 A-N2): the schema fingerprint and the integration tier's
/// <c>MigratedSchemaTemplate</c> both build on it, so the two cannot disagree. Every read is against the
/// connection's current database (<c>DATABASE()</c>) and touches only <c>information_schema</c> and
/// <c>SHOW CREATE TABLE</c> — never a table's rows.
/// </summary>
internal static class SchemaCapture
{
    /// <summary>Views, triggers, routines, events and any non-base table, which <c>SHOW CREATE TABLE</c> does not carry.</summary>
    public static Task<List<string>> ReadUnsupportedObjectsAsync(DbConnection connection, CancellationToken ct = default) =>
        ReadAsync(connection,
            """
            SELECT CONCAT('view ', TABLE_NAME) FROM information_schema.VIEWS WHERE TABLE_SCHEMA = DATABASE()
            UNION ALL SELECT CONCAT('trigger ', TRIGGER_NAME) FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA = DATABASE()
            UNION ALL SELECT CONCAT('routine ', ROUTINE_NAME) FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = DATABASE()
            UNION ALL SELECT CONCAT('event ', EVENT_NAME) FROM information_schema.EVENTS WHERE EVENT_SCHEMA = DATABASE()
            UNION ALL SELECT CONCAT(TABLE_TYPE, ' ', TABLE_NAME) FROM information_schema.TABLES
                WHERE TABLE_SCHEMA = DATABASE() AND TABLE_TYPE NOT IN ('BASE TABLE', 'VIEW')
            """,
            reader => reader.GetString(0), ct);

    /// <summary>The database's default character set and collation.</summary>
    public static async Task<(string Charset, string Collation)> ReadDefaultsAsync(DbConnection connection, CancellationToken ct = default) =>
        (await ReadAsync(connection,
            "SELECT DEFAULT_CHARACTER_SET_NAME, DEFAULT_COLLATION_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = DATABASE()",
            reader => (reader.GetString(0), reader.GetString(1)), ct)).Single();

    /// <summary>Every base table, ordered by name.</summary>
    public static Task<List<string>> ReadBaseTablesAsync(DbConnection connection, CancellationToken ct = default) =>
        ReadAsync(connection,
            "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_TYPE = 'BASE TABLE' ORDER BY TABLE_NAME",
            reader => reader.GetString(0), ct);

    /// <summary>The <c>CREATE TABLE</c> statement the engine reports for <paramref name="table"/>, verbatim.</summary>
    public static async Task<string> ShowCreateTableAsync(DbConnection connection, string table, CancellationToken ct = default) =>
        (await ReadAsync(connection, $"SHOW CREATE TABLE {Quote(table)}", reader => reader.GetString(1), ct)).Single();

    /// <summary>Each table's columns in ordinal order, with whether the column is generated.</summary>
    public static async Task<Dictionary<string, List<(string Column, bool Generated)>>> ReadColumnsAsync(
        DbConnection connection, CancellationToken ct = default) =>
        (await ReadAsync(connection,
            "SELECT TABLE_NAME, COLUMN_NAME, IS_GENERATED FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() ORDER BY TABLE_NAME, ORDINAL_POSITION",
            reader => (Table: reader.GetString(0), Column: reader.GetString(1), Generated: reader.GetString(2) != "NEVER"), ct))
        .GroupBy(column => column.Table, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Select(c => (c.Column, c.Generated)).ToList(), StringComparer.Ordinal);

    /// <summary>Each table's primary-key columns in key order; a table without one is absent.</summary>
    public static async Task<Dictionary<string, List<string>>> ReadPrimaryKeysAsync(DbConnection connection, CancellationToken ct = default) =>
        (await ReadAsync(connection,
            """
            SELECT TABLE_NAME, COLUMN_NAME FROM information_schema.KEY_COLUMN_USAGE
            WHERE TABLE_SCHEMA = DATABASE() AND CONSTRAINT_NAME = 'PRIMARY'
            ORDER BY TABLE_NAME, ORDINAL_POSITION
            """,
            reader => (Table: reader.GetString(0), Column: reader.GetString(1)), ct))
        .GroupBy(key => key.Table, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Select(k => k.Column).ToList(), StringComparer.Ordinal);

    /// <summary>The server's version string.</summary>
    public static async Task<string> ReadServerVersionAsync(DbConnection connection, CancellationToken ct = default) =>
        (await ReadAsync(connection, "SELECT VERSION()", reader => reader.GetString(0), ct)).Single();

    /// <summary>Backtick-quotes an identifier the engine itself reported.</summary>
    public static string Quote(string identifier) =>
        identifier.Contains('`', StringComparison.Ordinal)
            ? throw new ArgumentException($"Identifier '{identifier}' cannot be quoted.", nameof(identifier))
            : $"`{identifier}`";

    private static async Task<List<T>> ReadAsync<T>(
        DbConnection connection, string sql, Func<DbDataReader, T> read, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<T>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(read(reader));
        }

        return rows;
    }
}
