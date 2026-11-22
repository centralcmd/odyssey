using System.Data.Common;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MySqlConnector;
using Odyssey.Context;
using Odyssey.Context.Migrations;
using Odyssey.MigrationService.Fingerprint;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The test-only half of the schema fingerprint (issue #581 §5): the <c>[parity-rows]</c> section — every row of
/// every table — appended to the operator sections the migrations job writes.
/// </summary>
/// <remarks>
/// <para>
/// This is the only code that reads rows from tables outside <see cref="SeededTables"/>, and it never ships: it lives
/// in the integration tier and refuses a database that is not one the tier created for the purpose — its name must
/// start with <see cref="DatabasePrefix"/>, no account may exist, and its history must hold only ids this build
/// ships. On a freshly migrated database every row is seed data, which is why comparing all of them is safe and is
/// what catches a seeded default, or a raw-SQL insert, that a rebaseline forgot.
/// </para>
/// <para>
/// A column whose value is generated at migration time would differ between two fresh databases. Each one is a
/// declared <see cref="VolatileColumn"/> carrying a reason and the shape its value must have; the value is checked
/// against that shape and replaced by a placeholder.
/// </para>
/// </remarks>
internal static partial class ParityFingerprint
{
    /// <summary>The name every parity database starts with; the writer refuses any other.</summary>
    public const string DatabasePrefix = "odyssey_parity_";

    /// <summary>The committed fingerprint for <paramref name="baselineId"/>, as copied to the test output.</summary>
    public static string CommittedPath(string baselineId) =>
        Path.Combine(AppContext.BaseDirectory, "Baseline", $"{baselineId}.fingerprint.txt");

    /// <summary>The same file in the checkout, for the explicit regeneration step of the runbook.</summary>
    public static string SourcePath(string baselineId)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Odyssey.sln")))
        {
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        return Path.Combine(dir ?? throw new InvalidOperationException("No repository root."),
            "Odyssey.Context", "Migrations", "Baseline", $"{baselineId}.fingerprint.txt");
    }

    /// <param name="Reason">Why the value differs between two fresh migrations.</param>
    /// <param name="Shape">What the value must look like; anything else fails rather than being masked.</param>
    internal sealed record VolatileColumn(string Table, string Column, string Reason, Regex Shape);

    /// <summary>The declared volatile columns. Empty is the expected steady state.</summary>
    internal static IReadOnlyList<VolatileColumn> VolatileColumns { get; } = [];

    /// <summary>The placeholder a volatile value is replaced by.</summary>
    public const string VolatilePlaceholder = "<volatile>";

    /// <summary>Drops and recreates <paramref name="database"/> migrated to exactly <paramref name="migrationId"/>.</summary>
    public static async Task<string> CreateAtAsync(MariaDbFixture fixture, string database, string migrationId)
    {
        if (!database.StartsWith(DatabasePrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException($"A parity database's name starts with '{DatabasePrefix}'.", nameof(database));
        }

        await using (var server = new MySqlConnection(fixture.OdysseyConnectionString))
        {
            await server.OpenAsync();
            await using var command = new MySqlCommand($"DROP DATABASE IF EXISTS `{database}`; CREATE DATABASE `{database}`;", server);
            await command.ExecuteNonQueryAsync();
        }

        var connectionString = new MySqlConnectionStringBuilder(fixture.ConnectionStringFor(database)) { Pooling = false }.ConnectionString;
        await using var context = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connectionString, new MariaDbServerVersion(new Version(11, 4, 0))).Options);
        await context.GetService<IMigrator>().MigrateAsync(migrationId);
        return connectionString;
    }

    public static async Task DropAsync(MariaDbFixture fixture, string database)
    {
        await using var server = new MySqlConnection(fixture.OdysseyConnectionString);
        await server.OpenAsync();
        await using var command = new MySqlCommand($"DROP DATABASE IF EXISTS `{database}`", server);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>The full fingerprint — operator sections and <c>[parity-rows]</c> — of a parity database.</summary>
    public static async Task<string> CaptureAsync(string connectionString)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();

        var output = new StringWriter();
        await SchemaFingerprint.WriteOperatorSectionsAsync(connection, output);
        await WriteParityRowsAsync(connection, output);
        return output.ToString();
    }

    /// <summary>Appends <c>[parity-rows]</c>: every row of every base table but the history, in key order.</summary>
    /// <param name="volatileColumns">The exclusions to apply; <see cref="VolatileColumns"/> when null.</param>
    public static async Task WriteParityRowsAsync(
        DbConnection connection, TextWriter output, IReadOnlyList<VolatileColumn>? volatileColumns = null)
    {
        volatileColumns ??= VolatileColumns;
        await RefuseUnlessParityDatabaseAsync(connection);

        var tables = await SchemaCapture.ReadBaseTablesAsync(connection);
        var columns = await SchemaCapture.ReadColumnsAsync(connection);
        var keys = await SchemaCapture.ReadPrimaryKeysAsync(connection);

        output.Write($"{SchemaFingerprint.ParityRowsSection}\n");
        foreach (var table in tables.Where(t => t != SchemaFingerprint.HistoryTable))
        {
            var names = columns[table].Select(c => c.Column).ToList();
            var order = keys.TryGetValue(table, out var key) ? key : names;
            output.Write($"table {SchemaCapture.Quote(table)}\n{string.Join('\t', names)}\n");

            var volatileAt = names
                .Select((name, index) => (index, column: volatileColumns.SingleOrDefault(v => v.Table == table && v.Column == name)))
                .Where(pair => pair.column is not null)
                .ToDictionary(pair => pair.index, pair => pair.column!);

            await using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT {string.Join(", ", names.Select(SchemaCapture.Quote))} FROM {SchemaCapture.Quote(table)} " +
                $"ORDER BY {string.Join(", ", order.Select(SchemaCapture.Quote))}";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var fields = new string[reader.FieldCount];
                for (var i = 0; i < fields.Length; i++)
                {
                    var text = SchemaFingerprint.FormatValue(reader.GetValue(i));
                    if (volatileAt.TryGetValue(i, out var volatileColumn))
                    {
                        if (!volatileColumn.Shape.IsMatch(text))
                        {
                            throw new InvalidOperationException(
                                $"Volatile column {table}.{names[i]} holds a value that is not {volatileColumn.Shape}.");
                        }

                        text = VolatilePlaceholder;
                    }

                    fields[i] = text;
                }

                output.Write(string.Join('\t', fields) + "\n");
            }
        }
    }

    /// <summary>
    /// Throws before reading any row unless the connection's database is one this tier made: the prefix, no
    /// account, and a history of ids this build ships only.
    /// </summary>
    public static async Task RefuseUnlessParityDatabaseAsync(DbConnection connection)
    {
        var database = await ScalarAsync(connection, "SELECT DATABASE()") as string ?? "";
        if (!database.StartsWith(DatabasePrefix, StringComparison.Ordinal))
        {
            throw new NotATestDatabaseException($"'{database}' is not a parity database (its name lacks '{DatabasePrefix}').");
        }

        var tables = await SchemaCapture.ReadBaseTablesAsync(connection);
        if (tables.Contains("AspNetUsers")
            && Convert.ToInt64(await ScalarAsync(connection, "SELECT COUNT(*) FROM `AspNetUsers`")) != 0)
        {
            throw new NotATestDatabaseException($"'{database}' holds accounts, so it is not a freshly migrated database.");
        }

        if (tables.Contains(SchemaFingerprint.HistoryTable))
        {
            using var context = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
                .UseMySql("server=localhost;database=odyssey", new MariaDbServerVersion(new Version(11, 4, 0))).Options);
            var known = context.GetService<IMigrationsAssembly>().Migrations.Keys.ToHashSet(StringComparer.Ordinal);

            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT MigrationId FROM {SchemaCapture.Quote(SchemaFingerprint.HistoryTable)}";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (!known.Contains(reader.GetString(0)))
                {
                    throw new NotATestDatabaseException($"'{database}' was not built by this build's migrations.");
                }
            }
        }
    }

    private static async Task<object?> ScalarAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    /// <summary>A unified-style list of the lines that differ, for a readable failure.</summary>
    public static string Diff(string expected, string actual)
    {
        var expectedLines = expected.Split('\n');
        var actualLines = actual.Split('\n');
        var report = new StringBuilder();
        var shown = 0;
        for (var i = 0; i < Math.Max(expectedLines.Length, actualLines.Length) && shown < 40; i++)
        {
            var e = i < expectedLines.Length ? expectedLines[i] : "<end>";
            var a = i < actualLines.Length ? actualLines[i] : "<end>";
            if (e != a)
            {
                report.Append($"line {i + 1}:\n- {e}\n+ {a}\n");
                shown++;
            }
        }

        return report.ToString();
    }

    /// <summary>The committed fingerprint's baseline id, from the declaration.</summary>
    public static string BaselineId => MigrationBaseline.Current.BaselineMigrationId;
}

/// <summary>The parity writer was pointed at a database the tier did not create.</summary>
internal sealed class NotATestDatabaseException(string message) : InvalidOperationException(message);
