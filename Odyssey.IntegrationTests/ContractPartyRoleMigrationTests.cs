// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid or a
// formatted DateTime this test generated itself — there is no external input — and the pre-migration
// rows are deliberately written outside the service, which is the whole point of the seam.
#pragma warning disable EF1002

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Odyssey.Context;
using Xunit;
using ContextAccountType = Odyssey.Context.AccountType;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The relational half of issue #121: the two unique indexes, the backfill, and the pre-flight
/// duplicate sweep in <c>AddContractPartyRoleAndTerm</c> (AC 15–17).
/// </summary>
/// <remarks>
/// None of this is observable on the fast tiers. The EF InMemory provider enforces no indexes at all,
/// so the service's <c>EnsureNotDuplicateParty</c> pre-check is the only implementation those tiers
/// see — and the pre-check is a check-then-act with no transaction, which is precisely why the index
/// exists. The migration seam (<see cref="MigrationSeam"/>) is what lets the "before" state be built;
/// an ordinary fixture migrates straight to head, by which time the sweep has already run against an
/// empty table.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class ContractPartyRoleMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_contract_party_role";

    /// <summary>The migration immediately before the one under test.</summary>
    private const string Baseline = "_RenameAccountTermsToTermsAndReworkInterval";

    private const string Subject = "_AddContractPartyRoleAndTerm";

    /// <summary>
    /// AC 16 — the migration applies cleanly over pre-existing party rows, every one reads back with
    /// the default term, and the CASCADE behaviour on all three relationship columns is unchanged.
    /// </summary>
    /// <remarks>
    /// Both dates null is not a gap but the <b>default term</b> — the contract's own extent — so an
    /// upgrade leaves every existing party following its contract exactly as it did.
    ///
    /// <para>
    /// The role reads back as <c>Other</c>, not <c>Unspecified</c>: this migrates to HEAD, and issue
    /// #157 retired that member and remapped every row carrying it. The backfill itself is unchanged
    /// — <c>AddContractPartyRoleAndTerm</c> still writes <c>0</c> — and what this now asserts is the
    /// two migrations composing, which is what an upgrade from this baseline actually does.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task Existing_rows_backfill_and_keep_their_cascades()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var contract = Guid.NewGuid();
        var account = Guid.NewGuid();
        var contact = Guid.NewGuid();
        var accountParty = Guid.NewGuid();
        var contactParty = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await SeedTargetsAsync(context, account, contact);
                await SeedContractAsync(context, contract);
                await AddPartyAsync(context, accountParty, contract, accountId: account);
                await AddPartyAsync(context, contactParty, contract, contactId: contact);
            }

            await using (var context = NewContext())
            {
                await context.Database.MigrateAsync();

                var parties = await context.ContractParties.AsNoTracking()
                    .Where(p => p.ContractId == contract)
                    .ToListAsync();

                Assert.Equal(2, parties.Count);
                Assert.All(parties, party =>
                {
                    Assert.Equal(ContractPartyRole.Other, party.Role);
                    Assert.Null(party.FromDate);
                    Assert.Null(party.ToDate);
                });

                // The rows keep their own ids — a party id is a stable handle the API addresses, and
                // a migration that recreated them would break every link a client holds.
                Assert.Contains(parties, p => p.ContractPartyId == accountParty);
                Assert.Contains(parties, p => p.ContractPartyId == contactParty);
            }

            await using (var context = NewContext())
            {
                // Deleting the linked contact removes its party row and leaves the contract standing:
                // a party row IS its link, so it dies with either end.
                await context.Database.ExecuteSqlRawAsync($"DELETE FROM `Contacts` WHERE `ContactId` = '{contact}';");

                Assert.Null(await context.ContractParties.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.ContractPartyId == contactParty));
                Assert.NotNull(await context.Contracts.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.ContractId == contract));
                Assert.NotNull(await context.ContractParties.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.ContractPartyId == accountParty));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

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
        await RecreateAsync();

        var contract = Guid.NewGuid();
        var account = Guid.NewGuid();
        var contact = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await context.Database.MigrateAsync();
                await SeedTargetsAsync(context, account, contact);
                await SeedContractAsync(context, contract);
            }

            await using (var context = NewContext())
            {
                await AddPartyAsync(context, Guid.NewGuid(), contract, accountId: account, role: 2);
                await AddPartyAsync(context, Guid.NewGuid(), contract, contactId: contact, role: 2);

                // Same target, DIFFERENT role — a record named twice in two genuinely different
                // capacities is one contract with two parties.
                await AddPartyAsync(context, Guid.NewGuid(), contract, accountId: account, role: 4);
                await AddPartyAsync(context, Guid.NewGuid(), contract, contactId: contact, role: 4);

                await Assert.ThrowsAsync<MySqlException>(() =>
                    AddPartyAsync(context, Guid.NewGuid(), contract, accountId: account, role: 2));
                await Assert.ThrowsAsync<MySqlException>(() =>
                    AddPartyAsync(context, Guid.NewGuid(), contract, contactId: contact, role: 2));

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

    /// <summary>
    /// AC 17 — the pre-flight sweep refuses a database holding a duplicate <i>(contract, target)</i>
    /// pair, naming that contract and target, AND none of the three columns exists afterwards.
    /// </summary>
    /// <remarks>
    /// The column assertion is the observable form of "the sweep ran before any DDL", and it is the
    /// half that matters: MariaDB commits DDL implicitly, so "nothing was applied" only holds if the
    /// sweep precedes the <c>AddColumn</c> calls too, not merely the <c>CreateIndex</c> ones. It
    /// reports rather than repairs — which of two duplicate links to drop is the operator's call.
    /// </remarks>
    [SkippableFact]
    public async Task A_pre_existing_duplicate_fails_the_migration_before_any_DDL()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var contract = Guid.NewGuid();
        var contact = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await SeedTargetsAsync(context, accountId: null, contactId: contact);
                await SeedContractAsync(context, contract);

                // The duplicate the old check-then-act pre-check could never have prevented: a race, a
                // hand edit or a restore. Nothing in the pre-#121 schema would ever have caught it.
                await AddPartyAsync(context, Guid.NewGuid(), contract, contactId: contact);
                await AddPartyAsync(context, Guid.NewGuid(), contract, contactId: contact);
            }

            await using (var context = NewContext())
            {
                var failure = await Assert.ThrowsAnyAsync<Exception>(() => context.Database.MigrateAsync());

                var text = Flatten(failure);
                Assert.Contains("Duplicate contract party link", text, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(contract.ToString(), text, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(contact.ToString(), text, StringComparison.OrdinalIgnoreCase);
            }

            await using (var context = NewContext())
            {
                Assert.False(await MigrationSeam.HasRunAsync(context, Subject));

                // Nothing applied: not the indexes, and not the columns either.
                Assert.False(await ColumnExistsAsync(context, "Role"));
                Assert.False(await ColumnExistsAsync(context, "FromDate"));
                Assert.False(await ColumnExistsAsync(context, "ToDate"));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    private static string Flatten(Exception exception)
    {
        var text = new System.Text.StringBuilder();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            text.AppendLine(current.Message);
        }

        return text.ToString();
    }

    // ── Schema introspection ───────────────────────────────────────────────────

    private static async Task<bool> ColumnExistsAsync(OdysseyContext context, string column) =>
        await MigrationSeam.CountAsync(context, $"""
            SELECT COUNT(*) FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'ContractParties' AND COLUMN_NAME = '{column}'
            """) > 0;

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
            await BaselineContacts.AddOrganizationAsync(context, contact, "Counterparty");
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
    /// pre-check is what the fast tiers cover, and this has to reach the index behind it. The
    /// <c>Role</c> column is written only when the caller names it, so the same helper serves the
    /// pre-migration schema (where it does not exist).
    /// </summary>
    private static Task AddPartyAsync(
        OdysseyContext context, Guid partyId, Guid contractId,
        Guid? accountId = null, Guid? contactId = null, int? role = null)
    {
        static string Value(Guid? id) => id is null ? "NULL" : $"'{id}'";

        var columns = "`ContractPartyId`, `ContractId`, `AccountId`, `ContactId`"
            + (role is null ? "" : ", `Role`");
        var values = $"'{partyId}', '{contractId}', {Value(accountId)}, {Value(contactId)}"
            + (role is null ? "" : $", {role.Value}");

        return context.Database.ExecuteSqlRawAsync($"INSERT INTO `ContractParties` ({columns}) VALUES ({values});");
    }

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
