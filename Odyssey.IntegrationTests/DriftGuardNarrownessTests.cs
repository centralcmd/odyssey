// EF1002 flags interpolation into ExecuteSqlRawAsync. The interpolated values are a table name and the
// database name, both written by this test itself — there is no external input.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.MigrationService;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The <c>MigrationRunner</c> drift guard stays narrow: an ordinary first run and an ordinary restart both pass it.
/// </summary>
/// <remarks>
/// This class once tested the guard against the <c>AccountTerms</c> → <c>Terms</c> rename (issue #120). That migration
/// was folded into the November 2026 rebaseline, so the rename-specific tests went with it.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class DriftGuardNarrownessTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_drift_guard_narrowness";

    /// <summary>
    /// AC 47 — the guard stays NARROW. A clean database with every migration pending, and a fully
    /// migrated one with none, must both pass: the extension must not make an ordinary first run or
    /// an ordinary upgrade look like drift.
    /// </summary>
    [SkippableFact]
    public async Task A_clean_database_and_a_fully_migrated_one_both_pass_the_pre_flight_check()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            await using (var context = NewContext())
            {
                // Everything pending, nothing present — an ordinary first run.
                Assert.NotEmpty(await context.Database.GetPendingMigrationsAsync());
                await MigrationRunner.GuardAgainstDriftAsync(context, CancellationToken.None);
                await MigrationRunner.MigrateAsync(context, CancellationToken.None);
                Assert.Empty(await context.Database.GetPendingMigrationsAsync());
            }

            await using (var context = NewContext())
            {
                // Nothing pending, everything present — an ordinary restart.
                Assert.Empty(await context.Database.GetPendingMigrationsAsync());
                await MigrationRunner.GuardAgainstDriftAsync(context, CancellationToken.None);
                await MigrationRunner.MigrateAsync(context, CancellationToken.None);
                Assert.Equal(context.Database.GetMigrations(), await context.Database.GetAppliedMigrationsAsync());
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private OdysseyContext NewContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);

    private async Task RecreateAsync()
    {
        await DropAsync();
        await using var server = fixture.CreateOdysseyContext();
        await server.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
    }

    private async Task DropAsync()
    {
        await using var server = fixture.CreateOdysseyContext();
        await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
    }
}
