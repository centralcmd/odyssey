// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid or a
// literal this test wrote itself — there is no external input — and the pre-migration rows are
// deliberately written outside the service, which is the whole point of the seam.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Context.Migrations;
using Xunit;
using ContextContractType = Odyssey.Context.ContractType;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The relational half of issue #308 (AC 15): <c>AddContractSmartTagScope</c> backfills every
/// pre-existing contract shape the demo seed does not produce — a term with a time of day, a one-off,
/// a one-off with stale term dates and an inverted legacy term — re-running the backfill changes
/// nothing, and the CHECK refuses an inverted scope written past the service.
/// </summary>
/// <remarks>
/// None of it is observable on the fast tiers: the EF InMemory provider runs no migration, executes no
/// raw SQL and enforces no CHECK.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class ContractSmartTagScopeMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_contract_smart_tag_scope";

    /// <summary>The migration immediately before the one under test.</summary>
    private const string Baseline = "_MakeCandidateReviewStatusConcurrencyToken";

    private const string Subject = "_AddContractSmartTagScope";

    [SkippableFact]
    public async Task The_backfill_copies_each_shape_and_is_idempotent_and_the_check_holds()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var term = Guid.NewGuid();
        var oneOff = Guid.NewGuid();
        var staleOneOff = Guid.NewGuid();
        var inverted = Guid.NewGuid();
        var open = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await SeedAsync(context, term, "'2021-03-01 09:30:00'", "'2026-08-31 18:00:00'", "NULL");
                await SeedAsync(context, oneOff, "NULL", "NULL", "'2025-05-01 00:00:00'");
                await SeedAsync(context, staleOneOff, "'2021-03-01 00:00:00'", "'2022-03-01 00:00:00'", "'2025-05-01 00:00:00'");
                await SeedAsync(context, inverted, "'2024-06-01 00:00:00'", "'2023-06-01 00:00:00'", "NULL");
                await SeedAsync(context, open, "NULL", "NULL", "NULL");
            }

            await using (var context = NewContext())
            {
                await context.Database.MigrateAsync();
                Assert.True(await MigrationSeam.HasRunAsync(context, Subject));

                await AssertScopesAsync(context);

                // Re-running the backfill derives every value from the term columns alone.
                await context.Database.ExecuteSqlRawAsync(AddContractSmartTagScope.BackfillSql);
                await AssertScopesAsync(context);

                // The CHECK is the backstop for a write that bypasses the service's 422.
                var refused = await Assert.ThrowsAsync<MySqlConnector.MySqlException>(() => context.Database.ExecuteSqlRawAsync($"""
                    UPDATE `Contracts` SET `SmartTagScopeStart` = '2027-07-01', `SmartTagScopeEnd` = '2027-06-30'
                    WHERE `ContractId` = '{term}'
                    """));
                Assert.Contains("CK_Contracts_SmartTagScopeOrder", refused.Message, StringComparison.Ordinal);
                // Same day is a valid one-day window.
                await context.Database.ExecuteSqlRawAsync($"""
                    UPDATE `Contracts` SET `SmartTagScopeStart` = '2027-06-30', `SmartTagScopeEnd` = '2027-06-30'
                    WHERE `ContractId` = '{term}'
                    """);
            }
        }
        finally
        {
            await DropAsync();
        }

        async Task AssertScopesAsync(OdysseyContext context)
        {
            context.ChangeTracker.Clear();
            var rows = await context.Contracts.AsNoTracking()
                .ToDictionaryAsync(c => c.ContractId, c => (c.SmartTagScopeStart, c.SmartTagScopeEnd));

            Assert.Equal((new DateTime(2021, 3, 1), new DateTime(2026, 8, 31)), rows[term]);
            Assert.Equal((null, null), rows[oneOff]);
            Assert.Equal((null, null), rows[staleOneOff]);
            Assert.Equal((new DateTime(2024, 6, 1), (DateTime?)null), rows[inverted]);
            Assert.Equal((null, null), rows[open]);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Task SeedAsync(OdysseyContext context, Guid id, string start, string end, string completion) =>
        context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `Contracts` (`ContractId`, `Name`, `Type`, `StartDate`, `EndDate`, `CompletionDate`, `CreatedAtUtc`)
            VALUES ('{id}', 'Pre-existing agreement', {(int)ContextContractType.Other}, {start}, {end}, {completion},
                    '2021-01-01 00:00:00');
            """);

    // ── Fixture plumbing ───────────────────────────────────────────────────────

    private OdysseyContext NewContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);

    private async Task RecreateAsync()
    {
        await DropAsync();
        await using var server = ServerContext();
        await server.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
    }

    private async Task DropAsync()
    {
        await using var server = ServerContext();
        await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
    }

    private OdysseyContext ServerContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);
}
