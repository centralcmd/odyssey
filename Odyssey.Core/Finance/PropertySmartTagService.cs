using Odyssey.Core;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Mapster;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Core.Finance;

/// <summary>
/// Business logic for property "smart tags" (issue #167): a curated set of <see cref="TransactionTag"/>
/// entities associated with a <see cref="Property"/> to drive a persistent, per-property saved
/// transaction filter. Associations are managed individually (add/remove) rather than replaced in
/// bulk, keeping the API idempotent. The returned shape is the tag itself
/// (<see cref="ExistingTransactionTag"/>); the join row carries no client-visible data beyond the
/// pairing.
///
/// <para>
/// <strong>Nothing here joins properties to transactions.</strong> The filter resolves client-side by
/// composing this endpoint with <c>GET /api/transactions?tagIds=…</c>, which keeps transaction data
/// behind <c>transactions.read</c> structurally rather than by a check.
/// </para>
///
/// <para>
/// A sibling of <see cref="AccountSmartTagService"/> and <see cref="ContractSmartTagService"/> sharing
/// no code with either, as they share none with each other: every rule here is scalar, so a divergence
/// fails loudly on the request that hits it (issue #167 §4).
/// </para>
/// </summary>
public class PropertySmartTagService
{
    private readonly OdysseyContext context;
    private readonly IPropertyLimitsLookup limits;
    private readonly TimeProvider timeProvider;

    // The cap is admin-editable, and this service is the CONTROL: a client-side pre-check is a
    // convenience, never the gate, so an over-cap add is rejected here whatever the browser believed.
    public PropertySmartTagService(
        OdysseyContext context, IPropertyLimitsLookup limits, TimeProvider? timeProvider = null)
    {
        this.context = context;
        this.limits = limits;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Returns the tags currently associated with the property as smart tags (oldest association
    /// first), or <c>null</c> if the property does not exist.
    /// </summary>
    public async Task<IList<ExistingTransactionTag>?> GetSmartTags(
        Guid propertyId, CancellationToken cancellationToken = default)
    {
        var propertyExists = await context.Properties
            .AnyAsync(c => c.PropertyId == propertyId, cancellationToken);
        if (!propertyExists)
            return null;

        var tags = await context.PropertySmartTags
            .Where(smartTag => smartTag.PropertyId == propertyId)
            .OrderBy(smartTag => smartTag.AddedAt)
            .Select(smartTag => smartTag.TransactionTag)
            .ToListAsync(cancellationToken);

        return tags.Adapt<List<ExistingTransactionTag>>();
    }

    /// <summary>
    /// Associates an existing, non-archived tag with the property as a smart tag and returns the added
    /// tag.
    /// </summary>
    /// <exception cref="DomainNotFoundException">The property or the tag does not exist.</exception>
    /// <exception cref="DomainConflictException">The tag is already a smart tag for the property.</exception>
    /// <exception cref="DomainUnprocessableException">
    /// The tag is archived, or the property is at the smart-tag limit.
    /// </exception>
    public async Task<ExistingTransactionTag> AddSmartTag(
        Guid propertyId, Guid tagId, CancellationToken cancellationToken = default)
    {
        var propertyExists = await context.Properties
            .AnyAsync(c => c.PropertyId == propertyId, cancellationToken);
        if (!propertyExists)
            throw new DomainNotFoundException($"Property ID {propertyId} not found.");

        var tag = await context.TransactionTags
                .FirstOrDefaultAsync(t => t.TransactionTagId == tagId, cancellationToken)
            ?? throw new DomainNotFoundException($"Transaction tag with ID {tagId} was not found.");

        // An archived tag is retired vocabulary: linking one would surface a filter the user cannot
        // manage from the tags page. A tag archived AFTER being linked keeps its row — retroactively
        // dropping links would silently discard configuration.
        if (tag.Archived is not null)
            throw new DomainUnprocessableException(
                $"Tag '{tag.Name}' is archived and cannot be added as a smart tag.");

        var alreadyAssociated = await context.PropertySmartTags
            .AnyAsync(
                smartTag => smartTag.PropertyId == propertyId && smartTag.TransactionTagId == tagId,
                cancellationToken);
        if (alreadyAssociated)
            throw new DomainConflictException(
                $"Tag '{tag.Name}' is already a smart tag for this property.");

        var maxSmartTags = (await limits.GetAsync(cancellationToken)).MaxSmartTagsPerProperty;
        var smartTagCount = await context.PropertySmartTags
            .CountAsync(smartTag => smartTag.PropertyId == propertyId, cancellationToken);
        if (smartTagCount >= maxSmartTags)
            throw new DomainUnprocessableException(
                $"A property may have at most {maxSmartTags} smart tags.");

        context.PropertySmartTags.Add(new PropertySmartTag
        {
            PropertyId = propertyId,
            TransactionTagId = tagId,
            AddedAt = timeProvider.GetUtcNow().UtcDateTime,
        });
        await context.SaveChangesAsync(cancellationToken);

        return tag.Adapt<ExistingTransactionTag>();
    }

    /// <summary>
    /// Removes a smart-tag association from the property. Returns <c>false</c> if the association does
    /// not exist.
    /// </summary>
    public async Task<bool> RemoveSmartTag(
        Guid propertyId, Guid tagId, CancellationToken cancellationToken = default)
    {
        var smartTag = await context.PropertySmartTags
            .FirstOrDefaultAsync(
                s => s.PropertyId == propertyId && s.TransactionTagId == tagId, cancellationToken);
        if (smartTag is null)
            return false;

        context.PropertySmartTags.Remove(smartTag);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
