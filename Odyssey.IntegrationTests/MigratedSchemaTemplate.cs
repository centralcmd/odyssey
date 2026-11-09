using System.Text;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Odyssey.Context;

namespace Odyssey.IntegrationTests;

/// <summary>
/// One database migrated to head through the real migration chain, copied into each private database a test asks for.
/// </summary>
/// <remarks>
/// <para>
/// Most private-database tests start by dropping their database and running every migration again. That replay was
/// most of this tier's runtime, and its result is the same every time. The template runs the chain once per container;
/// a copy is the template's own <c>SHOW CREATE TABLE</c> output replayed under <c>FOREIGN_KEY_CHECKS = 0</c>, followed
/// by its rows (the model's seed data and <c>__EFMigrationsHistory</c>, so a later <c>MigrateAsync</c> is a no-op).
/// </para>
/// <para>
/// A copy is only as good as its fidelity. <c>SHOW CREATE TABLE</c> carries columns, generated columns, indexes,
/// <c>CHECK</c> constraints, foreign keys and table options, which is everything the migrations create: they create no
/// view, trigger, routine, event or sequence, and <see cref="CaptureAsync"/> refuses to build a template if one ever
/// appears. <c>MigratedSchemaTemplateTests</c> compares a copy with a database migrated the slow way, so a difference
/// fails one test instead of quietly weakening every test built on a copy.
/// </para>
/// <para>
/// A test of a migration itself (the <see cref="MigrationSeam"/> tests) still migrates from empty: it needs the
/// database before head, which a copy of head cannot give it.
/// </para>
/// </remarks>
internal sealed class MigratedSchemaTemplate
{
    public const string Database = "odyssey_template";

    private readonly string charset;
    private readonly string collation;
    private readonly string body;

    private MigratedSchemaTemplate(string charset, string collation, string body)
    {
        this.charset = charset;
        this.collation = collation;
        this.body = body;
    }

    /// <summary>Migrates the template database to head, then records how to copy it.</summary>
    public static async Task<MigratedSchemaTemplate> BuildAsync(string serverConnectionString, string templateConnectionString)
    {
        await using (var server = new MySqlConnection(serverConnectionString))
        {
            await server.OpenAsync();
            await ExecuteAsync(server, $"DROP DATABASE IF EXISTS `{Database}`; CREATE DATABASE `{Database}`;");
        }

        var options = new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(templateConnectionString, ServerVersion.AutoDetect(templateConnectionString))
            .Options;
        await using (var context = new OdysseyContext(options))
        {
            await context.Database.MigrateAsync();
        }

        await using var connection = new MySqlConnection(templateConnectionString);
        await connection.OpenAsync();
        return await CaptureAsync(connection);
    }

    /// <summary>Drops <paramref name="target"/> if it exists and recreates it as a copy of the template.</summary>
    public async Task CopyToAsync(MySqlConnection server, string target)
    {
        if (target.Contains('`') || string.Equals(target, Database, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{target}' cannot be a copy target.", nameof(target));
        }

        await ExecuteAsync(server,
            $"DROP DATABASE IF EXISTS `{target}`; " +
            $"CREATE DATABASE `{target}` CHARACTER SET {charset} COLLATE {collation}; " +
            $"USE `{target}`; " +
            body);
    }

    private static async Task<MigratedSchemaTemplate> CaptureAsync(MySqlConnection connection)
    {
        var unsupported = await ReadAsync(connection,
            $"""
            SELECT CONCAT('view ', TABLE_NAME) FROM information_schema.VIEWS WHERE TABLE_SCHEMA = '{Database}'
            UNION ALL SELECT CONCAT('trigger ', TRIGGER_NAME) FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA = '{Database}'
            UNION ALL SELECT CONCAT('routine ', ROUTINE_NAME) FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = '{Database}'
            UNION ALL SELECT CONCAT('event ', EVENT_NAME) FROM information_schema.EVENTS WHERE EVENT_SCHEMA = '{Database}'
            UNION ALL SELECT CONCAT(TABLE_TYPE, ' ', TABLE_NAME) FROM information_schema.TABLES
                WHERE TABLE_SCHEMA = '{Database}' AND TABLE_TYPE NOT IN ('BASE TABLE', 'VIEW')
            """,
            reader => reader.GetString(0));
        if (unsupported.Count > 0)
        {
            throw new InvalidOperationException(
                "The migrated schema holds objects SHOW CREATE TABLE does not copy, so a template copy would differ from a " +
                $"migrated database: {string.Join(", ", unsupported)}. Teach {nameof(MigratedSchemaTemplate)} to copy them.");
        }

        var (charset, collation) = (await ReadAsync(connection,
            $"SELECT DEFAULT_CHARACTER_SET_NAME, DEFAULT_COLLATION_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = '{Database}'",
            reader => (reader.GetString(0), reader.GetString(1)))).Single();

        var tables = await ReadAsync(connection,
            $"SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = '{Database}' AND TABLE_TYPE = 'BASE TABLE' ORDER BY TABLE_NAME",
            reader => reader.GetString(0));

        // Generated columns are computed by the copy itself and cannot be inserted into.
        var insertable = (await ReadAsync(connection,
                $"SELECT TABLE_NAME, COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = '{Database}' AND IS_GENERATED = 'NEVER' ORDER BY TABLE_NAME, ORDINAL_POSITION",
                reader => (Table: reader.GetString(0), Column: reader.GetString(1))))
            .GroupBy(column => column.Table)
            .ToDictionary(group => group.Key, group => string.Join(", ", group.Select(column => $"`{column.Column}`")));

        var body = new StringBuilder("SET FOREIGN_KEY_CHECKS = 0; ");
        var copies = new StringBuilder();
        foreach (var table in tables)
        {
            var create = (await ReadAsync(connection, $"SHOW CREATE TABLE `{Database}`.`{table}`", reader => reader.GetString(1))).Single();
            body.Append(create).Append("; ");

            var hasRows = (await ReadAsync(connection, $"SELECT EXISTS (SELECT 1 FROM `{Database}`.`{table}`)", reader => reader.GetBoolean(0))).Single();
            if (hasRows)
            {
                var columns = insertable[table];
                copies.Append($"INSERT INTO `{table}` ({columns}) SELECT {columns} FROM `{Database}`.`{table}`; ");
            }
        }

        body.Append(copies).Append("SET FOREIGN_KEY_CHECKS = 1;");
        return new MigratedSchemaTemplate(charset, collation, body.ToString());
    }

    private static async Task ExecuteAsync(MySqlConnection connection, string sql)
    {
        await using var command = new MySqlCommand(sql, connection) { CommandTimeout = 300 };
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<T>> ReadAsync<T>(MySqlConnection connection, string sql, Func<MySqlDataReader, T> read)
    {
        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<T>();
        while (await reader.ReadAsync())
        {
            rows.Add(read(reader));
        }

        return rows;
    }
}
