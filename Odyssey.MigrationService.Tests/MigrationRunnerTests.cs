using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Xunit;

namespace Odyssey.MigrationService.Tests;

/// <summary>
/// The two decisions in <see cref="MigrationRunner"/> that are neither the detector's arithmetic nor
/// the relational round trip: the non-relational short-circuit, and the cancellation token deliberately
/// withheld from <c>MigrateAsync</c> (issue #468).
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

        var call = Regex.Match(source, @"Database\.MigrateAsync\(([^)]*)\)");

        Assert.True(call.Success, "Could not find the Database.MigrateAsync call in MigrationRunner.cs.");
        Assert.Equal("CancellationToken.None", call.Groups[1].Value.Trim());
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
