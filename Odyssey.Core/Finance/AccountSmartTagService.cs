using Odyssey.Core;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Mapster;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Core.Finance;

/// <summary>
/// Business logic for account "smart tags": a curated set of <see cref="TransactionTag"/> entities
/// associated with an <see cref="Account"/> to drive a persistent, per-account saved transaction
/// filter. Associations are managed individually (add/remove) rather than replaced in bulk, keeping
/// the API idempotent. The returned shape is the tag itself (<see cref="ExistingTransactionTag"/>);
/// the join row carries no client-visible data beyond the pairing.
/// </summary>
public class AccountSmartTagService
{
    private readonly OdysseyContext context;
    private readonly IAccountLimitsLookup limits;
    private readonly TimeProvider timeProvider;

    // The cap was a `public const 20` here until issue #434 (key 15). It is now admin-editable, and
    // this service is the CONTROL: a client-side pre-check is a convenience, never the gate, so an
    // over-cap add is rejected here whatever the browser believed. Newly injected lookup — one cached
    // read per add, on a path that already performs four queries.
    public AccountSmartTagService(
        OdysseyContext context, IAccountLimitsLookup limits, TimeProvider? timeProvider = null)
    {
        this.context = context;
        this.limits = limits;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Returns the tags currently associated with the account as smart tags (oldest association
    /// first), or <c>null</c> if the account does not exist.
    /// </summary>
    public async Task<IList<ExistingTransactionTag>?> GetSmartTags(Guid accountId, CancellationToken cancellationToken = default)
    {
        var accountExists = await context.Accounts.AnyAsync(a => a.AccountId == accountId, cancellationToken);
        if (!accountExists)
            return null;

        var tags = await context.AccountSmartTags
            .Where(smartTag => smartTag.AccountId == accountId)
            .OrderBy(smartTag => smartTag.AddedAt)
            .Select(smartTag => smartTag.TransactionTag)
            .ToListAsync(cancellationToken);

        return tags.Adapt<List<ExistingTransactionTag>>();
    }

    /// <summary>
    /// Associates an existing, non-archived tag with the account as a smart tag and returns the added
    /// tag.
    /// </summary>
    /// <exception cref="DomainNotFoundException">The account or the tag does not exist.</exception>
    /// <exception cref="DomainConflictException">The tag is already a smart tag for the account.</exception>
    /// <exception cref="DomainValidationException">The tag is archived, or the account is at the smart-tag limit.</exception>
    public async Task<ExistingTransactionTag> AddSmartTag(Guid accountId, Guid tagId, CancellationToken cancellationToken = default)
    {
        var accountExists = await context.Accounts.AnyAsync(a => a.AccountId == accountId, cancellationToken);
        if (!accountExists)
            throw new DomainNotFoundException($"Account with ID {accountId} was not found.");

        var tag = await context.TransactionTags.FirstOrDefaultAsync(t => t.TransactionTagId == tagId, cancellationToken)
            ?? throw new DomainNotFoundException($"Transaction tag with ID {tagId} was not found.");

        if (tag.Archived is not null)
            throw new DomainUnprocessableException(
                $"Tag '{tag.Name}' is archived and cannot be added as a smart tag.");

        var alreadyAssociated = await context.AccountSmartTags
            .AnyAsync(smartTag => smartTag.AccountId == accountId && smartTag.TransactionTagId == tagId, cancellationToken);
        if (alreadyAssociated)
            throw new DomainConflictException(
                $"Tag '{tag.Name}' is already a smart tag for this account.");

        var maxSmartTags = (await limits.GetAsync(cancellationToken)).MaxSmartTagsPerAccount;
        var smartTagCount = await context.AccountSmartTags.CountAsync(smartTag => smartTag.AccountId == accountId, cancellationToken);
        if (smartTagCount >= maxSmartTags)
            throw new DomainUnprocessableException(
                $"An account may have at most {maxSmartTags} smart tags.");

        context.AccountSmartTags.Add(new AccountSmartTag
        {
            AccountId = accountId,
            TransactionTagId = tagId,
            AddedAt = timeProvider.GetUtcNow().UtcDateTime,
        });
        await context.SaveChangesAsync(cancellationToken);

        return tag.Adapt<ExistingTransactionTag>();
    }

    /// <summary>
    /// Removes a smart-tag association from the account. Returns <c>false</c> if the association does
    /// not exist.
    /// </summary>
    public async Task<bool> RemoveSmartTag(Guid accountId, Guid tagId, CancellationToken cancellationToken = default)
    {
        var smartTag = await context.AccountSmartTags
            .FirstOrDefaultAsync(s => s.AccountId == accountId && s.TransactionTagId == tagId, cancellationToken);
        if (smartTag is null)
            return false;

        context.AccountSmartTags.Remove(smartTag);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
