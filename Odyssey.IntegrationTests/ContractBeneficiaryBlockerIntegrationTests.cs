using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Finance;
using Xunit;
using ContextContractPartyRole = Odyssey.Context.ContractPartyRole;
using ContextContractType = Odyssey.Context.ContractType;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Issue #157's contact-delete half against the real engine (AC 11–14, 21–22): a contact named as a
/// <c>Beneficiary</c> on a CONTRACT blocks its deletion exactly as a policy beneficiary does, the
/// detach valve clears both classes in one transaction, and the per-class claim check refuses a
/// caller that has not proved it may destroy a class actually present.
/// </summary>
/// <remarks>
/// None of this is observable on the fast tiers. <c>ContactReferenceGuard</c>'s cleanup is written in
/// <c>ExecuteUpdateAsync</c>/<c>ExecuteDeleteAsync</c>, which live in
/// <c>EntityFrameworkCore.Relational</c> and throw on the InMemory provider, and the EF InMemory
/// provider honours neither transactions nor the execution strategy.
///
/// <para>
/// <b>AC 13 and AC 23 are NOT here</b>, despite being contact-delete criteria: both are decided in
/// <c>ContactController</c> before the service reaches any relational-only statement, so they are
/// reachable on the fast tier — and only there do they go through the real ASP.NET Core pipeline,
/// which is what AC 13 actually asserts. They live in
/// <c>Odyssey.Api.Tests/ContractPartyRoleMatrixApiTests</c>. What this file covers of the claim check
/// is the service-level half: that the refusal happens at all, and that it names the capability.
/// </para>
///
/// <para>
/// <b>There is no FK backstop for the contract half.</b> The <c>ContractParty → Contact</c> key stays
/// <c>CASCADE</c> for every role, because the fourteen other roles should keep cascading — so unlike
/// the three insurance <c>RESTRICT</c> keys, the guard here is the ONLY enforcement rather than a
/// friendlier face on a constraint. That is what makes the direct-service test (AC 21) load-bearing.
/// </para>
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class ContractBeneficiaryBlockerIntegrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_contract_beneficiary_blocker";

    /// <summary>Every claim the detach valve can demand — the ordinary Owner/Admin caller.</summary>
    private static readonly HashSet<ContactDeleteBlockerClass> AllClasses =
        [ContactDeleteBlockerClass.InsuranceLink, ContactDeleteBlockerClass.ContractBeneficiary];

    // ── The blocker (AC 11, 14, 21) ──────────────────────────────────────────

    /// <summary>
    /// AC 11 and AC 21 — the delete is refused, and refused <b>through the service directly</b>,
    /// bypassing HTTP. That is the assertion that proves <c>IsReferencedByRestrictedLinkAsync</c> was
    /// widened rather than only the blocker query the <c>409</c> payload reads: with no constraint
    /// behind it, a direct caller is refused here or not at all.
    /// </summary>
    [SkippableFact]
    public async Task A_contact_named_as_a_contract_beneficiary_cannot_be_deleted()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var connectionString = await MigratedSchemaAsync();
        try
        {
            var contactId = Guid.NewGuid();
            var contractId = Guid.NewGuid();

            await using (var context = New(connectionString))
            {
                context.Contacts.Add(Organization(contactId, "Named Beneficiary"));
                context.Contracts.Add(Contract(contractId, "Whole-of-life cover"));
                await context.SaveChangesAsync();
                AddParty(context, contractId, contactId, ContextContractPartyRole.Beneficiary);
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
                Assert.Equal(1, await context.ContractParties.CountAsync(p => p.ContactId == contactId));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// AC 14 — the other fourteen roles are UNAFFECTED: the contact deletes without a blocker and the
    /// party row still cascades away. The rule is one role wide, and a guard that widened it to every
    /// contract party would make most contacts' counterparties undeletable.
    /// </summary>
    [SkippableTheory]
    [InlineData(ContextContractPartyRole.Insurer)]
    [InlineData(ContextContractPartyRole.Policyholder)]
    [InlineData(ContextContractPartyRole.Broker)]
    [InlineData(ContextContractPartyRole.Other)]
    public async Task A_contact_in_any_other_role_still_deletes_and_its_party_row_cascades(
        ContextContractPartyRole role)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var connectionString = await MigratedSchemaAsync();
        try
        {
            var contactId = Guid.NewGuid();
            var contractId = Guid.NewGuid();

            await using (var context = New(connectionString))
            {
                context.Contacts.Add(Organization(contactId, $"Contract {role}"));
                context.Contracts.Add(Contract(contractId, "Buildings cover"));
                await context.SaveChangesAsync();
                AddParty(context, contractId, contactId, role);
                await context.SaveChangesAsync();
            }

            await using (var context = New(connectionString))
            {
                var service = new ContactService(context, new ContactReferenceGuard(context));
                await service.Delete(contactId);
            }

            await using (var context = New(connectionString))
            {
                Assert.False(await context.Contacts.AnyAsync(c => c.ContactId == contactId));
                Assert.Empty(await context.ContractParties.Where(p => p.ContactId == contactId).ToListAsync());
                // The contract itself stands, with one fewer party.
                Assert.True(await context.Contracts.AnyAsync(c => c.ContractId == contractId));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// The blocker payload the controller shapes its <c>409</c> from: the contract is named, and the
    /// count is of link ROWS. Both halves can be present at once, and the reader must be able to tell
    /// them apart.
    /// </summary>
    [SkippableFact]
    public async Task The_blocker_payload_names_the_contract_and_counts_rows()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var connectionString = await MigratedSchemaAsync();
        try
        {
            var contactId = Guid.NewGuid();
            var contractId = Guid.NewGuid();

            await using (var context = New(connectionString))
            {
                context.Contacts.Add(Organization(contactId, "Named Beneficiary"));
                context.Contracts.Add(Contract(contractId, "Whole-of-life cover"));
                await context.SaveChangesAsync();
                AddParty(context, contractId, contactId, ContextContractPartyRole.Beneficiary);
                // A second, NON-blocking party for the same contact on the same contract. The count
                // must not pick it up: one blocking row, not two parties.
                AddParty(context, contractId, contactId, ContextContractPartyRole.Broker);
                await context.SaveChangesAsync();
            }

            await using (var context = New(connectionString))
            {
                var blockers = await new ContactReferenceGuard(context).GetDeleteBlockersAsync(contactId);

                Assert.True(blockers.Any);
                Assert.True(blockers.AnyContractBeneficiary);
                Assert.False(blockers.AnyInsurance);
                Assert.Equal(1, blockers.ContractBeneficiaryLinks);

                var named = Assert.Single(blockers.Contracts);
                Assert.Equal(contractId, named.ContractId);
                Assert.Equal("Whole-of-life cover", named.ContractName);

                Assert.Equal([ContactDeleteBlockerClass.ContractBeneficiary], blockers.Classes);
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    // ── The detach valve (AC 12, 22) ─────────────────────────────────────────

    /// <summary>
    /// AC 12 and AC 22 — the valve removes the contract-party row and the contact in one transaction,
    /// and completes <b>without a <c>DbUpdateConcurrencyException</c></b>.
    /// </summary>
    /// <remarks>
    /// That second half is the regression this test exists for, and it is an ORDINARY success path
    /// rather than a race: <c>ClearAndCascadeReferencesAsync</c> used to delete every contract-party
    /// row unconditionally, so on the detach path the same row was hit by a set-based DELETE and a
    /// tracked <c>RemoveRange</c> inside one <c>SaveChangesAsync</c>, which EF answers by throwing.
    /// Excluding <c>Beneficiary</c> from the cascade is what fixes it.
    /// </remarks>
    [SkippableFact]
    public async Task The_detach_valve_removes_the_beneficiary_row_and_the_contact_in_one_transaction()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var connectionString = await MigratedSchemaAsync();
        try
        {
            var contactId = Guid.NewGuid();
            var contractId = Guid.NewGuid();

            await using (var context = New(connectionString))
            {
                context.Contacts.Add(Organization(contactId, "Detachable Beneficiary"));
                context.Contracts.Add(Contract(contractId, "Whole-of-life cover"));
                await context.SaveChangesAsync();
                AddParty(context, contractId, contactId, ContextContractPartyRole.Beneficiary);
                // A second, cascading role on the SAME contact: the cascade and the staged detach run
                // in one save, and each must claim exactly its own rows.
                AddParty(context, contractId, contactId, ContextContractPartyRole.Broker);
                await context.SaveChangesAsync();
            }

            await using (var context = New(connectionString))
            {
                var service = new ContactService(context, new ContactReferenceGuard(context));
                var detached = await service.Delete(contactId, detachInsuranceLinks: true, AllClasses);

                Assert.NotNull(detached);
                Assert.Equal(1, detached!.ContractBeneficiaryLinks);
                Assert.Equal([contractId], detached.AffectedContractIds);
                // No insurance links were involved, and the response says so rather than implying it.
                Assert.Equal(0, detached.TotalLinks);
                Assert.Empty(detached.AffectedPolicyIds);
            }

            await using (var context = New(connectionString))
            {
                Assert.False(await context.Contacts.AnyAsync(c => c.ContactId == contactId));
                Assert.Empty(await context.ContractParties.Where(p => p.ContactId == contactId).ToListAsync());
                Assert.True(await context.Contracts.AnyAsync(c => c.ContractId == contractId));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// Both blocker classes at once: the valve clears the insurance links AND the contract
    /// beneficiary in the same transaction, and reports each separately.
    /// </summary>
    [SkippableFact]
    public async Task The_detach_valve_clears_both_classes_together()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var connectionString = await MigratedSchemaAsync();
        try
        {
            var contactId = Guid.NewGuid();
            var contractId = Guid.NewGuid();
            var policyId = Guid.NewGuid();

            await using (var context = New(connectionString))
            {
                context.Contacts.Add(Organization(contactId, "Named Everywhere"));
                context.Contracts.Add(Contract(contractId, "Whole-of-life cover"));
                context.InsurancePolicies.Add(new InsurancePolicy
                {
                    InsurancePolicyId = policyId,
                    Name = "Term life",
                    Type = Odyssey.Context.InsurancePolicyType.Life,
                    CreatedAtUtc = DateTime.UtcNow,
                });
                await context.SaveChangesAsync();

                AddParty(context, contractId, contactId, ContextContractPartyRole.Beneficiary);
                context.InsurancePolicyBeneficiaries.Add(new InsurancePolicyBeneficiary
                {
                    InsurancePolicyId = policyId,
                    ContactId = contactId,
                    CreatedAtUtc = DateTime.UtcNow,
                });
                await context.SaveChangesAsync();
            }

            await using (var context = New(connectionString))
            {
                var service = new ContactService(context, new ContactReferenceGuard(context));
                var detached = await service.Delete(contactId, detachInsuranceLinks: true, AllClasses);

                Assert.NotNull(detached);
                Assert.Equal(1, detached!.TotalLinks);
                Assert.Equal([policyId], detached.AffectedPolicyIds);
                Assert.Equal(1, detached.ContractBeneficiaryLinks);
                Assert.Equal([contractId], detached.AffectedContractIds);
            }

            await using (var context = New(connectionString))
            {
                Assert.False(await context.Contacts.AnyAsync(c => c.ContactId == contactId));
                Assert.Empty(await context.InsurancePolicyBeneficiaries.Where(l => l.ContactId == contactId).ToListAsync());
                Assert.Empty(await context.ContractParties.Where(p => p.ContactId == contactId).ToListAsync());
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    // ── The per-class claim check (AC 13) ────────────────────────────────────

    /// <summary>
    /// The service half of AC 13 — a caller holding <c>contacts.delete</c> but not
    /// <c>contracts.update</c> is refused, and the contact survives: never a silent downgrade to the
    /// refused delete.
    /// </summary>
    /// <remarks>
    /// Asserting <c>DomainForbiddenException.StatusCode</c> here proves the constant is right, NOT
    /// that the pipeline turns it into a <c>403</c> response — <c>DomainForbiddenException</c> is the
    /// first subtype to map to that status, so the wiring is worth its own assertion. That one is
    /// <c>DeleteContact_WithDetach_WithoutContractsUpdate_Returns403_AndKeepsTheContact</c> in
    /// <c>Odyssey.Api.Tests</c>, over real HTTP.
    /// </remarks>
    [SkippableFact]
    public async Task The_detach_valve_refuses_a_caller_missing_the_claim_for_a_class_present()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var connectionString = await MigratedSchemaAsync();
        try
        {
            var contactId = Guid.NewGuid();
            var contractId = Guid.NewGuid();

            await using (var context = New(connectionString))
            {
                context.Contacts.Add(Organization(contactId, "Named Beneficiary"));
                context.Contracts.Add(Contract(contractId, "Whole-of-life cover"));
                await context.SaveChangesAsync();
                AddParty(context, contractId, contactId, ContextContractPartyRole.Beneficiary);
                await context.SaveChangesAsync();
            }

            await using (var context = New(connectionString))
            {
                var service = new ContactService(context, new ContactReferenceGuard(context));

                // Insurance only — the class this contact does NOT have.
                var refusal = await Assert.ThrowsAsync<DomainForbiddenException>(() =>
                    service.Delete(contactId, detachInsuranceLinks: true,
                        new HashSet<ContactDeleteBlockerClass> { ContactDeleteBlockerClass.InsuranceLink }));

                Assert.Equal(403, refusal.StatusCode);
                // Names the capability, not the rows.
                Assert.Contains("update contracts", refusal.Message, StringComparison.OrdinalIgnoreCase);
            }

            await using (var context = New(connectionString))
            {
                Assert.True(await context.Contacts.AnyAsync(c => c.ContactId == contactId));
                Assert.Equal(1, await context.ContractParties.CountAsync(p => p.ContactId == contactId));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// The other direction of the same rule, and the reason it is per-class rather than
    /// unconditional: a contact with no contract links is <b>not</b> de-authorized by a rule about
    /// contracts. Demanding <c>contracts.update</c> from that caller would break a request that is
    /// legitimate today.
    /// </summary>
    [SkippableFact]
    public async Task A_caller_without_contracts_update_still_detaches_insurance_only_links()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var connectionString = await MigratedSchemaAsync();
        try
        {
            var contactId = Guid.NewGuid();
            var policyId = Guid.NewGuid();

            await using (var context = New(connectionString))
            {
                context.Contacts.Add(Organization(contactId, "Policy Beneficiary Only"));
                context.InsurancePolicies.Add(new InsurancePolicy
                {
                    InsurancePolicyId = policyId,
                    Name = "Term life",
                    Type = Odyssey.Context.InsurancePolicyType.Life,
                    CreatedAtUtc = DateTime.UtcNow,
                });
                await context.SaveChangesAsync();

                context.InsurancePolicyBeneficiaries.Add(new InsurancePolicyBeneficiary
                {
                    InsurancePolicyId = policyId,
                    ContactId = contactId,
                    CreatedAtUtc = DateTime.UtcNow,
                });
                await context.SaveChangesAsync();
            }

            await using (var context = New(connectionString))
            {
                var service = new ContactService(context, new ContactReferenceGuard(context));
                var detached = await service.Delete(contactId, detachInsuranceLinks: true,
                    new HashSet<ContactDeleteBlockerClass> { ContactDeleteBlockerClass.InsuranceLink });

                Assert.NotNull(detached);
                Assert.Equal(1, detached!.TotalLinks);
                Assert.Equal(0, detached.ContractBeneficiaryLinks);
            }

            await using (var context = New(connectionString))
            {
                Assert.False(await context.Contacts.AnyAsync(c => c.ContactId == contactId));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void AddParty(
        OdysseyContext context, Guid contractId, Guid contactId, ContextContractPartyRole role) =>
        context.ContractParties.Add(new ContractParty
        {
            ContractPartyId = Guid.NewGuid(),
            ContractId = contractId,
            ContactId = contactId,
            Role = role,
        });

    private static Contract Contract(Guid id, string name) => new()
    {
        ContractId = id,
        Name = name,
        // Insurance is the one type that SUGGESTS Beneficiary, so the fixture is a legal contract
        // rather than one the API itself would have refused.
        Type = ContextContractType.Insurance,
        CreatedAtUtc = DateTime.UtcNow,
    };

    private static Contact Organization(Guid id, string legalName) => new()
    {
        ContactId = id,
        ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
        OrganizationDetails = new() { LegalName = legalName },
        NormalizedName = legalName.ToUpperInvariant(),
        Type = Odyssey.Dtos.ContactType.Organization,
    };

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
