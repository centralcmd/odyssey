using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using ContextContractPartyRole = Odyssey.Context.ContractPartyRole;

namespace Odyssey.Core.Finance;

/// <summary>
/// <see cref="IContactReferenceGuard"/> over <see cref="OdysseyContext"/>. Uses set-based
/// <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> so the reference cleanup is a handful of statements rather
/// than materialising rows.
/// </summary>
/// <remarks>
/// The link detach is the deliberate exception: it uses tracked <c>RemoveRange</c>, because
/// <c>ExecuteDeleteAsync</c> lives in <c>EntityFrameworkCore.Relational</c> and <b>throws</b> on the
/// InMemory provider — the tier the application-code cascade exists to serve — and because it has to
/// compose into the caller's transaction rather than saving itself.
/// </remarks>
public sealed class ContactReferenceGuard(OdysseyContext context) : IContactReferenceGuard
{
    /// <summary>
    /// The one contract party role that blocks a contact delete. Named once so the blocker query, the
    /// defence-in-depth probe, the cascade exclusion and the detach plan cannot drift apart — the four
    /// places issue #157 §5.5 requires to agree.
    /// </summary>
    private const ContextContractPartyRole BlockingRole = ContextContractPartyRole.Beneficiary;

    public async Task<ContactDeleteBlockers> GetDeleteBlockersAsync(
        Guid contactId, CancellationToken cancellationToken = default)
    {
        // Three probes over the (ContactId) indexes, projecting the policy id and its name in one pass
        // each — the caller needs both the per-kind counts and the policy list, and re-querying the
        // policies afterwards would cost a fourth round trip for data these already carry.
        var insurers = await context.InsurancePolicyInsurers
            .Where(link => link.ContactId == contactId)
            .Select(link => new { link.InsurancePolicyId, Name = link.InsurancePolicy!.Name })
            .ToListAsync(cancellationToken);

        var insuredContacts = await context.InsurancePolicyInsuredContacts
            .Where(link => link.ContactId == contactId)
            .Select(link => new { link.InsurancePolicyId, Name = link.InsurancePolicy!.Name })
            .ToListAsync(cancellationToken);

        var beneficiaries = await context.InsurancePolicyBeneficiaries
            .Where(link => link.ContactId == contactId)
            .Select(link => new { link.InsurancePolicyId, Name = link.InsurancePolicy!.Name })
            .ToListAsync(cancellationToken);

        // The fourth probe, over (ContactId) filtered by the one blocking role. Every other role still
        // cascades away silently, exactly as it did (issue #157 AC 14).
        var contractBeneficiaries = await context.ContractParties
            .Where(party => party.ContactId == contactId && party.Role == BlockingRole)
            .Select(party => new { party.ContractId, Name = party.Contract!.Name })
            .ToListAsync(cancellationToken);

        var kinds = new List<InsuranceLinkKindCount>();
        if (insurers.Count > 0)
            kinds.Add(new InsuranceLinkKindCount { Kind = InsuranceLinkKind.Insurer, Count = insurers.Count });
        if (insuredContacts.Count > 0)
            kinds.Add(new InsuranceLinkKindCount { Kind = InsuranceLinkKind.InsuredContact, Count = insuredContacts.Count });
        if (beneficiaries.Count > 0)
            kinds.Add(new InsuranceLinkKindCount { Kind = InsuranceLinkKind.Beneficiary, Count = beneficiaries.Count });

        if (kinds.Count == 0 && contractBeneficiaries.Count == 0)
        {
            return ContactDeleteBlockers.None;
        }

        var policies = new Dictionary<Guid, BlockingInsurancePolicy>();
        void Record(Guid policyId, string name, InsuranceLinkKind kind)
        {
            if (!policies.TryGetValue(policyId, out var entry))
            {
                entry = new BlockingInsurancePolicy { InsurancePolicyId = policyId, Name = name };
                policies[policyId] = entry;
            }

            if (!entry.Kinds.Contains(kind))
            {
                entry.Kinds.Add(kind);
            }
        }

        foreach (var link in insurers) Record(link.InsurancePolicyId, link.Name, InsuranceLinkKind.Insurer);
        foreach (var link in insuredContacts) Record(link.InsurancePolicyId, link.Name, InsuranceLinkKind.InsuredContact);
        foreach (var link in beneficiaries) Record(link.InsurancePolicyId, link.Name, InsuranceLinkKind.Beneficiary);

        // Distinct CONTRACTS, while the count below stays a count of ROWS: a contract could name the
        // same contact as a beneficiary twice over only via distinct targets, but the two numbers are
        // deliberately different things and neither is derived from the other.
        var contracts = contractBeneficiaries
            .GroupBy(party => party.ContractId)
            .Select(group => new BlockingContractBeneficiary
            {
                ContractId = group.Key,
                ContractName = group.First().Name,
            })
            .OrderBy(entry => entry.ContractName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new ContactDeleteBlockers(
            kinds,
            [.. policies.Values.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)],
            contracts,
            contractBeneficiaries.Count);
    }

