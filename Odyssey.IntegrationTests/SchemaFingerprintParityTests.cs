using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Odyssey.Context.Migrations;
using Odyssey.MigrationService.Fingerprint;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The parity test and the fingerprint mode against real MariaDB (issue #581 AC 10–13, 15, 16, 23, 24, 26).
/// </summary>
/// <remarks>
/// <para>
/// The committed <c>Odyssey.Context/Migrations/Baseline/&lt;baseline-id&gt;.fingerprint.txt</c> is what an empty
/// database migrated to the baseline id — not head, so later migrations never force it to change — must produce.
/// After a rebaseline it is the old chain's output at the cut, so this test is what proves the squashed baseline
/// builds the same schema and the same seed rows.
/// </para>
/// <para>
/// Regenerating it is a deliberate runbook step (docs/migration-rebaseline.md), never a fix for a failing
/// comparison: set <c>ODYSSEY_WRITE_BASELINE_FINGERPRINT=1</c> (and <c>ODYSSEY_FINGERPRINT_MIGRATION</c> to the cut id
/// during a rebaseline) and run
/// <see cref="Regenerate_the_committed_fingerprint_when_asked"/> on the tree whose chain is the reference.
/// </para>
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class SchemaFingerprintParityTests(MariaDbFixture fixture)
{
    private const string First = ParityFingerprint.DatabasePrefix + "first";
    private const string Second = ParityFingerprint.DatabasePrefix + "second";

    private static string Baseline => MigrationBaseline.Current.BaselineMigrationId;

    private static string Committed() => File.ReadAllText(ParityFingerprint.CommittedPath(Baseline));

    [SkippableFact]
    public async Task Regenerate_the_committed_fingerprint_when_asked()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("ODYSSEY_WRITE_BASELINE_FINGERPRINT") == "1",
            "Regenerating the committed fingerprint is a deliberate runbook step; set ODYSSEY_WRITE_BASELINE_FINGERPRINT=1.");
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        // A rebaseline captures the reference at the CUT, which on the pre-squash tree is not yet the declared
        // baseline; ODYSSEY_FINGERPRINT_MIGRATION names it. Unset, the declared baseline is regenerated.
        var target = Environment.GetEnvironmentVariable("ODYSSEY_FINGERPRINT_MIGRATION") is { Length: > 0 } cut ? cut : Baseline;
        try
        {
            var fingerprint = await ParityFingerprint.CaptureAsync(await ParityFingerprint.CreateAtAsync(fixture, First, target));
            await File.WriteAllTextAsync(ParityFingerprint.SourcePath(target), fingerprint);
        }
        finally
        {
            await ParityFingerprint.DropAsync(fixture, First);
        }
    }

    /// <summary>AC 10 — the baseline, applied to an empty database, produces exactly the committed fingerprint.</summary>
    [SkippableFact]
    public async Task The_baseline_builds_exactly_the_committed_fingerprint()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        try
        {
            var actual = await ParityFingerprint.CaptureAsync(await ParityFingerprint.CreateAtAsync(fixture, First, Baseline));

            AssertSame(Committed(), actual);
        }
        finally
        {
            await ParityFingerprint.DropAsync(fixture, First);
        }
    }

    /// <summary>
    /// AC 10–11, 13 — the comparison sees a changed CHECK, a changed seeded default, an inserted row and a view.
    /// Each change is made to the DATABASE and read back through the real capture, so every case can fail.
    /// </summary>
    [SkippableFact]
    public async Task The_comparison_fails_on_a_check_a_seeded_default_an_inserted_row_and_a_view()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        try
        {
            var connectionString = await ParityFingerprint.CreateAtAsync(fixture, First, Baseline);
            var committed = SchemaFingerprint.WithoutHeader(Committed());
            async Task<string> CaptureAsync() => SchemaFingerprint.WithoutHeader(await ParityFingerprint.CaptureAsync(connectionString));
            Assert.Equal(committed, await CaptureAsync());

            // A CHECK expression altered: dropped and re-added under its own name with a different clause.
            var (table, name, clause) = await FirstCheckAsync(connectionString);
            await ExecuteAsync(connectionString,
                $"ALTER TABLE `{table}` DROP CONSTRAINT `{name}`; ALTER TABLE `{table}` ADD CONSTRAINT `{name}` CHECK (({clause}) AND 1 = 1)");
            var withCheck = await CaptureAsync();
            Assert.NotEqual(committed, withCheck);
            Assert.Contains("1 = 1", withCheck[..withCheck.IndexOf(SchemaFingerprint.SeedRowsSection, StringComparison.Ordinal)]);
            await ExecuteAsync(connectionString,
                $"ALTER TABLE `{table}` DROP CONSTRAINT `{name}`; ALTER TABLE `{table}` ADD CONSTRAINT `{name}` CHECK ({clause})");
            Assert.Equal(committed, await CaptureAsync());

            // A seeded SystemSettings default changed.
            await ExecuteAsync(connectionString,
                "UPDATE `SystemSettings` SET `Value` = CONCAT(`Value`, '0') ORDER BY `Key` LIMIT 1");
            Assert.NotEqual(committed, await CaptureAsync());
            await ExecuteAsync(connectionString,
                "UPDATE `SystemSettings` SET `Value` = LEFT(`Value`, CHAR_LENGTH(`Value`) - 1) ORDER BY `Key` LIMIT 1");
            Assert.Equal(committed, await CaptureAsync());

            // A row inserted into the database.
            await ExecuteAsync(connectionString,
                "INSERT INTO `Currencies` (`CurrencyCode`, `Name`, `MinorUnits`) VALUES ('ZZZ', 'Test', 2)");
            Assert.NotEqual(committed, await CaptureAsync());
            await ExecuteAsync(connectionString, "DELETE FROM `Currencies` WHERE `CurrencyCode` = 'ZZZ'");
            Assert.Equal(committed, await CaptureAsync());

            // A view.
            await ExecuteAsync(connectionString, "CREATE VIEW `ParityProbe` AS SELECT 1 AS `One`");
            var withView = await CaptureAsync();
            Assert.Contains("other object: view ParityProbe", withView);
            Assert.NotEqual(committed, withView);
        }
        finally
        {
            await ParityFingerprint.DropAsync(fixture, First);
        }
    }

    private static async Task<(string Table, string Name, string Clause)> FirstCheckAsync(string connectionString)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(
            "SELECT TABLE_NAME, CONSTRAINT_NAME, CHECK_CLAUSE FROM information_schema.CHECK_CONSTRAINTS " +
            "WHERE CONSTRAINT_SCHEMA = DATABASE() ORDER BY TABLE_NAME, CONSTRAINT_NAME LIMIT 1", connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "The baseline creates no CHECK constraint to alter.");
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    /// <summary>
    /// The volatile-column mechanism itself, with a synthetic exclusion (the declared list is empty today, so the
    /// live test above cannot exercise it): a value of the declared shape is masked, any other is refused.
    /// </summary>
    [SkippableFact]
    public async Task A_volatile_column_is_masked_only_when_its_value_has_the_declared_shape()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        try
        {
            var connectionString = await ParityFingerprint.CreateAtAsync(fixture, First, Baseline);
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();

            var guid = new ParityFingerprint.VolatileColumn("AspNetRoles", "ConcurrencyStamp", "synthetic",
                new System.Text.RegularExpressions.Regex("^[0-9a-f-]{36}$"));
            var masked = new StringWriter();
            await ParityFingerprint.WriteParityRowsAsync(connection, masked, [guid]);
            var roles = masked.ToString()[masked.ToString().IndexOf("table `AspNetRoles`", StringComparison.Ordinal)..]
                .Split('\n').Skip(2).Take(4).ToList();
            Assert.All(roles, row => Assert.EndsWith("\t" + ParityFingerprint.VolatilePlaceholder, row));

            var digits = guid with { Shape = new System.Text.RegularExpressions.Regex("^[0-9]+$") };
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => ParityFingerprint.WriteParityRowsAsync(connection, new StringWriter(), [digits]));
        }
        finally
        {
            await ParityFingerprint.DropAsync(fixture, First);
        }
    }

    /// <summary>AC 12 — two independently migrated databases fingerprint identically, and a re-run is identical.</summary>
    [SkippableFact]
    public async Task The_fingerprint_is_deterministic()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        try
        {
            var first = await ParityFingerprint.CreateAtAsync(fixture, First, Baseline);
            var second = await ParityFingerprint.CreateAtAsync(fixture, Second, Baseline);

            var a = await ParityFingerprint.CaptureAsync(first);
            Assert.Equal(a, await ParityFingerprint.CaptureAsync(first));
            AssertSame(SchemaFingerprint.WithoutHeader(a), SchemaFingerprint.WithoutHeader(await ParityFingerprint.CaptureAsync(second)));
        }
        finally
        {
            await ParityFingerprint.DropAsync(fixture, First);
            await ParityFingerprint.DropAsync(fixture, Second);
        }
    }

    /// <summary>
    /// AC 23 and AC 15 — the fingerprint MODE, as an operator runs it, prints exactly the committed file's operator
    /// sections, and changes nothing.
    /// </summary>
    [SkippableFact]
    public async Task The_fingerprint_mode_prints_the_committed_operator_sections_and_changes_nothing()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        try
        {
            var connectionString = await ParityFingerprint.CreateAtAsync(fixture, First, Baseline);
            var before = await ParityFingerprint.CaptureAsync(connectionString);
            var (output, error) = (new StringWriter(), new StringWriter());

            var exit = await SchemaFingerprintCommand.RunAsync(Configuration(connectionString), output, error);

            Assert.Equal(SchemaFingerprintCommand.Succeeded, exit);
            Assert.Equal("", error.ToString());
            Assert.Equal(
                SchemaFingerprint.WithoutHeader(SchemaFingerprint.OperatorSectionsOf(Committed())),
                SchemaFingerprint.WithoutHeader(output.ToString()));
            Assert.Equal(before, await ParityFingerprint.CaptureAsync(connectionString));
        }
        finally
        {
            await ParityFingerprint.DropAsync(fixture, First);
        }
    }

    /// <summary>The mode's session refuses a write, whatever the account could do.</summary>
    [SkippableFact]
    public async Task The_fingerprint_session_is_read_only()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        try
        {
            var connectionString = await ParityFingerprint.CreateAtAsync(fixture, First, Baseline);
            await using var connection = await SchemaFingerprintCommand.OpenReadOnlyAsync(connectionString);
            await using var insert = new MySqlCommand(
                "INSERT INTO `Currencies` (`CurrencyCode`, `Name`, `MinorUnits`) VALUES ('ZZZ', 'Test', 2)", connection);

            var error = await Assert.ThrowsAsync<MySqlException>(() => insert.ExecuteNonQueryAsync());
            Assert.Equal(MySqlErrorCode.CannotExecuteInReadOnlyTransaction, error.ErrorCode);
        }
        finally
        {
            await ParityFingerprint.DropAsync(fixture, First);
        }
    }

    /// <summary>AC 16 — the mode's output carries no setting value, no currency, no account and no credential.</summary>
    [SkippableFact]
    public async Task The_fingerprint_mode_prints_no_values_beyond_roles_and_no_credentials()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        try
        {
            var connectionString = await ParityFingerprint.CreateAtAsync(fixture, First, Baseline);
            var output = new StringWriter();
            await SchemaFingerprintCommand.RunAsync(Configuration(connectionString), output, new StringWriter());
            var text = output.ToString();
            var seeds = text[text.IndexOf(SchemaFingerprint.SeedRowsSection, StringComparison.Ordinal)..];

            Assert.DoesNotContain(SchemaFingerprint.ParityRowsSection, text);
            Assert.DoesNotContain("table `Currencies`", seeds);
            Assert.DoesNotContain("table `AspNetUsers`", seeds);
            Assert.DoesNotContain("table `SystemSettingSecrets`", seeds);

            // SystemSettings is keys-only: its block is the key column header and one key per line, no tab.
            var keysSection = seeds[seeds.IndexOf(SchemaFingerprint.SeedKeysSection, StringComparison.Ordinal)..];
            var settings = keysSection[keysSection.IndexOf("table `SystemSettings`", StringComparison.Ordinal)..]
                .Split('\n').Skip(1).TakeWhile(line => !line.StartsWith("table ", StringComparison.Ordinal) && line.Length > 0).ToList();
            Assert.NotEmpty(settings);
            Assert.All(settings, line => Assert.DoesNotContain('\t', line));

            var builder = new MySqlConnectionStringBuilder(connectionString);
            Assert.DoesNotContain(builder.Password, text);
            Assert.DoesNotContain($"{builder.Server}:{builder.Port}", text);
        }
        finally
        {
            await ParityFingerprint.DropAsync(fixture, First);
        }
    }

    /// <summary>AC 24 — the all-rows writer refuses a database the tier did not create, before reading a row.</summary>
    [SkippableFact]
    public async Task The_all_rows_writer_refuses_a_database_it_did_not_create()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        const string Other = "odyssey_not_parity";
        try
        {
            await using (var server = new MySqlConnection(fixture.OdysseyConnectionString))
            {
                await server.OpenAsync();
                await using var create = new MySqlCommand($"DROP DATABASE IF EXISTS `{Other}`; CREATE DATABASE `{Other}`;", server);
                await create.ExecuteNonQueryAsync();
            }

            await using var connection = new MySqlConnection(fixture.ConnectionStringFor(Other));
            await connection.OpenAsync();
            await Assert.ThrowsAsync<NotATestDatabaseException>(() => ParityFingerprint.WriteParityRowsAsync(connection, new StringWriter()));

            var parity = await ParityFingerprint.CreateAtAsync(fixture, First, Baseline);
            await ExecuteAsync(parity,
                "INSERT INTO `__EFMigrationsHistory` (MigrationId, ProductVersion) VALUES ('20990101000000_Foreign', '9.0.0')");
            await using var foreign = new MySqlConnection(parity);
            await foreign.OpenAsync();
            await Assert.ThrowsAsync<NotATestDatabaseException>(() => ParityFingerprint.WriteParityRowsAsync(foreign, new StringWriter()));
        }
        finally
        {
            await ParityFingerprint.DropAsync(fixture, Other);
            await ParityFingerprint.DropAsync(fixture, First);
        }
    }

    /// <summary>
    /// AC 26 — every volatile-column exclusion is real (the column differs between two fresh migrations), its values
    /// fit its declared shape, and no credential-like column can be masked.
    /// </summary>
    [SkippableFact]
    public async Task Volatile_column_exclusions_are_live_shaped_and_never_credentials()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        string[] credentialWords = ["token", "hash", "key", "secret", "password", "credential"];
        Assert.All(ParityFingerprint.VolatileColumns, column =>
        {
            Assert.False(string.IsNullOrWhiteSpace(column.Reason), $"{column.Table}.{column.Column} has no reason.");
            Assert.DoesNotContain(credentialWords, word => column.Column.Contains(word, StringComparison.OrdinalIgnoreCase));
        });

        try
        {
            var first = await ParityFingerprint.CreateAtAsync(fixture, First, Baseline);
            var second = await ParityFingerprint.CreateAtAsync(fixture, Second, Baseline);
            foreach (var column in ParityFingerprint.VolatileColumns)
            {
                var a = await ValuesAsync(first, column);
                var b = await ValuesAsync(second, column);
                Assert.NotEqual(a, b);
                Assert.All(a.Concat(b), value => Assert.Matches(column.Shape, value));
            }
        }
        finally
        {
            await ParityFingerprint.DropAsync(fixture, First);
            await ParityFingerprint.DropAsync(fixture, Second);
        }
    }

    private static async Task<List<string>> ValuesAsync(string connectionString, ParityFingerprint.VolatileColumn column)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(
            $"SELECT {SchemaCapture.Quote(column.Column)} FROM {SchemaCapture.Quote(column.Table)} ORDER BY 1", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(SchemaFingerprint.FormatValue(reader.GetValue(0)));
        }

        return values;
    }

    private static IConfiguration Configuration(string connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:OdysseyConnection"] = connectionString })
            .Build();

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static void AssertSame(string expected, string actual)
    {
        var e = SchemaFingerprint.WithoutHeader(expected);
        var a = SchemaFingerprint.WithoutHeader(actual);
        Assert.True(e == a, "The fingerprint differs from the committed one:\n" + ParityFingerprint.Diff(e, a));
    }
}
