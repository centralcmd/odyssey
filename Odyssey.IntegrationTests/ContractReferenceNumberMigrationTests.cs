// EF1002 flags interpolation into ExecuteSqlRawAsync. The only value interpolated below is the
// database name this test declares itself — there is no external input.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Core;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The relational half of issue #181: the search leg's <c>LIKE</c> escaping holds on the real engine
/// (AC 13), and <c>Contracts.ReferenceNumber</c> carries no index and no constraint.
/// </summary>
/// <remarks>
/// None of it is observable on the fast tiers. The EF InMemory provider has no schema, so "the column
/// is nullable and unindexed" is unrunnable there; and its <c>LIKE</c> emulation does not honour
/// MariaDB's default backslash escape, so a literal <c>%</c> in a search term can only be proven
/// literal against MariaDB itself.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class ContractReferenceNumberMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_contract_reference_number";

    /// <summary>
    /// A non-BMP value survives the <c>utf8mb4</c> column byte-identically, and a literal <c>%</c> in
    /// the search term matches literally — it cannot widen the new <c>OR</c> leg into a wildcard.
    /// </summary>
    [SkippableFact]
    public async Task Search_escapes_like_metacharacters_and_non_bmp_values_round_trip()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);

        try
        {
            Guid literal;
            await using (var context = NewContext())
            {
                var service = NewService(context);
                await service.Create(new NewContract { Name = "Storage", ReferenceNumber = "1001" }, null);
                literal = (await service.Create(new NewContract { Name = "Rebate", ReferenceNumber = "100%-rebate" }, null)).ContractId;
                await service.Create(new NewContract { Name = "Deed", ReferenceNumber = "REF-\U00020BB7-1" }, null);
            }

            await using (var context = NewContext())
            {
                var service = NewService(context);

                var percent = await service.ListAsync(new ContractsQueryParams { Search = "100%" });
                Assert.Equal(literal, Assert.Single(percent.Items).ContractId);

                var nonBmp = Assert.Single((await service.ListAsync(new ContractsQueryParams { Search = "\U00020BB7" })).Items);
                Assert.Equal("REF-\U00020BB7-1", nonBmp.ReferenceNumber);

                // Nullable varchar(64), and deliberately nothing else: no index (neither a
                // leading-wildcard LIKE nor an in-memory sort can use one) and no unique constraint
                // (the number is the counterparty's, so duplicates are legitimate — §7 #9).
                Assert.Equal(1, await MigrationSeam.CountAsync(context, """
                    SELECT COUNT(*) FROM information_schema.COLUMNS
                    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Contracts'
                      AND COLUMN_NAME = 'ReferenceNumber' AND IS_NULLABLE = 'YES'
                      AND DATA_TYPE = 'varchar' AND CHARACTER_MAXIMUM_LENGTH = 64
                      -- MariaDB reports "no default" on a nullable column as the literal string NULL.
                      AND (COLUMN_DEFAULT IS NULL OR COLUMN_DEFAULT = 'NULL')
                    """));
                Assert.Equal(0, await MigrationSeam.CountAsync(context, """
                    SELECT COUNT(*) FROM information_schema.STATISTICS
                    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Contracts' AND COLUMN_NAME = 'ReferenceNumber'
                    """));
                Assert.Equal(0, await MigrationSeam.CountAsync(context, """
                    SELECT COUNT(*) FROM information_schema.KEY_COLUMN_USAGE
                    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Contracts' AND COLUMN_NAME = 'ReferenceNumber'
                    """));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ContractService NewService(OdysseyContext context) =>
        new(context, new ContactLookup(context), TimeProvider.System,
            NullLogger<ContractService>.Instance);

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
