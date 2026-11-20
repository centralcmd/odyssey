using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MySqlConnector;
using Odyssey.Context;

namespace Odyssey.IntegrationTests;

/// <summary>
/// One database migrated through the real migration chain — to head, or to a chosen migration — copied into each
/// private database a test asks for.
/// </summary>
/// <remarks>
/// <para>
/// Most private-database tests start from the same state: head, or (for a <see cref="MigrationSeam"/> test of a data
/// migration) the migration just before the one under test. Replaying the chain to get there was most of this tier's
/// runtime, and its result is the same every time. A template runs the chain once per container and per target
/// migration; a copy is the template's own <c>SHOW CREATE TABLE</c> output replayed under
/// <c>FOREIGN_KEY_CHECKS = 0</c>, followed by its rows (the seed data and <c>__EFMigrationsHistory</c>, so a later
/// <c>MigrateAsync</c> picks up exactly where the template stopped: a no-op at head, the pending migrations below it).
/// </para>
/// <para>
/// A copy is only as good as its fidelity. <c>SHOW CREATE TABLE</c> carries columns, generated columns, indexes,
/// <c>CHECK</c> constraints, foreign keys and table options, which is everything the migrations create: they create no
/// view, trigger, routine, event or sequence, and <see cref="CaptureAsync"/> refuses to build a template if one ever
/// appears. <c>MigratedSchemaTemplateTests</c> compares a copy with a database migrated the slow way, at head and at an
/// intermediate migration, so a difference fails one test instead of quietly weakening every test built on a copy.
/// </para>
/// <para>
/// What a copy cannot give is an EMPTY database: a test whose point is that the chain applies from nothing (the
/// <c>MigrationRunner</c> drift guard, the seeder) still migrates from empty.
/// </para>
/// </remarks>
internal sealed class MigratedSchemaTemplate
{
    /// <summary>The template migrated to head.</summary>
    public const string HeadDatabase = "odyssey_template";

    private readonly string charset;
    private readonly string collation;
    private readonly string body;

    private MigratedSchemaTemplate(string database, string charset, string collation, string body)
    {
        Database = database;
        this.charset = charset;
        this.collation = collation;
        this.body = body;
    }

    /// <summary>The template's own database, which is never a copy target.</summary>
    public string Database { get; }

