using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Xunit;

namespace Odyssey.MigrationService.Tests;

/// <summary>
/// The decisions in <see cref="MigrationRunner"/> that are neither the detector's arithmetic nor the
/// relational round trip: the non-relational short-circuit, the cancellation token deliberately withheld
/// from <c>MigrateAsync</c> (issue #468), and the issue #380 staging around the location conversion.
/// </summary>
public class MigrationRunnerTests
{
    /// <summary>
    /// Reached through <c>InternalsVisibleTo</c> because the branch is invisible from outside: every
    /// path out of <c>MigrateAsync</c> against a non-relational provider throws the same
    /// relational-provider error, whether the guard short-circuited or tried to query
    /// <c>information_schema</c> and failed. Calling the guard directly is the only way to tell the two
    /// apart — and the distinction matters, because the guard reaching for a connection here would
    /// replace EF's clear message with a confusing one.
    /// </summary>
    [Fact]
    public async Task TheDriftGuard_IsANoOp_AgainstANonRelationalProvider()
    {
        await using var context = InMemoryContext();

        await MigrationRunner.GuardAgainstDriftAsync(context, CancellationToken.None);
    }

    /// <summary>
    /// This is emphatically not support for the in-memory provider, and the comment on the
    /// short-circuit says so. Pinning it here keeps the two from drifting: a future reader who takes
    /// the short-circuit as "in-memory is handled" would be contradicted by this test.
    /// </summary>
    [Fact]
    public async Task MigrateAsync_StillRefusesANonRelationalProvider_AfterTheGuardShortCircuits()
    {
        await using var context = InMemoryContext();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => MigrationRunner.MigrateAsync(context, CancellationToken.None));
    }

    /// <summary>
    /// A source-lint, because the property is about which token reaches a call rather than about any
    /// observable behaviour: proving it at runtime would need a migration to be interrupted at a
    /// precise instant, which is the timing-dependent test this repository would rightly reject.
    ///
    /// <para>
    /// The rule it guards is counter-intuitive enough that <c>CLAUDE.md</c> spells it out — passing the
    /// caller's token here looks like an obvious tidy-up, and is the exact mistake that produces the
    /// drift the rest of this file exists to catch. Cancelling between two <c>CREATE TABLE</c>s leaves
    /// committed tables with no history row.
    /// </para>
    /// </summary>
    [Fact]
    public void MigrateAsync_IsCalledWithoutTheCallersCancellationToken()
    {
        var source = File.ReadAllText(RunnerSourcePath());

        // Issue #380 §11 (N3) staged the run around the location conversion: one call migrates to
        // UnifyLocationsExpand, one migrates the rest, and the non-relational short-circuit keeps its own.
        // Every one of them must withhold the caller's token — the last argument is the token.
        var calls = Regex.Matches(source, @"Database\.MigrateAsync\(([^)]*)\)");

        Assert.True(calls.Count >= 3, $"Expected the staged MigrateAsync calls in MigrationRunner.cs, found {calls.Count}.");
        Assert.All(calls, call =>
            Assert.Equal("CancellationToken.None", call.Groups[1].Value.Split(',')[^1].Trim()));
        Assert.Contains(calls, call => call.Groups[1].Value.Contains(',', StringComparison.Ordinal));
    }

    // ── The staging decision (senior-tester L3) ──────────────────────────────

    private const string Initial = "20260829095318_InitialCreate";
    private const string Before = "20261001000000_SomethingBefore";
    private const string Expand = "20261020000000_" + Odyssey.Core.Locations.Conversion.ILocationConversion.ExpandMigration;
    private const string Contract = "20261020000001_" + Odyssey.Core.Locations.Conversion.ILocationConversion.ContractMigration;
    private const string After = "20261101000000_SomethingAfter";

    [Fact]
    public void A_full_upgrade_migrates_through_Expand_then_converts()
    {
        var plan = MigrationRunner.PlanStages([Initial, Before, Expand, Contract, After], hasConversion: true);

        Assert.True(plan.RunConversion);
        Assert.Equal([Initial, Before, Expand], plan.ExpandStage);
    }

    [Fact]
    public void A_restart_after_Expand_converts_without_an_Expand_stage()
    {
        var plan = MigrationRunner.PlanStages([Contract, After], hasConversion: true);

        Assert.True(plan.RunConversion);
        Assert.Null(plan.ExpandStage);
    }

    [Fact]
    public void Once_the_contract_is_applied_the_conversion_never_runs_again()
    {
        string[][] cases = [[], [After], [Before, After]];

        Assert.All(cases, pending =>
            Assert.Equal(new MigrationRunner.StagePlan(null, false), MigrationRunner.PlanStages(pending, hasConversion: true)));
    }

    [Fact]
    public void Without_a_conversion_everything_is_one_stage()
    {
        Assert.Equal(
            new MigrationRunner.StagePlan(null, false),
            MigrationRunner.PlanStages([Initial, Expand, Contract, After], hasConversion: false));
    }

    [Fact]
    public void Only_a_suffix_match_names_a_stage_migration()
    {
        // A migration whose name merely contains the contract's is not the contract.
        var lookalike = "20261102000000_" + Odyssey.Core.Locations.Conversion.ILocationConversion.ContractMigration + "Followup";

        Assert.False(MigrationRunner.PlanStages([lookalike], hasConversion: true).RunConversion);
    }

    private static OdysseyContext InMemoryContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseInMemoryDatabase($"drift-guard-{Guid.NewGuid()}")
            .Options);

    private static string RunnerSourcePath()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "Odyssey.MigrationService", "MigrationRunner.cs");
            if (File.Exists(candidate) && File.Exists(Path.Combine(dir, "Odyssey.sln")))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        throw new InvalidOperationException(
            "Could not locate MigrationRunner.cs from " + AppContext.BaseDirectory);
    }
}
