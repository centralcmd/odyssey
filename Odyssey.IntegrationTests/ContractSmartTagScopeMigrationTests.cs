// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid or a
// literal this test wrote itself — there is no external input — and the rows are deliberately written outside
// the service, so the CHECK is what refuses them.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Xunit;
using ContextContractType = Odyssey.Context.ContractType;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The relational half of issue #308 (AC 15): <c>CK_Contracts_SmartTagScopeOrder</c> refuses an inverted scope
/// written past the service, and a same-day scope is a valid one-day window.
/// </summary>
/// <remarks>
/// Unobservable on the fast tiers: the EF InMemory provider enforces no CHECK. The <c>AddContractSmartTagScope</c>
/// backfill's own tests were retired once production had applied it (see the README).
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class ContractSmartTagScopeMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_contract_smart_tag_scope";

    [SkippableFact]
    public async Task The_check_refuses_an_inverted_scope_and_admits_a_one_day_window()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);

        var contract = Guid.NewGuid();

        try
        {
            await using var context = NewContext();
            await SeedAsync(context, contract, "'2021-03-01 09:30:00'", "'2026-08-31 18:00:00'", "NULL");

            // The CHECK is the backstop for a write that bypasses the service's 422.
            var refused = await Assert.ThrowsAsync<MySqlConnector.MySqlException>(() => context.Database.ExecuteSqlRawAsync($"""
                UPDATE `Contracts` SET `SmartTagScopeStart` = '2027-07-01', `SmartTagScopeEnd` = '2027-06-30'
                WHERE `ContractId` = '{contract}'
                """));
            Assert.Contains("CK_Contracts_SmartTagScopeOrder", refused.Message, StringComparison.Ordinal);
            // Same day is a valid one-day window.
            await context.Database.ExecuteSqlRawAsync($"""
                UPDATE `Contracts` SET `SmartTagScopeStart` = '2027-06-30', `SmartTagScopeEnd` = '2027-06-30'
                WHERE `ContractId` = '{contract}'
                """);
        }
        finally
        {
            await DropAsync();
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

    private async Task DropAsync()
    {
        await using var server = fixture.CreateOdysseyContext();
        await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
    }
}
