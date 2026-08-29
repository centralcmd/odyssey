using Microsoft.EntityFrameworkCore;
using Odyssey.Context;

namespace Odyssey.Core.Finance;

/// <summary>
/// <see cref="IContactReferenceGuard"/> over <see cref="OdysseyContext"/>. Uses set-based
/// <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> so the reference cleanup is a handful of statements rather
/// than materialising rows.
/// </summary>
public sealed class ContactReferenceGuard(OdysseyContext context) : IContactReferenceGuard
{
    public Task<bool> IsReferencedAsInsurerAsync(Guid contactId, CancellationToken cancellationToken = default) =>
        context.InsurancePolicies.AnyAsync(p => p.InsurerId == contactId, cancellationToken);

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
}
