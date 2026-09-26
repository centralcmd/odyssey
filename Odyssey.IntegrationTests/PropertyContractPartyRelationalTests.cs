// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid or an
// int this test generated itself — there is no external input — and the raw writes are deliberate: they
// go around the services so the ENGINE's constraints are what is being tested.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Odyssey.Context;
using Odyssey.Core;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Xunit;
using ContextAccountType = Odyssey.Context.AccountType;
using ContextContractEventType = Odyssey.Context.ContractEventType;
using ContextContractPartyRole = Odyssey.Context.ContractPartyRole;
using ContextContractType = Odyssey.Context.ContractType;
using ContractPartyRole = Odyssey.Dtos.Finance.ContractPartyRole;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The real-engine half of issue #208: the migration over existing parties (AC 18), the widened
/// <c>CK_ContractParties_ExactlyOneTarget</c> and the new unique index (AC 17), the property delete on
/// MariaDB (AC 16), and the property Contracts section / count translating to SQL. None of this is
/// observable on EF InMemory, which runs no migrations and enforces no constraint at all.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class PropertyContractPartyRelationalTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_property_parties";
    private const string PreviousMigration = "_AddProperties";
    private const string ThisMigration = "_AddPropertyContractParties";

    private static readonly DateTime Anchor = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ── Migration (AC 18) ─────────────────────────────────────────────────────

    [SkippableFact]
    public async Task The_migration_keeps_every_existing_account_and_contact_party_unchanged()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        Guid accountPartyId = Guid.NewGuid(), contactPartyId = Guid.NewGuid(), accountId, contactId, contractId;
        await using (var context = NewContext())
        {
            await MigrationSeam.MigrateToAsync(context, PreviousMigration);
            accountId = await SeedAccountAsync(context);
            contactId = await SeedContactAsync(context);
            contractId = await SeedContractAsync(context, "Mortgage", ContextContractType.Loan);

            // Raw SQL: the model now maps PropertyId, a column this schema does not have yet.
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO `ContractParties` (`ContractPartyId`, `ContractId`, `AccountId`, `ContactId`, `Role`) VALUES " +
                $"('{accountPartyId}', '{contractId}', '{accountId}', NULL, {(int)ContextContractPartyRole.Borrower}), " +
                $"('{contactPartyId}', '{contractId}', NULL, '{contactId}', {(int)ContextContractPartyRole.Lender})");

            await MigrationSeam.MigrateToAsync(context, ThisMigration);
        }

        await using (var context = NewContext())
        {
            var parties = await context.ContractParties.AsNoTracking().OrderBy(p => p.Role).ToListAsync();

            Assert.Equal(2, parties.Count);
            Assert.Contains(parties, p => p.ContractPartyId == accountPartyId && p.AccountId == accountId
                && p.ContactId == null && p.PropertyId == null && p.Role == ContextContractPartyRole.Borrower);
            Assert.Contains(parties, p => p.ContractPartyId == contactPartyId && p.ContactId == contactId
                && p.AccountId == null && p.PropertyId == null && p.Role == ContextContractPartyRole.Lender);
        }
    }

    // ── Constraints (AC 17) ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task The_engine_rejects_two_targets_no_target_and_a_duplicate_property_role()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        await using var context = NewContext();
        var accountId = await SeedAccountAsync(context);
        var propertyId = await SeedPropertyAsync(context);
        var contractId = await SeedContractAsync(context, "Other", ContextContractType.Other);
        var role = (int)ContextContractPartyRole.Other;

        Task InsertAsync(string accountSql, string propertySql) =>
            context.Database.ExecuteSqlRawAsync(
                "INSERT INTO `ContractParties` (`ContractPartyId`, `ContractId`, `AccountId`, `ContactId`, `PropertyId`, `Role`) " +
                $"VALUES ('{Guid.NewGuid()}', '{contractId}', {accountSql}, NULL, {propertySql}, {role})");

        var both = await Assert.ThrowsAsync<MySqlException>(() => InsertAsync($"'{accountId}'", $"'{propertyId}'"));
        Assert.Contains("CK_ContractParties_ExactlyOneTarget", both.Message, StringComparison.Ordinal);

        var none = await Assert.ThrowsAsync<MySqlException>(() => InsertAsync("NULL", "NULL"));
        Assert.Contains("CK_ContractParties_ExactlyOneTarget", none.Message, StringComparison.Ordinal);

        await InsertAsync("NULL", $"'{propertyId}'");
        var duplicate = await Assert.ThrowsAsync<MySqlException>(() => InsertAsync("NULL", $"'{propertyId}'"));
        Assert.Equal(MySqlErrorCode.DuplicateKeyEntry, duplicate.ErrorCode);
        Assert.Contains("IX_ContractParties_ContractId_PropertyId_Role", duplicate.Message, StringComparison.Ordinal);
    }

    // ── Delete (AC 16) ────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Deleting_a_property_removes_its_party_rows_evented_and_keeps_the_contract()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        Guid propertyId, accountId, contractId;
        await using (var context = NewContext())
        {
            propertyId = await SeedPropertyAsync(context);
            accountId = await SeedAccountAsync(context);
            contractId = await SeedContractAsync(context, "Mortgage", ContextContractType.Loan,
                (null, propertyId, ContextContractPartyRole.Collateral),
                (accountId, null, ContextContractPartyRole.Borrower));
        }

        await using (var context = NewContext())
        {
            Assert.True(await new PropertyService(context).Delete(propertyId, userId: null));
        }

        await using (var context = NewContext())
        {
            var party = Assert.Single(await context.ContractParties.AsNoTracking().ToListAsync());
            Assert.Equal(accountId, party.AccountId);
            Assert.True(await context.Contracts.AnyAsync(c => c.ContractId == contractId));
            Assert.Equal(1, await context.ContractEvents.CountAsync(
                e => e.ContractId == contractId && e.Type == ContextContractEventType.PartyRemoved));
        }
    }

    /// <summary>The FK <c>CASCADE</c> is the backstop behind the service's tracked removal.</summary>
    [SkippableFact]
    public async Task A_raw_property_delete_cascades_its_party_rows_through_the_foreign_key()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        await using var context = NewContext();
        var propertyId = await SeedPropertyAsync(context);
        var contractId = await SeedContractAsync(context, "Cover", ContextContractType.Insurance,
            (null, propertyId, ContextContractPartyRole.Insured));

        await context.Database.ExecuteSqlRawAsync(
            $"DELETE FROM `RealEstateDetails` WHERE `PropertyId` = '{propertyId}'");
        await context.Database.ExecuteSqlRawAsync($"DELETE FROM `Properties` WHERE `PropertyId` = '{propertyId}'");

        Assert.Equal(0L, await MigrationSeam.CountAsync(context,
            $"SELECT COUNT(*) FROM `ContractParties` WHERE `ContractId` = '{contractId}'"));
        Assert.Equal(1L, await MigrationSeam.CountAsync(context,
            $"SELECT COUNT(*) FROM `Contracts` WHERE `ContractId` = '{contractId}'"));
    }

    // ── Translation ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task The_count_and_the_contracts_section_translate_and_count_contracts_not_party_rows()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        Guid propertyId, loneId, mortgageId;
        await using (var context = NewContext())
        {
            propertyId = await SeedPropertyAsync(context, "Cabin");
            loneId = await SeedPropertyAsync(context, "Lonely");
            mortgageId = await SeedContractAsync(context, "Mortgage", ContextContractType.Loan,
                (null, propertyId, ContextContractPartyRole.Collateral), (null, propertyId, ContextContractPartyRole.Property));
            await SeedContractAsync(context, "Cover", ContextContractType.Insurance,
                (null, propertyId, ContextContractPartyRole.Insured));
        }

        await using (var context = NewContext())
        {
            var properties = new PropertyService(context);
            var page = await properties.ListAsync(new PropertiesQueryParams(), includeContractCount: true);
            Assert.Equal(2, page.Items.Single(p => p.PropertyId == propertyId).ContractCount);
            Assert.Equal(0, page.Items.Single(p => p.PropertyId == loneId).ContractCount);
            Assert.Equal(2, (await properties.Get(propertyId, includeContractCount: true))!.ContractCount);
        }

        await using (var context = NewContext())
        {
            var contracts = new ContractService(
                context, new ContactLookup(context), TimeProvider.System, new ShippedCaps(),
                NullLogger<ContractService>.Instance);

            var rows = (await contracts.ListForPropertyAsync(propertyId))!;
            Assert.Equal(["Cover", "Mortgage"], rows.Select(r => r.Name));
            Assert.Equal(
                new HashSet<ContractPartyRole> { ContractPartyRole.Collateral, ContractPartyRole.Property },
                rows.Single(r => r.ContractId == mortgageId).Roles.ToHashSet());
            Assert.Empty((await contracts.ListForPropertyAsync(loneId))!);
            Assert.Null(await contracts.ListForPropertyAsync(Guid.NewGuid()));

            var detail = (await contracts.Get(mortgageId))!;
            Assert.All(detail.Parties, p =>
            {
                Assert.Equal(ContractPartyKind.Property, p.Kind);
                Assert.Equal("Cabin", p.Property!.Name);
            });
        }
    }

    // ── Seeding ───────────────────────────────────────────────────────────────

    private static async Task<Guid> SeedAccountAsync(OdysseyContext context)
    {
        var account = new Account
        {
            AccountId = Guid.NewGuid(),
            Name = "Everyday Checking",
            Description = "Seed",
            Opened = Anchor,
            AccountType = ContextAccountType.CheckingAccount,
            CurrencyCode = "USD",
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();
        return account.AccountId;
    }

    private static async Task<Guid> SeedContactAsync(OdysseyContext context)
    {
        var contact = new Contact
        {
            ContactId = Guid.NewGuid(),
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = "acme bank",
            Type = ContactType.Organization,
            OrganizationDetails = new() { LegalName = "Acme Bank" },
        };
        context.Contacts.Add(contact);
        await context.SaveChangesAsync();
        return contact.ContactId;
    }

    private static async Task<Guid> SeedPropertyAsync(OdysseyContext context, string name = "Storgata 14")
    {
        var property = new Property
        {
            Name = name,
            Description = "Seed",
            Type = PropertyType.RealEstate,
            CurrencyCode = "USD",
            CreatedAt = Anchor,
            UpdatedAt = Anchor,
            RealEstateDetails = new RealEstateDetails { Kind = RealEstateKind.House, City = "Oslo" },
        };
        context.Properties.Add(property);
        await context.SaveChangesAsync();
        return property.PropertyId;
    }

    private static async Task<Guid> SeedContractAsync(
        OdysseyContext context, string name, ContextContractType type,
        params (Guid? AccountId, Guid? PropertyId, ContextContractPartyRole Role)[] parties)
    {
        var contract = new Contract
        {
            ContractId = Guid.NewGuid(),
            Name = name,
            Type = type,
            StartDate = Anchor,
            Signed = Anchor,
            CreatedAtUtc = Anchor,
        };
        foreach (var (accountId, propertyId, role) in parties)
        {
            contract.Parties.Add(new ContractParty
            {
                ContractPartyId = Guid.NewGuid(),
                ContractId = contract.ContractId,
                AccountId = accountId,
                PropertyId = propertyId,
                Role = role,
            });
        }

        context.Contracts.Add(contract);
        await context.SaveChangesAsync();
        return contract.ContractId;
    }

    private sealed class ShippedCaps : ISystemSettingsLookup
    {
        public Task<FinanceRequestCaps> GetRequestCapsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new FinanceRequestCaps(25, 50, 500, 1000));

        public Task<ContractSummarySettings> GetContractSummarySettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ContractSummarySettings(45, 45, 6));
    }

    private async Task RecreateAsync()
    {
        await using var server = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);
        await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
        await server.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
    }

    private async Task MigrateAsync()
    {
        await RecreateAsync();
        await using var context = NewContext();
        await context.Database.MigrateAsync();
    }

    private OdysseyContext NewContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);
}
