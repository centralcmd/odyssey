// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid or a
// formatted DateTime this test generated itself — there is no external input — and the pre-migration
// rows are deliberately written outside the service, which is the whole point of the seam.
#pragma warning disable EF1002

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Issue #157's data migration (AC 15, AC 25): the two role remaps and the pass over the
/// <c>(type, role)</c> pairs the new matrix rejects.
/// </summary>
/// <remarks>
/// Step 3 is the one part of this change whose correctness the compiler cannot check — it is a table
/// of literal ordinals, written against the matrix as specified rather than against the C# declaration
/// (which no longer holds the retired members, and which a later issue may widen). So it gets its own
/// test, and that test asserts the <b>specific</b> landing value rather than merely "legal now":
/// "the type's nearest legal role" was the ambiguity the spec removed, and an implementation that
/// picked a plausible neighbour would satisfy a looser assertion while doing something nobody agreed.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class RetirePartyRolesMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_retire_party_roles";

    /// <summary>The migration immediately before the one under test.</summary>
    private const string Baseline = "_AddContractFileValidity";

    private const int Unspecified = 0;
    private const int Employee = 1;
    private const int Buyer = 3;
    private const int Seller = 4;
    private const int ServiceProvider = 5;
    private const int Other = 6;
    private const int Insurer = 9;

    private const int EmploymentType = 0;
    private const int RentalType = 2;
    private const int OtherType = 3;
    private const int InsuranceType = 4;

    /// <summary>
    /// AC 15 and AC 25 in one pass over a fixture built to cover every branch: the two retired
    /// ordinals disappear, and every surviving row sits on a cell the matrix accepts.
    /// </summary>
    /// <remarks>
    /// The Insurance row is the spec's own worked example of the ambiguity step 3 closes. It arrives
    /// as a <c>ServiceProvider</c>, step 2 moves it to <c>Seller</c>, and <c>Seller</c> is rejected on
    /// Insurance — where <c>Insurer</c>, <c>Policyholder</c>, <c>Insured</c> and <c>Beneficiary</c> are
    /// all equally "near". It must land on <c>Other</c>: legal on every type, and honest that the
    /// original value did not survive.
    /// </remarks>
    [SkippableFact]
    public async Task The_migration_retires_both_ordinals_and_remaps_every_rejected_pair_to_Other()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var connectionString = await MigratedToBaselineAsync();
        try
        {
            var contactId = Guid.NewGuid();

            // (contract type, pre-migration role, expected role after) — one row per branch.
            var cases = new (Guid ContractId, Guid PartyId, int Type, int Role, int Expected, string Why)[]
            {
                (Guid.NewGuid(), Guid.NewGuid(), OtherType, Unspecified, Other,
                    "step 1: Unspecified is retired and becomes Other"),
                (Guid.NewGuid(), Guid.NewGuid(), EmploymentType, ServiceProvider, Other,
                    "step 2 lands it on Seller, which step 3 then rejects on Employment"),
                (Guid.NewGuid(), Guid.NewGuid(), RentalType, ServiceProvider, Other,
                    "same, on Rental — a service provider on a tenancy has no surviving vocabulary"),
                (Guid.NewGuid(), Guid.NewGuid(), InsuranceType, ServiceProvider, Other,
                    "the worked example: Seller on Insurance, with four equally near legal roles"),
                (Guid.NewGuid(), Guid.NewGuid(), EmploymentType, Employee, Employee,
                    "a legal cell is left alone"),
                (Guid.NewGuid(), Guid.NewGuid(), InsuranceType, Insurer, Insurer,
                    "a role that did not exist before this change, written by hand, is still legal"),
                (Guid.NewGuid(), Guid.NewGuid(), RentalType, Buyer, Other,
                    "step 3 alone: Buyer was never retired, it is simply illegal on a tenancy"),
                (Guid.NewGuid(), Guid.NewGuid(), OtherType, Seller, Seller,
                    "the catch-all type rejects nothing, so a Seller there survives untouched"),
            };

            await using (var context = New(connectionString))
            {
                await SeedContactAsync(context, contactId);
                foreach (var row in cases)
                {
                    await SeedContractAsync(context, row.ContractId, row.Type);
                    await AddPartyAsync(context, row.PartyId, row.ContractId, contactId, row.Role);
                }
            }

            await using (var context = New(connectionString))
            {
                await context.Database.MigrateAsync();

                var stored = await context.ContractParties.AsNoTracking()
                    .ToDictionaryAsync(p => p.ContractPartyId, p => (int)p.Role);

                foreach (var row in cases)
                {
                    Assert.True(stored.ContainsKey(row.PartyId), $"{row.Why}: the row was dropped.");
                    Assert.Equal(row.Expected, stored[row.PartyId]);
                }

                // AC 15 — nothing is left at either retired ordinal, anywhere.
                Assert.DoesNotContain(Unspecified, stored.Values);
                Assert.DoesNotContain(ServiceProvider, stored.Values);
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// Re-running the remap is a no-op. MariaDB commits DDL implicitly, so an interrupted migration
    /// can be replayed — and <c>Other</c> is legal on every type, which is what makes the third step
    /// idempotent in effect rather than merely by luck.
    /// </summary>
    [SkippableFact]
    public async Task Re_running_the_remap_changes_nothing()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var connectionString = await MigratedToBaselineAsync();
        try
        {
            var contactId = Guid.NewGuid();
            var contractId = Guid.NewGuid();
            var partyId = Guid.NewGuid();

            await using (var context = New(connectionString))
            {
                await SeedContactAsync(context, contactId);
                await SeedContractAsync(context, contractId, InsuranceType);
                await AddPartyAsync(context, partyId, contractId, contactId, ServiceProvider);
            }

            await using (var context = New(connectionString))
            {
                await context.Database.MigrateAsync();
            }

            await using (var context = New(connectionString))
            {
                // The same three statements, replayed by hand exactly as a re-run would.
                await context.Database.ExecuteSqlRawAsync("UPDATE ContractParties SET Role = 6 WHERE Role = 0;");
                await context.Database.ExecuteSqlRawAsync("UPDATE ContractParties SET Role = 4 WHERE Role = 5;");
                await context.Database.ExecuteSqlRawAsync(
                    """
                    UPDATE ContractParties AS p
                    JOIN Contracts AS c ON c.ContractId = p.ContractId
                    SET p.Role = 6
                    WHERE c.Type = 4 AND p.Role NOT IN (6, 9, 10, 11, 12, 16);
                    """);

                var party = await context.ContractParties.AsNoTracking().FirstAsync(p => p.ContractPartyId == partyId);
                Assert.Equal(Other, (int)party.Role);
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Task SeedContactAsync(OdysseyContext context, Guid contactId)
    {
        context.Contacts.Add(new Contact
        {
            ContactId = contactId,
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            OrganizationDetails = new() { LegalName = "Migration Subject" },
            NormalizedName = "MIGRATION SUBJECT",
            Type = Odyssey.Dtos.ContactType.Organization,
        });
        return context.SaveChangesAsync();
    }

    private static Task SeedContractAsync(OdysseyContext context, Guid contractId, int type)
    {
        var createdAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
        return context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `Contracts` (`ContractId`, `Name`, `Type`, `CreatedAtUtc`)
            VALUES ('{contractId}', 'Migration fixture', {type}, '{createdAt}');
            """);
    }

    /// <summary>
    /// Inserts one party through raw SQL, bypassing the service and its matrix check entirely — which
    /// is the point: these are the rows a pre-matrix database holds, and no write path would produce
    /// them today.
    /// </summary>
    private static Task AddPartyAsync(
        OdysseyContext context, Guid partyId, Guid contractId, Guid contactId, int role) =>
        context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `ContractParties` (`ContractPartyId`, `ContractId`, `AccountId`, `ContactId`, `Role`)
            VALUES ('{partyId}', '{contractId}', NULL, '{contactId}', {role});
            """);

    private async Task<string> MigratedToBaselineAsync()
    {
        await DropAsync();

        await using (var admin = new OdysseyContext(OptionsFor(fixture.OdysseyConnectionString)))
        {
            await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
        }

        var connectionString = fixture.ConnectionStringFor(Database);
        await using var context = new OdysseyContext(OptionsFor(connectionString));
        await MigrationSeam.MigrateToAsync(context, Baseline);

        return connectionString;
    }

    private async Task DropAsync()
    {
        await using var admin = new OdysseyContext(OptionsFor(fixture.OdysseyConnectionString));
        await admin.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
    }

    private static OdysseyContext New(string connectionString) => new(OptionsFor(connectionString));

    private static DbContextOptions<OdysseyContext> OptionsFor(string connectionString) =>
        new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connectionString, ServerVersion.AutoDetect(connectionString))
            .Options;
}
