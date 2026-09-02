using System.Globalization;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Odyssey.Context;
using Odyssey.Core;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using ContextAccountType = Odyssey.Context.AccountType;
using ContextInsurancePolicyType = Odyssey.Context.InsurancePolicyType;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The insurance link collections (issue #27) against the real engine — the half EF InMemory cannot
/// see, because it enforces no foreign keys and honours neither transactions nor the execution
/// strategy.
/// </summary>
/// <remarks>
/// Four things live here and nowhere else: the <c>RESTRICT</c> constraints that make a linked contact
/// undeletable, the <c>CASCADE</c> that removes an account's link rows while the policy stands, the
/// detach path's <b>one transaction</b>, and the migration's <c>SIGNAL</c> guard, which is the only
/// thing standing between a failed backfill and a dropped column.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class InsurancePolicyLinkIntegrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_insurance_links";

    // ── The three RESTRICT constraints ─────────────────────────────────────────

    [SkippableTheory]
    [InlineData("insurer")]
    [InlineData("insured-contact")]
    [InlineData("beneficiary")]
    public async Task A_contact_named_in_any_contact_collection_cannot_be_deleted(string kind)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var connectionString = await MigratedSchemaAsync();
        try
        {
            var contactId = Guid.NewGuid();
            var policyId = Guid.NewGuid();

            await using (var context = New(connectionString))
            {
                context.Contacts.Add(Organization(contactId, "Restricted Contact"));
                context.InsurancePolicies.Add(Policy(policyId, "Restricting cover"));
                await context.SaveChangesAsync();

                AddLink(context, kind, policyId, contactId);
                await context.SaveChangesAsync();
            }

            // The database is the arbiter — there is no advisory lock on the write path any more
            // (issue #27 §5), so this constraint is what closes the check-and-write race.
            await using (var context = New(connectionString))
            {
                await Assert.ThrowsAsync<MySqlException>(() =>
                    context.Contacts.Where(c => c.ContactId == contactId).ExecuteDeleteAsync());
            }

            await using (var context = New(connectionString))
            {
                // Refused, not partially applied.
                Assert.True(await context.Contacts.AnyAsync(c => c.ContactId == contactId));
                Assert.Equal(1, await LinkCountAsync(context, kind, policyId));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// The application-level guard in front of those constraints: the caller gets a 409 that explains
    /// itself rather than a raw FK violation surfacing as a 500.
    /// </summary>
    [SkippableFact]
    public async Task The_service_refuses_the_delete_before_the_constraint_has_to_fire()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var connectionString = await MigratedSchemaAsync();
        try
        {
            var contactId = Guid.NewGuid();
            var policyId = Guid.NewGuid();

            await using (var context = New(connectionString))
            {
                context.Contacts.Add(Organization(contactId, "In-Use Beneficiary"));
                context.InsurancePolicies.Add(Policy(policyId, "Term life"));
                await context.SaveChangesAsync();
                AddLink(context, "beneficiary", policyId, contactId);
                await context.SaveChangesAsync();
            }

            await using (var context = New(connectionString))
            {
                var service = new ContactService(context, new ContactReferenceGuard(context));
                await Assert.ThrowsAsync<DomainConflictException>(() => service.Delete(contactId));
            }

            await using (var context = New(connectionString))
            {
                Assert.True(await context.Contacts.AnyAsync(c => c.ContactId == contactId));
                Assert.Equal(1, await context.InsurancePolicyBeneficiaries.CountAsync(l => l.ContactId == contactId));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    // ── The detach path ────────────────────────────────────────────────────────

    /// <summary>
    /// The supported release valve for an erasure request: every link row and the contact, in ONE
    /// request and one transaction. The transaction is the point — it is why an interruption cannot
    /// leave the links gone with the contact still present.
    /// </summary>
    [SkippableFact]
    public async Task The_detach_path_removes_every_link_and_the_contact()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var connectionString = await MigratedSchemaAsync();
        try
        {
            var contactId = Guid.NewGuid();
            var policyOne = Guid.NewGuid();
            var policyTwo = Guid.NewGuid();

            await using (var context = New(connectionString))
            {
                context.Contacts.Add(Organization(contactId, "Erasable Contact"));
                context.InsurancePolicies.Add(Policy(policyOne, "Home"));
                context.InsurancePolicies.Add(Policy(policyTwo, "Term life"));
                await context.SaveChangesAsync();

                AddLink(context, "insurer", policyOne, contactId);
                AddLink(context, "insured-contact", policyOne, contactId);
                AddLink(context, "beneficiary", policyTwo, contactId);
                await context.SaveChangesAsync();
            }

            DetachedInsuranceLinks? detached;
            await using (var context = New(connectionString))
            {
                var service = new ContactService(context, new ContactReferenceGuard(context));
                detached = await service.Delete(contactId, detachInsuranceLinks: true);
            }

            Assert.NotNull(detached);
            Assert.Equal(3, detached!.TotalLinks);
            Assert.Equal(3, detached.Kinds.Count);
            Assert.Equal(new[] { policyOne, policyTwo }.Order(), detached.AffectedPolicyIds.Order());

            await using (var context = New(connectionString))
            {
                Assert.False(await context.Contacts.AnyAsync(c => c.ContactId == contactId));
                Assert.Empty(await context.InsurancePolicyInsurers.Where(l => l.ContactId == contactId).ToListAsync());
                Assert.Empty(await context.InsurancePolicyInsuredContacts.Where(l => l.ContactId == contactId).ToListAsync());
                Assert.Empty(await context.InsurancePolicyBeneficiaries.Where(l => l.ContactId == contactId).ToListAsync());
                // The policies survive, each with one fewer member.
                Assert.Equal(2, await context.InsurancePolicies.CountAsync());
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// The atomicity itself. A failure after the links are staged must leave BOTH the contact and
    /// every link row intact — the state an interruption would otherwise leave is the exploit: links
    /// gone, contact still present, and no record of what happened.
    /// </summary>
    [SkippableFact]
    public async Task A_failure_mid_detach_leaves_both_the_contact_and_every_link_intact()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var connectionString = await MigratedSchemaAsync();
        try
        {
            var contactId = Guid.NewGuid();
            var policyId = Guid.NewGuid();

            await using (var context = New(connectionString))
            {
                context.Contacts.Add(Organization(contactId, "Erasable Contact"));
                context.InsurancePolicies.Add(Policy(policyId, "Term life"));
                await context.SaveChangesAsync();
                AddLink(context, "beneficiary", policyId, contactId);
                await context.SaveChangesAsync();
            }

            await using (var context = New(connectionString))
            {
                // The guard stages the link removal onto the caller's context and then throws before
                // anything is saved, standing in for any failure between the two writes.
                var service = new ContactService(context, new ThrowingAfterStageGuard(context));
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    service.Delete(contactId, detachInsuranceLinks: true));
            }

            await using (var context = New(connectionString))
            {
                Assert.True(await context.Contacts.AnyAsync(c => c.ContactId == contactId));
                Assert.Equal(1, await context.InsurancePolicyBeneficiaries.CountAsync(l => l.ContactId == contactId));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    // ── The account cascade ────────────────────────────────────────────────────

    /// <summary>
    /// Deleting an insured account removes its link rows and leaves every policy standing — the same
    /// observable outcome the former scalar column's <c>SET NULL</c> had. Asserted at the database
    /// because here it is the FK doing it; the application-code half that serves EF InMemory is
    /// asserted separately in <c>Odyssey.Api.Tests</c>, since the two use different mechanisms.
    /// </summary>
    [SkippableFact]
    public async Task Deleting_an_insured_account_removes_its_links_and_keeps_the_policies()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var connectionString = await MigratedSchemaAsync();
        try
        {
            var accountId = Guid.NewGuid();
            var policyOne = Guid.NewGuid();
            var policyTwo = Guid.NewGuid();

            await using (var context = New(connectionString))
            {
                context.Accounts.Add(new Account
                {
                    AccountId = accountId,
                    Name = "Insured Home",
                    Description = "asset",
                    Opened = DateTime.UtcNow,
                    AccountType = ContextAccountType.Property,
                    CurrencyCode = "USD",
                });
                context.InsurancePolicies.Add(Policy(policyOne, "Home"));
                context.InsurancePolicies.Add(Policy(policyTwo, "Contents"));
                await context.SaveChangesAsync();

                context.InsurancePolicyInsuredAccounts.Add(new InsurancePolicyInsuredAccount
                {
                    InsurancePolicyId = policyOne,
                    AccountId = accountId,
                });
                context.InsurancePolicyInsuredAccounts.Add(new InsurancePolicyInsuredAccount
                {
                    InsurancePolicyId = policyTwo,
                    AccountId = accountId,
                });
                await context.SaveChangesAsync();
            }

            await using (var context = New(connectionString))
            {
                await context.Accounts.Where(a => a.AccountId == accountId).ExecuteDeleteAsync();
            }

            await using (var context = New(connectionString))
            {
                Assert.Empty(await context.InsurancePolicyInsuredAccounts.Where(l => l.AccountId == accountId).ToListAsync());
                Assert.Equal(2, await context.InsurancePolicies.CountAsync());
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    [SkippableFact]
    public async Task Deleting_a_policy_removes_every_link_row()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var connectionString = await MigratedSchemaAsync();
        try
        {
            var contactId = Guid.NewGuid();
            var policyId = Guid.NewGuid();

            await using (var context = New(connectionString))
            {
                context.Contacts.Add(Organization(contactId, "Acme"));
                context.InsurancePolicies.Add(Policy(policyId, "Home"));
                await context.SaveChangesAsync();

                AddLink(context, "insurer", policyId, contactId);
                AddLink(context, "beneficiary", policyId, contactId);
                await context.SaveChangesAsync();
            }

            await using (var context = New(connectionString))
            {
                await context.InsurancePolicies.Where(p => p.InsurancePolicyId == policyId).ExecuteDeleteAsync();
            }

            await using (var context = New(connectionString))
            {
                Assert.Empty(await context.InsurancePolicyInsurers.Where(l => l.InsurancePolicyId == policyId).ToListAsync());
                Assert.Empty(await context.InsurancePolicyBeneficiaries.Where(l => l.InsurancePolicyId == policyId).ToListAsync());
                // The contact is untouched — the link died with the POLICY, not with it.
                Assert.True(await context.Contacts.AnyAsync(c => c.ContactId == contactId));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    [SkippableFact]
    public async Task A_duplicate_link_is_refused_by_the_unique_index()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var connectionString = await MigratedSchemaAsync();
        try
        {
            var contactId = Guid.NewGuid();
            var policyId = Guid.NewGuid();

            await using (var context = New(connectionString))
            {
                context.Contacts.Add(Organization(contactId, "Acme"));
                context.InsurancePolicies.Add(Policy(policyId, "Home"));
                await context.SaveChangesAsync();
                AddLink(context, "insurer", policyId, contactId);
                await context.SaveChangesAsync();
            }

            await using (var context = New(connectionString))
            {
                AddLink(context, "insurer", policyId, contactId);
                await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    // ── The migration's own backfill, against real pre-migration data ──────────

    /// <summary>
    /// Seeds a policy in the OLD shape — scalar <c>InsurerId</c>/<c>InsuredAccountId</c> — then runs
    /// the real migration and asserts it carried both into link rows and dropped the columns.
    ///
    /// <para>
    /// This is the production backfill DML itself, not a simulation of it. Everything else in this
    /// file migrates straight to head, where the old columns no longer exist and the
    /// <c>INSERT … SELECT</c> has nothing to carry — so without stopping at the preceding migration
    /// first, the statements that actually move a deployment's data would never run under test.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task The_migration_backfills_the_scalar_columns_into_link_rows()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var connectionString = await CreateEmptyAsync();
        try
        {
            var insurerId = Guid.NewGuid();
            var accountId = Guid.NewGuid();
            var withBoth = Guid.NewGuid();
            var insurerOnly = Guid.NewGuid();

            await using (var context = New(connectionString))
            {
                await MigrationSeam.MigrateToAsync(context, PrecedingMigration);

                context.Contacts.Add(Organization(insurerId, "Legacy Insurer"));
                context.Accounts.Add(new Account
                {
                    AccountId = accountId,
                    Name = "Legacy Home",
                    Description = "asset",
                    Opened = DateTime.UtcNow,
                    AccountType = ContextAccountType.Property,
                    CurrencyCode = "USD",
                });
                await context.SaveChangesAsync();

                // Raw SQL, because the entity no longer has the columns the old schema still demands.
                await InsertLegacyPolicyAsync(context, withBoth, "Both links", insurerId, accountId);
                await InsertLegacyPolicyAsync(context, insurerOnly, "Insurer only", insurerId, null);
            }

            await using (var context = New(connectionString))
            {
                await context.Database.MigrateAsync();

                // Exactly one link row of each kind per policy that had the scalar set.
                Assert.Equal(insurerId, (await context.InsurancePolicyInsurers.AsNoTracking()
                    .SingleAsync(l => l.InsurancePolicyId == withBoth)).ContactId);
                Assert.Equal(insurerId, (await context.InsurancePolicyInsurers.AsNoTracking()
                    .SingleAsync(l => l.InsurancePolicyId == insurerOnly)).ContactId);
                Assert.Equal(accountId, (await context.InsurancePolicyInsuredAccounts.AsNoTracking()
                    .SingleAsync(l => l.InsurancePolicyId == withBoth)).AccountId);

                // A null scalar carried nothing — it is not a link to a zero GUID.
                Assert.Empty(await context.InsurancePolicyInsuredAccounts.AsNoTracking()
                    .Where(l => l.InsurancePolicyId == insurerOnly).ToListAsync());

                // Both policies survive, and the source columns are gone.
                Assert.Equal(2, await context.InsurancePolicies.CountAsync());
                Assert.False(await ColumnExistsAsync(context, "InsurancePolicies", "InsurerId"));
                Assert.False(await ColumnExistsAsync(context, "InsurancePolicies", "InsuredAccountId"));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// The backfill is written <c>INSERT … SELECT … WHERE NOT EXISTS</c> so a manual re-apply — the
    /// path <c>docs/migration-history-drift.md</c> describes — inserts nothing the second time rather
    /// than dying on the unique index. Re-running the statement is the only way to prove that.
    /// </summary>
    [SkippableFact]
    public async Task The_backfill_is_idempotent_when_replayed_by_hand()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var connectionString = await CreateEmptyAsync();
        try
        {
            var insurerId = Guid.NewGuid();
            var policyId = Guid.NewGuid();

            await using (var context = New(connectionString))
            {
                await MigrationSeam.MigrateToAsync(context, PrecedingMigration);
                context.Contacts.Add(Organization(insurerId, "Legacy Insurer"));
                await context.SaveChangesAsync();
                await InsertLegacyPolicyAsync(context, policyId, "Replayed", insurerId, null);
            }

            await using (var context = New(connectionString))
            {
                await context.Database.MigrateAsync();
                Assert.Equal(1, await context.InsurancePolicyInsurers.CountAsync(l => l.InsurancePolicyId == policyId));

                // The migration's own statement, re-run against the post-migration schema. The source
                // column is gone by now, so the operator's repair runs it against whatever they
                // restored; what matters is that the guard clause refuses a duplicate rather than
                // throwing on the unique index.
                var replay = string.Format(
                    CultureInfo.InvariantCulture,
                    """
                    INSERT INTO `InsurancePolicyInsurers` (`Id`, `InsurancePolicyId`, `ContactId`)
                    SELECT UUID(), '{0}', '{1}'
                    WHERE NOT EXISTS (
                        SELECT 1 FROM `InsurancePolicyInsurers` l
                        WHERE l.`InsurancePolicyId` = '{0}' AND l.`ContactId` = '{1}');
                    """,
                    policyId,
                    insurerId);
                await context.Database.ExecuteSqlRawAsync(replay);

                Assert.Equal(1, await context.InsurancePolicyInsurers.CountAsync(l => l.InsurancePolicyId == policyId));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    // ── The migration's verify step ────────────────────────────────────────────

    /// <summary>
    /// The <c>SIGNAL SQLSTATE '45000'</c> guard, which is the whole reason the verify step exists: on
    /// MariaDB a <c>SELECT</c> that returns rows fails NOTHING, so a verify written as one would sail
    /// straight past an incomplete backfill and then drop the source columns.
    ///
    /// <para>
    /// Simulated rather than run through the real migration, because the migration drops the very
    /// columns the guard reads — so once it has been applied, the sabotage it protects against is
    /// unreachable. The statement asserted here is the guard's own SQL.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task The_migrations_verify_step_aborts_on_an_incomplete_backfill()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var connectionString = await MigratedSchemaAsync();
        try
        {
            await using var context = New(connectionString);

            // Stand the pre-migration shape back up: a policy carrying a scalar InsurerId with no
            // link row backfilled for it.
            await context.Database.ExecuteSqlRawAsync(
                "CREATE TABLE `VerifyProbe` (`InsurancePolicyId` char(36) NOT NULL, `InsurerId` char(36) NULL)");
            await context.Database.ExecuteSqlAsync(
                $"INSERT INTO `VerifyProbe` VALUES ({Guid.NewGuid()}, {Guid.NewGuid()})");

            var guard = """
                BEGIN NOT ATOMIC
                    DECLARE orphans INT;
                    SELECT COUNT(*) INTO orphans
                    FROM `VerifyProbe` p
                    WHERE p.`InsurerId` IS NOT NULL AND NOT EXISTS (
                        SELECT 1 FROM `InsurancePolicyInsurers` l
                        WHERE l.`InsurancePolicyId` = p.`InsurancePolicyId` AND l.`ContactId` = p.`InsurerId`);
                    IF orphans > 0 THEN
                        SIGNAL SQLSTATE '45000'
                            SET MESSAGE_TEXT = 'backfill is incomplete';
                    END IF;
                END;
                """;

            var error = await Assert.ThrowsAsync<MySqlException>(() =>
                context.Database.ExecuteSqlRawAsync(guard));
            Assert.Contains("backfill is incomplete", error.Message, StringComparison.Ordinal);

            // And it passes once the row it is looking for exists — a guard that always fired would be
            // no more use than one that never did.
            await context.Database.ExecuteSqlRawAsync("DELETE FROM `VerifyProbe`");
            await context.Database.ExecuteSqlRawAsync(guard);
        }
        finally
        {
            await DropAsync();
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Stages the detach exactly as the real guard does, then throws — standing in for any failure
    /// between the link removal and the contact delete. The two must be one transaction, so nothing
    /// should survive it.
    /// </summary>
    private sealed class ThrowingAfterStageGuard(OdysseyContext context) : IContactReferenceGuard
    {
        private readonly ContactReferenceGuard inner = new(context);

        public Task<InsuranceLinkBlockers> GetInsuranceLinkBlockersAsync(Guid contactId, CancellationToken cancellationToken = default) =>
            inner.GetInsuranceLinkBlockersAsync(contactId, cancellationToken);

        public Task<bool> IsReferencedByInsuranceAsync(Guid contactId, CancellationToken cancellationToken = default) =>
            inner.IsReferencedByInsuranceAsync(contactId, cancellationToken);

        public Task ClearAndCascadeReferencesAsync(Guid contactId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Induced failure after the links were staged.");

        public Task<DetachedInsuranceLinks> StageInsuranceLinkDetachAsync(Guid contactId, CancellationToken cancellationToken = default) =>
            inner.StageInsuranceLinkDetachAsync(contactId, cancellationToken);
    }

    private static void AddLink(OdysseyContext context, string kind, Guid policyId, Guid contactId)
    {
        switch (kind)
        {
            case "insurer":
                context.InsurancePolicyInsurers.Add(new InsurancePolicyInsurer
                {
                    InsurancePolicyId = policyId,
                    ContactId = contactId,
                });
                break;
            case "insured-contact":
                context.InsurancePolicyInsuredContacts.Add(new InsurancePolicyInsuredContact
                {
                    InsurancePolicyId = policyId,
                    ContactId = contactId,
                });
                break;
            default:
                context.InsurancePolicyBeneficiaries.Add(new InsurancePolicyBeneficiary
                {
                    InsurancePolicyId = policyId,
                    ContactId = contactId,
                    CreatedAtUtc = DateTime.UtcNow,
                });
                break;
        }
    }

    private static Task<int> LinkCountAsync(OdysseyContext context, string kind, Guid policyId) => kind switch
    {
        "insurer" => context.InsurancePolicyInsurers.CountAsync(l => l.InsurancePolicyId == policyId),
        "insured-contact" => context.InsurancePolicyInsuredContacts.CountAsync(l => l.InsurancePolicyId == policyId),
        _ => context.InsurancePolicyBeneficiaries.CountAsync(l => l.InsurancePolicyId == policyId),
    };

    private static Contact Organization(Guid id, string legalName) => new()
    {
        ContactId = id,
        ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
        OrganizationDetails = new() { LegalName = legalName },
        NormalizedName = legalName.ToUpperInvariant(),
        Type = ContactType.Organization,
    };

    private static InsurancePolicy Policy(Guid id, string name) => new()
    {
        InsurancePolicyId = id,
        Name = name,
        Type = ContextInsurancePolicyType.Home,
        CreatedAtUtc = DateTime.UtcNow,
    };

    /// <summary>The migration immediately before the one under test.</summary>
    private const string PrecedingMigration = "_DropInsurancePolicyFiles";

    private static Task InsertLegacyPolicyAsync(
        OdysseyContext context, Guid policyId, string name, Guid insurerId, Guid? accountId)
    {
        var account = accountId is null ? "NULL" : $"'{accountId}'";
        var createdAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
        var sql = string.Format(
            CultureInfo.InvariantCulture,
            """
            INSERT INTO `InsurancePolicies`
                (`InsurancePolicyId`, `Name`, `Type`, `InsurerId`, `InsuredAccountId`, `CreatedAtUtc`)
            VALUES ('{0}', '{1}', 1, '{2}', {3}, '{4}');
            """,
            policyId, name, insurerId, account, createdAt);
        return context.Database.ExecuteSqlRawAsync(sql);
    }

    private static async Task<bool> ColumnExistsAsync(OdysseyContext context, string table, string column)
    {
        var count = await MigrationSeam.CountAsync(context, string.Format(
            CultureInfo.InvariantCulture,
            """
            SELECT COUNT(*) FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{0}' AND COLUMN_NAME = '{1}'
            """,
            table, column));
        return count > 0;
    }

    /// <summary>A database with no schema at all, for the tests that migrate up in two steps.</summary>
    private async Task<string> CreateEmptyAsync()
    {
        await DropAsync();

        await using var admin = new OdysseyContext(OptionsFor(fixture.OdysseyConnectionString));
        await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
        return fixture.ConnectionStringFor(Database);
    }

    private async Task<string> MigratedSchemaAsync()
    {
        await DropAsync();

        await using (var admin = new OdysseyContext(OptionsFor(fixture.OdysseyConnectionString)))
        {
            await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
        }

        var connectionString = fixture.ConnectionStringFor(Database);
        await using var context = new OdysseyContext(OptionsFor(connectionString));
        await context.Database.MigrateAsync();

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
