// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid or a
// literal this test wrote itself — there is no external input.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Odyssey.Context;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// What the retirement of the property and vehicle account types (issue #218) leaves in force, against real MariaDB:
/// <c>CK_Accounts_AccountTypeNotRetired</c> (AC 9). The EF InMemory provider enforces no <c>CHECK</c>, so this is the
/// only tier that sees it.
/// </summary>
/// <remarks>
/// The migration itself, its drift replay and the migrations job's retirement report were folded into the November
/// 2026 rebaseline; the constraint is all that remains.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class AccountTypeNotRetiredCheckTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_retire_account_types";

    private const string Check = "CK_Accounts_AccountTypeNotRetired";

    /// <summary>AC 9 — the CHECK refuses a retired ordinal written past the API.</summary>
    [SkippableFact]
    public async Task The_check_refuses_a_retired_account_type()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);

        try
        {
            await using var context = NewContext();
            foreach (var retired in new[] { 6, 7 })
            {
                var error = await Assert.ThrowsAnyAsync<MySqlException>(() => context.Database.ExecuteSqlRawAsync(
                    "INSERT INTO `Accounts` (`AccountId`, `Name`, `Description`, `Opened`, `AccountType`, `CurrencyCode`) " +
                    $"VALUES ('{Guid.NewGuid()}', 'Sneaky', '', '2026-01-01', {retired}, 'USD')"));
                Assert.Contains(Check, error.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    // ── Infrastructure ────────────────────────────────────────────────────────

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

#pragma warning restore EF1002
