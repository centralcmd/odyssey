using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Odyssey.Context;
using Odyssey.Context.Migrations;
using Odyssey.MigrationService.Fingerprint;
using Xunit;

namespace Odyssey.MigrationService.Tests;

/// <summary>
/// The fast-tier half of issue #581: the baseline declaration, the coupled-name registry and its lint, the seeded
/// table classification, the refusal messages, and the fingerprint's pure helpers. The real-engine half is in
/// <c>Odyssey.IntegrationTests</c>.
/// </summary>
public partial class MigrationRebaselineTests
{
    /// <summary>A relational context that is never dialled: enough for the migrations assembly and the model.</summary>
    internal static OdysseyContext UndialledContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql("server=localhost;database=odyssey", new MariaDbServerVersion(new Version(11, 4, 0)))
            .Options);

    private static IReadOnlyList<string> MigrationIds()
    {
        using var context = UndialledContext();
        return context.GetService<IMigrationsAssembly>().Migrations.Keys.Order(StringComparer.Ordinal).ToList();
    }

    // AC 1

    [Fact]
    public void The_current_baseline_is_InitialCreate_with_no_bridge_release()
    {
        Assert.Equal("20260829095318_InitialCreate", MigrationBaseline.Current.BaselineMigrationId);
        Assert.Null(MigrationBaseline.Current.BridgeRelease);
    }

    [Fact]
    public void The_baseline_is_the_earliest_migration_this_build_ships()
    {
        Assert.Equal(MigrationIds()[0], MigrationBaseline.Current.BaselineMigrationId);
    }

    [Fact]
    public void A_committed_fingerprint_exists_for_the_baseline_and_only_for_it()
    {
        var directory = Path.Combine(RepositoryFiles.Root, "Odyssey.Context", "Migrations", "Baseline");
        var files = Directory.GetFiles(directory, "*.fingerprint.txt").Select(Path.GetFileName).ToList();

        Assert.Equal([$"{MigrationBaseline.Current.BaselineMigrationId}.fingerprint.txt"], files);
    }

    // AC 18

    [Fact]
    public void Every_coupled_name_resolves_to_exactly_one_migration_that_is_not_the_baseline()
    {
        var problems = CoupledNameProblems(MigrationIds(), CoupledMigrationNames.All, MigrationBaseline.Current.BaselineMigrationId);

        Assert.Empty(problems);
    }

    [Fact]
    public void The_coupled_name_check_fails_for_none_several_and_the_baseline()
    {
        string[] ids = ["20260101000000_Base", "20260102000000_Twice", "20260103000000_Twice", "20260104000000_Once"];

        Assert.Empty(CoupledNameProblems(ids, ["Once"], "20260101000000_Base"));
        Assert.Contains("resolves to no migration", Assert.Single(CoupledNameProblems(ids, ["Gone"], ids[0])));
        Assert.Contains("resolves to 2 migrations", Assert.Single(CoupledNameProblems(ids, ["Twice"], ids[0])));
        Assert.Contains("resolves to the baseline", Assert.Single(CoupledNameProblems(ids, ["Base"], ids[0])));
    }

    private static List<string> CoupledNameProblems(IEnumerable<string> ids, IEnumerable<string> names, string baselineId)
    {
        var problems = new List<string>();
        foreach (var name in names)
        {
            var matches = ids.Where(id => CoupledMigrationNames.Matches(id, name)).ToList();
            if (matches.Count == 0)
            {
                problems.Add($"'{name}' resolves to no migration");
            }
            else if (matches.Count > 1)
            {
                problems.Add($"'{name}' resolves to {matches.Count} migrations");
            }
            else if (matches[0] == baselineId)
            {
                problems.Add($"'{name}' resolves to the baseline, which names the whole schema after a rebaseline");
            }
        }

        return problems;
    }

    // AC 19

    [Fact]
    public void No_migration_name_is_written_as_a_literal_outside_the_registry()
    {
        var names = MigrationIds().Select(id => id[(id.IndexOf('_') + 1)..]).ToHashSet(StringComparer.Ordinal);
        var offenders = new List<string>();

        foreach (var file in RepositoryFiles.SourceFiles("Odyssey.MigrationService").Concat(RepositoryFiles.SourceFiles("Odyssey.Core")))
        {
            foreach (Match literal in StringLiteral().Matches(File.ReadAllText(file)))
            {
                var value = literal.Groups[1].Value;
                if (names.Contains(value) || (value.StartsWith('_') && names.Contains(value[1..])))
                {
                    offenders.Add($"{Path.GetRelativePath(RepositoryFiles.Root, file)}: \"{value}\"");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Migration names must be read from CoupledMigrationNames, not written as literals:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void The_literal_lint_recognises_a_migration_name()
    {
        Assert.Equal("UnifyLocationsExpand", StringLiteral().Match("var x = \"UnifyLocationsExpand\";").Groups[1].Value);
    }

    [GeneratedRegex("\"(_?[A-Z][A-Za-z0-9]+)\"")]
    private static partial Regex StringLiteral();

    // AC 14

    [Fact]
    public void The_seeded_table_classification_covers_exactly_the_tables_the_model_seeds()
    {
        using var context = UndialledContext();
        var seeded = context.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model.GetEntityTypes()
            .Where(entity => entity.GetSeedData().Any())
            .Select(entity => entity.GetTableName()!)
            .Order(StringComparer.Ordinal);

        Assert.Equal(seeded, SeededTables.All.Select(t => t.Table).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Only_roles_print_rows_currencies_print_nothing_and_every_entry_has_a_reason()
    {
        Assert.Equal(["AspNetRoles"], SeededTables.All.Where(t => t.Class == SeededTableClass.Rows).Select(t => t.Table));
        Assert.Equal(["Currencies"], SeededTables.All.Where(t => t.Class == SeededTableClass.Excluded).Select(t => t.Table));
        Assert.All(SeededTables.All, table => Assert.False(string.IsNullOrWhiteSpace(table.Reason), table.Table));
    }

    // AC 24 (the shipped half)

    [Fact]
    public void The_fingerprint_reads_rows_in_exactly_one_place()
    {
        var directory = Path.Combine(RepositoryFiles.Root, "Odyssey.MigrationService", "Fingerprint");
        var rowReads = new List<string>();

        foreach (var file in Directory.GetFiles(directory, "*.cs"))
        {
            foreach (Match from in FromClause().Matches(File.ReadAllText(file)))
            {
                if (!from.Value.Contains("information_schema", StringComparison.OrdinalIgnoreCase))
                {
                    rowReads.Add($"{Path.GetFileName(file)}: {from.Value}");
                }
            }
        }

        var only = Assert.Single(rowReads);
        Assert.Contains("SchemaFingerprint.cs: FROM {SchemaCapture.Quote(seeded.Table)}", only);
    }

    [GeneratedRegex(@"FROM\s+\S+")]
    private static partial Regex FromClause();

    // The refusal messages (§9)

    [Fact]
    public void A_history_older_than_the_baseline_with_no_bridge_says_to_recreate()
    {
        var error = new MigrationBaselineException(new MigrationBaseline("20270101000000_Base", null), "20261231000000_Old");

        Assert.Equal(MigrationBaselineGap.BelowBaseline, error.Gap);
        Assert.Contains("'20270101000000_Base'", error.Message);
        Assert.Contains("'20261231000000_Old'", error.Message);
        Assert.Contains("must be recreated", error.Message);
        Assert.Contains("docs/migration-rebaseline.md", error.Message);
    }

    [Fact]
    public void A_history_older_than_the_baseline_with_a_bridge_names_the_release_to_upgrade_through()
    {
        var error = new MigrationBaselineException(new MigrationBaseline("20270101000000_Base", "v0.60.0"), "20261231000000_Old");

        Assert.Contains("Upgrade to v0.60.0 first", error.Message);
        Assert.Equal("v0.60.0", error.BridgeRelease);
    }

    [Fact]
    public void A_history_newer_than_the_baseline_is_a_foreign_set()
    {
        var error = new MigrationBaselineException(new MigrationBaseline("20270101000000_Base", "v0.60.0"), "20270202000000_Other");

        Assert.Equal(MigrationBaselineGap.ForeignSet, error.Gap);
        Assert.Contains("different migration set", error.Message);
        Assert.DoesNotContain("Upgrade to", error.Message);
    }

    [Fact]
    public void A_drift_with_superseded_ids_names_that_cause_and_lists_at_most_five()
    {
        var drift = new MigrationDrift("20270101000000_X", "table 'Accounts'");
        string[] ids = ["a1", "a2", "a3", "a4", "a5", "a6", "a7"];

        var error = new MigrationDriftException(drift, ids);

        Assert.Contains("superseded set", error.Message);
        Assert.Contains("'a5'", error.Message);
        Assert.DoesNotContain("'a6'", error.Message);
        Assert.Contains("and 2 more", error.Message);
        Assert.DoesNotContain("interrupted", error.Message);
    }

    [Fact]
    public void A_drift_without_superseded_ids_names_both_causes()
    {
        var error = new MigrationDriftException(new MigrationDrift("20270101000000_X", "table 'Accounts'"));

        Assert.Contains("interrupted", error.Message);
        Assert.Contains("docs/migration-history-drift.md", error.Message);
    }

    // Fingerprint helpers (AC 12)

    [Fact]
    public void Normalisation_strips_the_auto_increment_option_and_nothing_inside_a_definition()
    {
        const string create = "CREATE TABLE `T` (\r\n  `A` int NOT NULL AUTO_INCREMENT,\r\n" +
            "  CONSTRAINT `CK` CHECK (`B` <> 'two  spaces')\r\n) ENGINE=InnoDB AUTO_INCREMENT=42 DEFAULT CHARSET=utf8mb4";

        var normalized = SchemaFingerprint.NormalizeCreateTable(create);

        Assert.Contains("'two  spaces'", normalized);
        Assert.Contains("`A` int NOT NULL AUTO_INCREMENT,", normalized);
        Assert.DoesNotContain("AUTO_INCREMENT=42", normalized);
        Assert.Contains("ENGINE=InnoDB DEFAULT CHARSET=utf8mb4", normalized);
        Assert.DoesNotContain("\r", normalized);
    }

    [Fact]
    public void A_value_is_always_one_field_and_null_is_distinct_from_its_spelling()
    {
        Assert.Equal(@"a\tb\nc\\d", SchemaFingerprint.FormatValue("a\tb\nc\\d"));
        Assert.Equal(@"\N", SchemaFingerprint.FormatValue(DBNull.Value));
        Assert.Equal(@"\\N", SchemaFingerprint.FormatValue(@"\N"));
        Assert.Equal("1.5", SchemaFingerprint.FormatValue(1.5m));
        Assert.Equal("0x0A0B", SchemaFingerprint.FormatValue(new byte[] { 10, 11 }));
    }

    [Fact]
    public void The_operator_sections_are_the_fingerprint_without_its_parity_section()
    {
        const string full = "# h\n[schema]\nx\n[seed-rows]\n[seed-keys]\nk\n[parity-rows]\ntable `T`\nrow\n";

        Assert.Equal("# h\n[schema]\nx\n[seed-rows]\n[seed-keys]\nk\n", SchemaFingerprint.OperatorSectionsOf(full));
        Assert.Equal("[schema]\nx\n[seed-rows]\n[seed-keys]\nk\n", SchemaFingerprint.WithoutHeader(SchemaFingerprint.OperatorSectionsOf(full)));
    }

    // AC 15 (the switch), AC 25, AC 16 (the failure half)

    [Fact]
    public void Only_the_exact_switch_selects_the_fingerprint_mode()
    {
        Assert.True(SchemaFingerprintCommand.IsRequested(["--schema-fingerprint"]));
        Assert.False(SchemaFingerprintCommand.IsRequested(["--schema-fingerprints"]));
        Assert.False(SchemaFingerprintCommand.IsRequested([]));
    }

    [Fact]
    public async Task The_fingerprint_mode_refuses_an_in_memory_database()
    {
        var configuration = Configuration(new() { ["UseInMemoryDatabase"] = "true" });
        var (output, error) = (new StringWriter(), new StringWriter());

        var exit = await SchemaFingerprintCommand.RunAsync(configuration, output, error);

        Assert.Equal(SchemaFingerprintCommand.Refused, exit);
        Assert.Equal("", output.ToString());
        Assert.Contains("UseInMemoryDatabase", error.ToString());
    }

    [Fact]
    public async Task An_unreachable_server_prints_a_fixed_line_and_the_exception_type_only()
    {
        var configuration = Configuration(new()
        {
            ["ConnectionStrings:OdysseyConnection"] =
                "Server=odyssey-unreachable-host.invalid;Port=3399;Database=secretdb;User=secretuser;Password=secretpass;Connection Timeout=2",
        });
        var (output, error) = (new StringWriter(), new StringWriter());

        var exit = await SchemaFingerprintCommand.RunAsync(configuration, output, error);

        Assert.Equal(SchemaFingerprintCommand.Failed, exit);
        Assert.Equal("", output.ToString());
        var line = error.ToString();
        Assert.Matches(@"^Schema fingerprint failed: the database could not be read \(\w+\)\.\r?\n$", line);
        foreach (var secret in new[] { "odyssey-unreachable-host", "3399", "secretdb", "secretuser", "secretpass" })
        {
            Assert.DoesNotContain(secret, line);
        }
    }

    [Fact]
    public async Task A_missing_connection_string_is_a_failure_not_a_crash()
    {
        var (output, error) = (new StringWriter(), new StringWriter());

        var exit = await SchemaFingerprintCommand.RunAsync(Configuration([]), output, error);

        Assert.Equal(SchemaFingerprintCommand.Failed, exit);
        Assert.StartsWith("Schema fingerprint failed:", error.ToString());
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    /// <summary>
    /// The switch is handled in <c>Program.cs</c> BEFORE the host is built: the real executable, asked for a
    /// fingerprint of an in-memory database, refuses with the mode's own exit code and writes no host log line —
    /// a host that had started would run the worker, migrate and log instead.
    /// </summary>
    [Fact]
    public async Task The_executable_handles_the_switch_before_building_the_host()
    {
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Odyssey.MigrationService.dll"));
        start.ArgumentList.Add(SchemaFingerprintCommand.Switch);
        start.Environment["UseInMemoryDatabase"] = "true";

        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await process.WaitForExitAsync(timeout.Token);

        Assert.Equal(SchemaFingerprintCommand.Refused, process.ExitCode);
        Assert.Equal("", await output);
        Assert.Contains("Schema fingerprint refused", await error);
    }

    [Fact]
    public void The_configuration_reads_the_command_line_and_drops_the_switch()
    {
        var configuration = SchemaFingerprintCommand.BuildConfiguration(
            [SchemaFingerprintCommand.Switch, "--ConnectionStrings:OdysseyConnection=Server=from-args"]);

        Assert.Equal("Server=from-args", configuration.GetConnectionString("OdysseyConnection"));
        Assert.DoesNotContain(configuration.AsEnumerable(), pair => pair.Key.Contains("schema-fingerprint", StringComparison.Ordinal));
    }

    // The guard's non-relational short-circuit

    [Fact]
    public async Task The_baseline_guard_does_nothing_on_a_non_relational_provider()
    {
        await using var context = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
            .UseInMemoryDatabase($"baseline-guard-{Guid.NewGuid()}").Options);

        await MigrationRunner.GuardAgainstBaselineGapAsync(context, new MigrationBaseline("x", null), CancellationToken.None);
    }
}
