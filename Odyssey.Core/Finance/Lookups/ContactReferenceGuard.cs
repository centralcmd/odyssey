using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Dtos.Finance;

namespace Odyssey.Core.Finance;

/// <summary>
/// <see cref="IContactReferenceGuard"/> over <see cref="OdysseyContext"/>. Uses set-based
/// <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> so the reference cleanup is a handful of statements rather
/// than materialising rows.
/// </summary>
/// <remarks>
/// The insurance-link detach is the deliberate exception: it uses tracked <c>RemoveRange</c>, because
/// <c>ExecuteDeleteAsync</c> lives in <c>EntityFrameworkCore.Relational</c> and <b>throws</b> on the
/// InMemory provider — the tier the application-code cascade exists to serve — and because it has to
/// compose into the caller's transaction rather than saving itself.
/// </remarks>
public sealed class ContactReferenceGuard(OdysseyContext context) : IContactReferenceGuard
{
    public async Task<InsuranceLinkBlockers> GetInsuranceLinkBlockersAsync(
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

        var kinds = new List<InsuranceLinkKindCount>();
        if (insurers.Count > 0)
            kinds.Add(new InsuranceLinkKindCount { Kind = InsuranceLinkKind.Insurer, Count = insurers.Count });
        if (insuredContacts.Count > 0)
            kinds.Add(new InsuranceLinkKindCount { Kind = InsuranceLinkKind.InsuredContact, Count = insuredContacts.Count });
        if (beneficiaries.Count > 0)
            kinds.Add(new InsuranceLinkKindCount { Kind = InsuranceLinkKind.Beneficiary, Count = beneficiaries.Count });

        if (kinds.Count == 0)
        {
            return InsuranceLinkBlockers.None;
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

        return new InsuranceLinkBlockers(
            kinds,
            [.. policies.Values.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)]);
    }

    public async Task<bool> IsReferencedByInsuranceAsync(Guid contactId, CancellationToken cancellationToken = default) =>
        await context.InsurancePolicyInsurers.AnyAsync(link => link.ContactId == contactId, cancellationToken)
        || await context.InsurancePolicyInsuredContacts.AnyAsync(link => link.ContactId == contactId, cancellationToken)
        || await context.InsurancePolicyBeneficiaries.AnyAsync(link => link.ContactId == contactId, cancellationToken);

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

        await context.FileAnalysisCandidateTransactions
            .Where(c => c.MatchedContactId == contactId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.MatchedContactId, (Guid?)null), cancellationToken);

        // Cascade link (previously ON DELETE CASCADE FK): remove the contract-party rows so the XOR
        // "exactly one target" check constraint stays satisfied.
        await context.ContractParties
            .Where(p => p.ContactId == contactId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<DetachedInsuranceLinks> StageInsuranceLinkDetachAsync(
        Guid contactId, CancellationToken cancellationToken = default)
    {
        var insurers = await context.InsurancePolicyInsurers
            .Where(link => link.ContactId == contactId).ToListAsync(cancellationToken);
        var insuredContacts = await context.InsurancePolicyInsuredContacts
            .Where(link => link.ContactId == contactId).ToListAsync(cancellationToken);
        var beneficiaries = await context.InsurancePolicyBeneficiaries
            .Where(link => link.ContactId == contactId).ToListAsync(cancellationToken);

        context.InsurancePolicyInsurers.RemoveRange(insurers);
        context.InsurancePolicyInsuredContacts.RemoveRange(insuredContacts);
        context.InsurancePolicyBeneficiaries.RemoveRange(beneficiaries);

        var kinds = new List<InsuranceLinkKindCount>();
        if (insurers.Count > 0)
            kinds.Add(new InsuranceLinkKindCount { Kind = InsuranceLinkKind.Insurer, Count = insurers.Count });
        if (insuredContacts.Count > 0)
            kinds.Add(new InsuranceLinkKindCount { Kind = InsuranceLinkKind.InsuredContact, Count = insuredContacts.Count });
        if (beneficiaries.Count > 0)
            kinds.Add(new InsuranceLinkKindCount { Kind = InsuranceLinkKind.Beneficiary, Count = beneficiaries.Count });

        var affected = insurers.Select(l => l.InsurancePolicyId)
            .Concat(insuredContacts.Select(l => l.InsurancePolicyId))
            .Concat(beneficiaries.Select(l => l.InsurancePolicyId))
            .Distinct()
            .OrderBy(id => id)
            .ToList();

        return new DetachedInsuranceLinks
        {
            Kinds = kinds,
            TotalLinks = insurers.Count + insuredContacts.Count + beneficiaries.Count,
            AffectedPolicyIds = affected,
        };
    }
}
