// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid or a
// formatted DateTime this test generated itself — there is no external input — and the pre-migration
// rows have no entity type to write through, which is the whole point of the seam.
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
/// The data half of <c>AddPolicyPartyTermsAndDropContractPolicyParty</c>: the
/// <c>DELETE FROM ContractParties WHERE InsurancePolicyId IS NOT NULL</c> that runs before the XOR
/// <c>CHECK</c> is re-added, and the term columns the same migration adds to the four link tables.
/// </summary>
/// <remarks>
/// <para>
/// None of this is observable on the fast tiers. EF InMemory has no schema, no migrations and no
/// <c>CHECK</c> constraints, and the ordinary relational fixtures migrate straight to head — so by the
/// time a test could write a policy-only party row, the migration that deletes it has already run
/// against an empty table. The <c>migrate to N−1 → seed → migrate to head</c> seam is
/// <see cref="MigrationSeam"/>.
/// </para>
/// <para>
/// The delete is load-bearing rather than tidy-up: MariaDB validates a <c>CHECK</c> against existing
/// rows when it is added, and a party whose only target was a policy has no target left once the
/// column goes — so leaving one behind makes the migration itself fail to apply.
/// </para>
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class ContractPartyPolicyRemovalTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_contract_party_removal";

    /// <summary>The migration immediately before the one under test — the last point at which
    /// <c>ContractParties.InsurancePolicyId</c> exists and can hold the state to be removed.</summary>
    private const string Baseline = "_AddInsurancePolicyLinkCollections";

    /// <summary>
    /// A policy-only party is deleted; the account and contact parties beside it are untouched, ids
    /// included. Deleting is the only option that keeps the XOR invariant — there is no other target
    /// to fall back to — so the assertion is that the migration removes exactly those rows and no
    /// more.
    /// </summary>
    [SkippableFact]
    public async Task A_policy_only_party_is_deleted_and_its_siblings_survive()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var contract = Guid.NewGuid();
        var policy = Guid.NewGuid();
        var account = Guid.NewGuid();
        var contact = Guid.NewGuid();
        var policyParty = Guid.NewGuid();
        var accountParty = Guid.NewGuid();
        var contactParty = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await SeedTargetsAsync(context, policy, account, contact);
                await SeedContractAsync(context, contract);

                await AddPartyAsync(context, policyParty, contract, policyId: policy);
                await AddPartyAsync(context, accountParty, contract, accountId: account);
                await AddPartyAsync(context, contactParty, contract, contactId: contact);
            }

            await using (var context = NewContext())
            {
                await context.Database.MigrateAsync();

                var surviving = await context.ContractParties
                    .AsNoTracking()
                    .Where(p => p.ContractId == contract)
                    .ToListAsync();

                Assert.Equal(2, surviving.Count);
                Assert.DoesNotContain(surviving, p => p.ContractPartyId == policyParty);

                // The survivors keep their own ids — a party row is a stable handle the API addresses,
                // so a migration that recreated them would break every link a client holds.
                var kept = surviving.Single(p => p.ContractPartyId == accountParty);
                Assert.Equal(account, kept.AccountId);
                Assert.Null(kept.ContactId);

                Assert.Equal(contact, surviving.Single(p => p.ContractPartyId == contactParty).ContactId);
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// The re-added <c>CHECK</c> is live afterwards and enforces one-of-TWO: a party naming neither
    /// target is refused. This is what the delete exists to make possible — the constraint could not
    /// have been created at all with a targetless row present.
    /// </summary>
    [SkippableFact]
    public async Task The_reinstated_check_rejects_a_party_with_no_target()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var contract = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await context.Database.MigrateAsync();
                await SeedContractAsync(context, contract);
            }

            await using (var context = NewContext())
            {
                var orphan = Guid.NewGuid();
                await Assert.ThrowsAsync<MySqlException>(() => context.Database.ExecuteSqlRawAsync($"""
                    INSERT INTO `ContractParties` (`ContractPartyId`, `ContractId`)
                    VALUES ('{orphan}', '{contract}');
                    """));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// A link row written before the migration reads back with BOTH dates null — which is not a gap
    /// but the default term, the policy's own extent. An upgrade therefore leaves every existing
    /// party following its policy exactly as it did.
    /// </summary>
    [SkippableFact]
    public async Task An_existing_link_upgrades_to_the_default_term()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var policy = Guid.NewGuid();
        var contact = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await SeedTargetsAsync(context, policy, accountId: null, contactId: contact);

                await context.Database.ExecuteSqlRawAsync($"""
                    INSERT INTO `InsurancePolicyInsurers` (`Id`, `InsurancePolicyId`, `ContactId`)
                    VALUES ('{Guid.NewGuid()}', '{policy}', '{contact}');
                    """);
            }

            await using (var context = NewContext())
            {
                await context.Database.MigrateAsync();

                var insurer = Assert.Single(await context.InsurancePolicyInsurers
                    .AsNoTracking()
                    .Where(l => l.InsurancePolicyId == policy)
                    .ToListAsync());

                Assert.Null(insurer.FromDate);
                Assert.Null(insurer.ToDate);
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    // ── Seeding at the baseline schema ─────────────────────────────────────────

    private static async Task SeedTargetsAsync(
        OdysseyContext context, Guid policyId, Guid? accountId, Guid? contactId)
    {
        if (contactId is { } contact)
        {
            context.Contacts.Add(new Contact
            {
                ContactId = contact,
                ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
                OrganizationDetails = new() { LegalName = "Counterparty" },
                NormalizedName = "COUNTERPARTY",
                Type = ContactType.Organization,
            });
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
        }

        context.InsurancePolicies.Add(new InsurancePolicy
        {
            InsurancePolicyId = policyId,
            Name = "Home cover",
            Type = Odyssey.Context.InsurancePolicyType.Home,
            CreatedAtUtc = DateTime.UtcNow,
        });

        await context.SaveChangesAsync();
    }

    private static Task SeedContractAsync(OdysseyContext context, Guid contractId)
    {
        var createdAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
        return context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `Contracts` (`ContractId`, `Name`, `Type`, `CreatedAtUtc`)
            VALUES ('{contractId}', 'Mortgage mandate', 4, '{createdAt}');
            """);
    }

    /// <summary>
    /// Inserts one party through raw SQL, because the entity no longer has an
    /// <c>InsurancePolicyId</c> to write through — which is exactly the pre-migration state this has
    /// to build.
    /// </summary>
    private static Task AddPartyAsync(
        OdysseyContext context, Guid partyId, Guid contractId,
        Guid? accountId = null, Guid? contactId = null, Guid? policyId = null)
    {
        static string Value(Guid? id) => id is null ? "NULL" : $"'{id}'";

        return context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `ContractParties`
                (`ContractPartyId`, `ContractId`, `AccountId`, `ContactId`, `InsurancePolicyId`)
            VALUES ('{partyId}', '{contractId}', {Value(accountId)}, {Value(contactId)}, {Value(policyId)});
            """);
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
