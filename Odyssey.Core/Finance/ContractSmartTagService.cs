using Odyssey.Core;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Mapster;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Core.Finance;

/// <summary>
/// Business logic for contract "smart tags" (issue #166): a curated set of <see cref="TransactionTag"/>
/// entities associated with a <see cref="Contract"/> to drive a persistent, per-contract saved
/// transaction filter. Associations are managed individually (add/remove) rather than replaced in
/// bulk, keeping the API idempotent. The returned shape is the tag itself
/// (<see cref="ExistingTransactionTag"/>); the join row carries no client-visible data beyond the
/// pairing.
///
/// <para>
/// <strong>Nothing here joins contracts to transactions.</strong> The filter resolves client-side by
/// composing this endpoint with <c>GET /api/transactions?tagIds=…</c>, which keeps transaction data
/// behind <c>transactions.read</c> structurally rather than by a check (§3.3, §7.3).
/// </para>
/// </summary>
public class ContractSmartTagService
{
    private readonly OdysseyContext context;
    private readonly IContractLimitsLookup limits;
    private readonly TimeProvider timeProvider;

    // The cap is admin-editable, and this service is the CONTROL: a client-side pre-check is a
    // convenience, never the gate, so an over-cap add is rejected here whatever the browser believed.
    public ContractSmartTagService(
        OdysseyContext context, IContractLimitsLookup limits, TimeProvider? timeProvider = null)
    {
        this.context = context;
        this.limits = limits;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Returns the tags currently associated with the contract as smart tags (oldest association
    /// first), or <c>null</c> if the contract does not exist.
    /// </summary>
    public async Task<IList<ExistingTransactionTag>?> GetSmartTags(
        Guid contractId, CancellationToken cancellationToken = default)
    {
        var contractExists = await context.Contracts
            .AnyAsync(c => c.ContractId == contractId, cancellationToken);
        if (!contractExists)
            return null;

        var tags = await context.ContractSmartTags
            .Where(smartTag => smartTag.ContractId == contractId)
            .OrderBy(smartTag => smartTag.AddedAt)
            .Select(smartTag => smartTag.TransactionTag)
            .ToListAsync(cancellationToken);

        return tags.Adapt<List<ExistingTransactionTag>>();
    }

    /// <summary>
    /// Associates an existing, non-archived tag with the contract as a smart tag and returns the added
    /// tag.
    /// </summary>
    /// <exception cref="DomainNotFoundException">The contract or the tag does not exist.</exception>
    /// <exception cref="DomainConflictException">The tag is already a smart tag for the contract.</exception>
    /// <exception cref="DomainUnprocessableException">
    /// The tag is archived, or the contract is at the smart-tag limit.
    /// </exception>
    public async Task<ExistingTransactionTag> AddSmartTag(
        Guid contractId, Guid tagId, CancellationToken cancellationToken = default)
    {
        var contractExists = await context.Contracts
            .AnyAsync(c => c.ContractId == contractId, cancellationToken);
        if (!contractExists)
            throw new DomainNotFoundException($"Contract with ID {contractId} was not found.");

        var tag = await context.TransactionTags
                .FirstOrDefaultAsync(t => t.TransactionTagId == tagId, cancellationToken)
            ?? throw new DomainNotFoundException($"Transaction tag with ID {tagId} was not found.");

        // An archived tag is retired vocabulary: linking one would surface a filter the user cannot
        // manage from the tags page. A tag archived AFTER being linked keeps its row — retroactively
        // dropping links would silently discard configuration.
        if (tag.Archived is not null)
            throw new DomainUnprocessableException(
                $"Tag '{tag.Name}' is archived and cannot be added as a smart tag.");

        var alreadyAssociated = await context.ContractSmartTags
            .AnyAsync(
                smartTag => smartTag.ContractId == contractId && smartTag.TransactionTagId == tagId,
                cancellationToken);
        if (alreadyAssociated)
            throw new DomainConflictException(
                $"Tag '{tag.Name}' is already a smart tag for this contract.");

        var maxSmartTags = (await limits.GetAsync(cancellationToken)).MaxSmartTagsPerContract;
        var smartTagCount = await context.ContractSmartTags
            .CountAsync(smartTag => smartTag.ContractId == contractId, cancellationToken);
        if (smartTagCount >= maxSmartTags)
            throw new DomainUnprocessableException(
                $"A contract may have at most {maxSmartTags} smart tags.");

        context.ContractSmartTags.Add(new ContractSmartTag
        {
            ContractId = contractId,
            TransactionTagId = tagId,
            AddedAt = timeProvider.GetUtcNow().UtcDateTime,
        });
        await context.SaveChangesAsync(cancellationToken);

        return tag.Adapt<ExistingTransactionTag>();
    }

    /// <summary>
    /// Removes a smart-tag association from the contract. Returns <c>false</c> if the association does
    /// not exist.
    /// </summary>
    public async Task<bool> RemoveSmartTag(
        Guid contractId, Guid tagId, CancellationToken cancellationToken = default)
    {
        var smartTag = await context.ContractSmartTags
            .FirstOrDefaultAsync(
                s => s.ContractId == contractId && s.TransactionTagId == tagId, cancellationToken);
        if (smartTag is null)
            return false;

        context.ContractSmartTags.Remove(smartTag);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