    /// <summary>
    /// The template database for <paramref name="migrationId"/>, or <see cref="HeadDatabase"/> for head (<c>null</c>).
    /// </summary>
    /// <remarks>Derived from a hash of the id rather than the id itself: a migration id is up to ~150 characters and
    /// MariaDB caps a database name at 64. Sixteen hex digits keep the name at 33.</remarks>
    public static string DatabaseFor(string? migrationId)
    {
        if (migrationId is null)
        {
            return HeadDatabase;
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(migrationId)));
        return $"{HeadDatabase}_{hash[..16]}";
    }

    /// <summary>Whether <paramref name="database"/> is a name <see cref="DatabaseFor"/> can produce — a template that a
    /// copy must never overwrite.</summary>
    public static bool IsTemplateName(string database) => TemplateName.IsMatch(database);

    private static readonly Regex TemplateName = new(
        $"^{HeadDatabase}(_[0-9a-f]{{16}})?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Migrates a fresh template database to <paramref name="migrationId"/> (head when <c>null</c>), then records how
    /// to copy it.
    /// </summary>
    public static async Task<MigratedSchemaTemplate> BuildAsync(
        string serverConnectionString, Func<string, string> connectionStringFor, string? migrationId)
    {
        var database = DatabaseFor(migrationId);
        await using (var server = new MySqlConnection(serverConnectionString))
        {
            await server.OpenAsync();
            await ExecuteAsync(server, $"DROP DATABASE IF EXISTS `{database}`; CREATE DATABASE `{database}`;");
        }

        // Unpooled: the template is read once, here, and never again. A pool per template would keep its idle
        // connections open for the rest of the run, one more pool for every target migration the tier asks for.
        var templateConnectionString = new MySqlConnectionStringBuilder(connectionStringFor(database)) { Pooling = false }
            .ConnectionString;
        var options = new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(templateConnectionString, ServerVersion.AutoDetect(templateConnectionString))
            .Options;
        await using (var context = new OdysseyContext(options))
        {
            if (migrationId is null)
            {
                await context.Database.MigrateAsync();
            }
            else
            {
                await context.GetService<IMigrator>().MigrateAsync(migrationId);
            }
        }

        await using var connection = new MySqlConnection(templateConnectionString);
        await connection.OpenAsync();
        return await CaptureAsync(connection, database);
    }

    /// <summary>Drops <paramref name="target"/> if it exists and recreates it as a copy of the template.</summary>
    public async Task CopyToAsync(MySqlConnection server, string target)
    {
        if (target.Contains('`') || IsTemplateName(target))
        {
            throw new ArgumentException($"'{target}' cannot be a copy target.", nameof(target));
        }

        await ExecuteAsync(server,
            $"DROP DATABASE IF EXISTS `{target}`; " +
            $"CREATE DATABASE `{target}` CHARACTER SET {charset} COLLATE {collation}; " +
            $"USE `{target}`; " +
            body);
    }

    private static async Task<MigratedSchemaTemplate> CaptureAsync(MySqlConnection connection, string database)
    {
        var unsupported = await ReadAsync(connection,
            $"""
            SELECT CONCAT('view ', TABLE_NAME) FROM information_schema.VIEWS WHERE TABLE_SCHEMA = '{database}'
            UNION ALL SELECT CONCAT('trigger ', TRIGGER_NAME) FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA = '{database}'
            UNION ALL SELECT CONCAT('routine ', ROUTINE_NAME) FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = '{database}'
            UNION ALL SELECT CONCAT('event ', EVENT_NAME) FROM information_schema.EVENTS WHERE EVENT_SCHEMA = '{database}'
            UNION ALL SELECT CONCAT(TABLE_TYPE, ' ', TABLE_NAME) FROM information_schema.TABLES
                WHERE TABLE_SCHEMA = '{database}' AND TABLE_TYPE NOT IN ('BASE TABLE', 'VIEW')
            """,
            reader => reader.GetString(0));
        if (unsupported.Count > 0)
        {
            throw new InvalidOperationException(
                "The migrated schema holds objects SHOW CREATE TABLE does not copy, so a template copy would differ from a " +
                $"migrated database: {string.Join(", ", unsupported)}. Teach {nameof(MigratedSchemaTemplate)} to copy them.");
        }

        var (charset, collation) = (await ReadAsync(connection,
            $"SELECT DEFAULT_CHARACTER_SET_NAME, DEFAULT_COLLATION_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = '{database}'",
            reader => (reader.GetString(0), reader.GetString(1)))).Single();

        var tables = await ReadAsync(connection,
            $"SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = '{database}' AND TABLE_TYPE = 'BASE TABLE' ORDER BY TABLE_NAME",
            reader => reader.GetString(0));

        // Generated columns are computed by the copy itself and cannot be inserted into.
        var insertable = (await ReadAsync(connection,
                $"SELECT TABLE_NAME, COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = '{database}' AND IS_GENERATED = 'NEVER' ORDER BY TABLE_NAME, ORDINAL_POSITION",
                reader => (Table: reader.GetString(0), Column: reader.GetString(1))))
            .GroupBy(column => column.Table)
            .ToDictionary(group => group.Key, group => string.Join(", ", group.Select(column => $"`{column.Column}`")));

        var body = new StringBuilder("SET FOREIGN_KEY_CHECKS = 0; ");
        var copies = new StringBuilder();
        foreach (var table in tables)
        {
            var create = (await ReadAsync(connection, $"SHOW CREATE TABLE `{database}`.`{table}`", reader => reader.GetString(1))).Single();
            body.Append(create).Append("; ");

            var hasRows = (await ReadAsync(connection, $"SELECT EXISTS (SELECT 1 FROM `{database}`.`{table}`)", reader => reader.GetBoolean(0))).Single();
            if (hasRows)
            {
                var columns = insertable[table];
                copies.Append($"INSERT INTO `{table}` ({columns}) SELECT {columns} FROM `{database}`.`{table}`; ");
            }
        }

        body.Append(copies).Append("SET FOREIGN_KEY_CHECKS = 1;");
        return new MigratedSchemaTemplate(database, charset, collation, body.ToString());
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