    public async Task<bool> IsReferencedByRestrictedLinkAsync(Guid contactId, CancellationToken cancellationToken = default) =>
        await context.InsurancePolicyInsurers.AnyAsync(link => link.ContactId == contactId, cancellationToken)
        || await context.InsurancePolicyInsuredContacts.AnyAsync(link => link.ContactId == contactId, cancellationToken)
        || await context.InsurancePolicyBeneficiaries.AnyAsync(link => link.ContactId == contactId, cancellationToken)
        // No FK backstop behind this one — the ContractParty -> Contact key stays CASCADE for every
        // role — so a direct, non-HTTP caller is refused here or not at all (issue #157 §5.5).
        || await context.ContractParties.AnyAsync(
            party => party.ContactId == contactId && party.Role == BlockingRole, cancellationToken);

    public async Task ClearAndCascadeReferencesAsync(Guid contactId, CancellationToken cancellationToken = default)
    {
        // SetNull links (previously ON DELETE SET NULL FKs).
        await context.Transactions
            .Where(t => t.ContactId == contactId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ContactId, (Guid?)null), cancellationToken);

        await context.Subscriptions
            .Where(s => s.ContactId == contactId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ContactId, (Guid?)null), cancellationToken);

        await context.Accounts
            .Where(a => a.CustodianId == contactId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.CustodianId, (Guid?)null), cancellationToken);

        await context.AccountFiles
            .Where(f => f.IssuedBy == contactId)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.IssuedBy, (Guid?)null), cancellationToken);

        await context.ContractFiles
            .Where(f => f.IssuedBy == contactId)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.IssuedBy, (Guid?)null), cancellationToken);

        await context.FileAnalysisCandidateTransactions
            .Where(c => c.MatchedContactId == contactId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.MatchedContactId, (Guid?)null), cancellationToken);

        // Cascade link (previously ON DELETE CASCADE FK): remove the contract-party rows so the XOR
        // "exactly one target" check constraint stays satisfied.
        //
        // EXCLUDING the Beneficiary role (issue #157 §5.5). Those rows are a blocker, so either the
        // delete was refused before reaching here, or the caller asked for the detach valve and
        // StageLinkDetach has already staged the very same rows. Deleting them here as well puts a
        // set-based DELETE and a tracked RemoveRange over one row inside one SaveChangesAsync, which
        // EF answers with DbUpdateConcurrencyException — breaking the ORDINARY success path.
        await context.ContractParties
            .Where(p => p.ContactId == contactId && p.Role != BlockingRole)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<ContactLinkDetachPlan> ReadLinkDetachPlanAsync(
        Guid contactId, CancellationToken cancellationToken = default)
    {
        var insurers = await context.InsurancePolicyInsurers
            .Where(link => link.ContactId == contactId).ToListAsync(cancellationToken);
        var insuredContacts = await context.InsurancePolicyInsuredContacts
            .Where(link => link.ContactId == contactId).ToListAsync(cancellationToken);
        var beneficiaries = await context.InsurancePolicyBeneficiaries
            .Where(link => link.ContactId == contactId).ToListAsync(cancellationToken);
        var contractBeneficiaries = await context.ContractParties
            .Where(party => party.ContactId == contactId && party.Role == BlockingRole)
            .ToListAsync(cancellationToken);

        return new ContactLinkDetachPlan
        {
            Insurers = insurers,
            InsuredContacts = insuredContacts,
            Beneficiaries = beneficiaries,
            ContractBeneficiaries = contractBeneficiaries,
        };
    }

    public DetachedInsuranceLinks StageLinkDetach(ContactLinkDetachPlan plan)
    {
        context.InsurancePolicyInsurers.RemoveRange(plan.Insurers);
        context.InsurancePolicyInsuredContacts.RemoveRange(plan.InsuredContacts);
        context.InsurancePolicyBeneficiaries.RemoveRange(plan.Beneficiaries);
        context.ContractParties.RemoveRange(plan.ContractBeneficiaries);

        var kinds = new List<InsuranceLinkKindCount>();
        if (plan.Insurers.Count > 0)
            kinds.Add(new InsuranceLinkKindCount { Kind = InsuranceLinkKind.Insurer, Count = plan.Insurers.Count });
        if (plan.InsuredContacts.Count > 0)
            kinds.Add(new InsuranceLinkKindCount { Kind = InsuranceLinkKind.InsuredContact, Count = plan.InsuredContacts.Count });
        if (plan.Beneficiaries.Count > 0)
            kinds.Add(new InsuranceLinkKindCount { Kind = InsuranceLinkKind.Beneficiary, Count = plan.Beneficiaries.Count });

        var affectedPolicies = plan.Insurers.Select(l => l.InsurancePolicyId)
            .Concat(plan.InsuredContacts.Select(l => l.InsurancePolicyId))
            .Concat(plan.Beneficiaries.Select(l => l.InsurancePolicyId))
            .Distinct()
            .OrderBy(id => id)
            .ToList();

        return new DetachedInsuranceLinks
        {
            Kinds = kinds,
            TotalLinks = plan.TotalInsuranceLinks,
            AffectedPolicyIds = affectedPolicies,
            ContractBeneficiaryLinks = plan.ContractBeneficiaries.Count,
            AffectedContractIds = [.. plan.ContractBeneficiaries.Select(p => p.ContractId).Distinct().OrderBy(id => id)],
        };
    }
}
