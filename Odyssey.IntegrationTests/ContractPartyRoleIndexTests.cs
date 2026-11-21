// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid, an
// ordinal or a formatted DateTime this test generated itself — there is no external input — and the
// rows are deliberately written outside the service, which is the whole point of the test.
#pragma warning disable EF1002

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Odyssey.Context;
using Odyssey.Dtos;
using Xunit;
using ContextAccountType = Odyssey.Context.AccountType;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The relational half of issue #121: the two unique indexes on <c>ContractParties</c> (AC 15).
/// </summary>
/// <remarks>
/// Not observable on the fast tiers. The EF InMemory provider enforces no indexes at all, so the
/// service's <c>EnsureNotDuplicateParty</c> pre-check is the only implementation those tiers see — and
/// the pre-check is a check-then-act with no transaction, which is precisely why the index exists.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class ContractPartyRoleIndexTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_contract_party_role";

    /// <summary>
    /// AC 15 — both unique indexes exist and reject a duplicate <i>(contract, target, role)</i> insert
    /// that bypasses the service pre-check entirely, while the SAME target in a DIFFERENT role is
    /// accepted. The index, not the racy pre-check, is what makes the rule true.
    /// </summary>
    /// <remarks>
    /// MariaDB treats <c>NULL</c> as distinct in a unique index, so the account-side index does not
    /// constrain contact parties and vice versa — which is what lets one table carry two uniqueness
    /// rules without a discriminator column. Both sides are asserted, because covering one would let a
    /// dropped index pass on the strength of the other.
    /// </remarks>
    [SkippableFact]
    public async Task The_unique_indexes_reject_a_duplicate_role_on_either_target_column()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);

        var contract = Guid.NewGuid();
        var account = Guid.NewGuid();
        var contact = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await SeedTargetsAsync(context, account, contact);
                await SeedContractAsync(context, contract);
            }

            await using (var context = NewContext())
            {
                await AddPartyAsync(context, Guid.NewGuid(), contract, 2, accountId: account);
                await AddPartyAsync(context, Guid.NewGuid(), contract, 2, contactId: contact);

                // Same target, DIFFERENT role — a record named twice in two genuinely different
                // capacities is one contract with two parties.
                await AddPartyAsync(context, Guid.NewGuid(), contract, 4, accountId: account);
                await AddPartyAsync(context, Guid.NewGuid(), contract, 4, contactId: contact);

                await Assert.ThrowsAsync<MySqlException>(() =>
                    AddPartyAsync(context, Guid.NewGuid(), contract, 2, accountId: account));
                await Assert.ThrowsAsync<MySqlException>(() =>
                    AddPartyAsync(context, Guid.NewGuid(), contract, 2, contactId: contact));

                Assert.Equal(4, await context.ContractParties.AsNoTracking()
                    .CountAsync(p => p.ContractId == contract));
            }

            // Both indexes are declared UNIQUE and named as scaffolded — a non-unique index of the
            // same name would silently satisfy every assertion above on an empty-enough table.
            await using (var context = NewContext())
            {
                Assert.True(await IsUniqueIndexAsync(context, "IX_ContractParties_ContractId_AccountId_Role"));
                Assert.True(await IsUniqueIndexAsync(context, "IX_ContractParties_ContractId_ContactId_Role"));

                // The three single-column indexes are all KEPT: the composites lead with ContractId,
                // so a lookup by target alone (the contact-deletion blockers) still needs its own.
                Assert.True(await IndexExistsAsync(context, "IX_ContractParties_AccountId"));
                Assert.True(await IndexExistsAsync(context, "IX_ContractParties_ContactId"));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    // ── Schema introspection ───────────────────────────────────────────────────

    private static async Task<bool> IndexExistsAsync(OdysseyContext context, string index) =>
        await MigrationSeam.CountAsync(context, $"""
            SELECT COUNT(*) FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'ContractParties' AND INDEX_NAME = '{index}'
            """) > 0;

    /// <summary><c>NON_UNIQUE = 0</c> is what makes an index the arbiter rather than a lookup aid.</summary>
    private static async Task<bool> IsUniqueIndexAsync(OdysseyContext context, string index) =>
        await MigrationSeam.CountAsync(context, $"""
            SELECT COUNT(*) FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'ContractParties'
              AND INDEX_NAME = '{index}' AND NON_UNIQUE = 0
            """) > 0;

    // ── Seeding ────────────────────────────────────────────────────────────────

    private static async Task SeedTargetsAsync(OdysseyContext context, Guid? accountId, Guid? contactId)
    {
        if (contactId is { } contact)
        {
            context.Contacts.Add(new Contact
            {
                ContactId = contact,
                ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
                NormalizedName = "COUNTERPARTY",
                Type = ContactType.Organization,
                OrganizationDetails = new() { LegalName = "Counterparty" },
            });
            await context.SaveChangesAsync();
        }

        if (accountId is { } account)
        {
            context.Accounts.Add(new Account
            {
                AccountId = account,
                Name = "Everyday checking",
                Description = "Contract party target",
                Opened = DateTime.UtcNow,
                AccountType = ContextAccountType.CheckingAccount,
                CurrencyCode = "USD",
            });
            await context.SaveChangesAsync();
        }
    }

    private static Task SeedContractAsync(OdysseyContext context, Guid contractId)
    {
        var createdAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
        return context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `Contracts` (`ContractId`, `Name`, `Type`, `CreatedAtUtc`)
            VALUES ('{contractId}', 'Mortgage mandate', 3, '{createdAt}');
            """);
    }

    /// <summary>
    /// Inserts one party through raw SQL, bypassing the service entirely — which is the point: the
    /// pre-check is what the fast tiers cover, and this has to reach the index behind it.
    /// </summary>
    private static Task AddPartyAsync(
        OdysseyContext context, Guid partyId, Guid contractId, int role,
        Guid? accountId = null, Guid? contactId = null)
    {
        static string Value(Guid? id) => id is null ? "NULL" : $"'{id}'";

        return context.Database.ExecuteSqlRawAsync(
            "INSERT INTO `ContractParties` (`ContractPartyId`, `ContractId`, `AccountId`, `ContactId`, `Role`) "
            + $"VALUES ('{partyId}', '{contractId}', {Value(accountId)}, {Value(contactId)}, {role});");
    }

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
