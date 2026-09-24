using Mapster;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Dtos.Finance;

namespace Odyssey.Core.Finance;

/// <summary>
/// Business logic for time-versioned property estimates (issue #167) — a sibling of
/// <see cref="AccountEstimateService"/> over a sibling table, behaviourally identical to it.
///
/// <para>
/// <b>Where each rule lives</b> (§4). The two scalar checks — a non-negative value and a currency equal
/// to the property's — are duplicated here, as <c>AccountSmartTagService</c>/<c>ContractSmartTagService</c>
/// duplicate theirs: a divergence fails loudly on the request that hits it. The two query-shaped rules
/// — the duplicate-<c>EffectiveFrom</c> conflict and the current-as-of resolution — are asked of
/// <see cref="EstimateEffectiveDating"/>, because a divergence there fails silently.
/// </para>
///
/// <para>
/// The owner always comes from the route: no request body carries a property id, and update/delete
/// resolve the row by <c>(estimateId, propertyId)</c> together, so an estimate id belonging to another
/// property is a <c>404</c> that mutates nothing.
/// </para>
/// </summary>
public class PropertyEstimateService
{
    private readonly OdysseyContext context;
    private readonly TimeProvider timeProvider;

    public PropertyEstimateService(OdysseyContext context, TimeProvider? timeProvider = null)
    {
        this.context = context;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Returns the full estimate history for a property (newest <c>EffectiveFrom</c> first), or
    /// <c>null</c> if the property does not exist. Optionally cut off at an as-of date.
    /// </summary>
    public async Task<IList<ExistingPropertyEstimate>?> GetHistory(
        Guid propertyId, DateTime? asOf = null, CancellationToken cancellationToken = default)
    {
        if (!await PropertyExists(propertyId, cancellationToken))
            return null;

        var query = context.PropertyEstimates.AsNoTracking().Where(estimate => estimate.PropertyId == propertyId);

        if (asOf is not null)
        {
            var cutoff = DateTimeNormalization.NormalizeToUtc(asOf.Value);
            query = query.Where(estimate => estimate.EffectiveFrom <= cutoff);
        }

        var estimates = await query.InSupersessionOrder().ToListAsync(cancellationToken);
        return estimates.Adapt<List<ExistingPropertyEstimate>>();
    }

    /// <summary>
    /// Returns the estimate in force as of <paramref name="asOf"/> (default now), or <c>null</c> when the
    /// property has none in force. The caller distinguishes an unknown property with
    /// <see cref="PropertyExists"/> first — "no estimate" is a <c>200</c> with a null body.
    /// </summary>
    public async Task<CurrentPropertyEstimate?> GetCurrent(
        Guid propertyId, DateTime? asOf = null, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeNormalization.NormalizeToUtc(asOf ?? timeProvider.GetUtcNow().UtcDateTime);

        var estimate = await EstimateEffectiveDating.ResolveCurrentAsync(
            context.PropertyEstimates.AsNoTracking().Where(e => e.PropertyId == propertyId),
            cutoff,
            cancellationToken);

        return estimate?.Adapt<CurrentPropertyEstimate>();
    }

    public Task<bool> PropertyExists(Guid propertyId, CancellationToken cancellationToken = default) =>
        context.Properties.AnyAsync(p => p.PropertyId == propertyId, cancellationToken);

    /// <summary>Records a new estimate on a property.</summary>
    /// <exception cref="DomainNotFoundException">The property does not exist.</exception>
    /// <exception cref="DomainValidationException">The value is negative, or the currency is unsupported or not the property's.</exception>
    /// <exception cref="DomainConflictException">An estimate with the same effective date exists.</exception>
    public async Task<ExistingPropertyEstimate> Create(
        Guid propertyId, NewPropertyEstimate newEstimate, CancellationToken cancellationToken = default)
    {
        var property = await context.Properties.FirstOrDefaultAsync(p => p.PropertyId == propertyId, cancellationToken)
            ?? throw new DomainNotFoundException($"Property ID {propertyId} not found.");

        var estimate = new PropertyEstimate
        {
            PropertyId = propertyId,
            CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
        };

        await ApplyAndValidate(estimate, newEstimate, property, excludeEstimateId: null, cancellationToken);

        context.PropertyEstimates.Add(estimate);
        await context.SaveChangesAsync(cancellationToken);

        return estimate.Adapt<ExistingPropertyEstimate>();
    }

    /// <summary>
    /// Updates an estimate. Returns <c>false</c> if it is not attached to the given property; otherwise
    /// applies the same validation as <see cref="Create"/>.
    /// </summary>
    public async Task<bool> Update(
        Guid propertyId, Guid estimateId, NewPropertyEstimate putEstimate, CancellationToken cancellationToken = default)
    {
        var estimate = await context.PropertyEstimates
            .FirstOrDefaultAsync(e => e.PropertyEstimateId == estimateId && e.PropertyId == propertyId, cancellationToken);
        if (estimate is null)
            return false;

        var property = await context.Properties.FirstOrDefaultAsync(p => p.PropertyId == propertyId, cancellationToken);
        if (property is null)
            return false;

        await ApplyAndValidate(estimate, putEstimate, property, excludeEstimateId: estimateId, cancellationToken);

        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Deletes an estimate. Returns <c>false</c> if it is not attached to the given property.</summary>
    public async Task<bool> Delete(Guid propertyId, Guid estimateId, CancellationToken cancellationToken = default)
    {
        var estimate = await context.PropertyEstimates
            .FirstOrDefaultAsync(e => e.PropertyEstimateId == estimateId && e.PropertyId == propertyId, cancellationToken);
        if (estimate is null)
            return false;

        context.PropertyEstimates.Remove(estimate);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task ApplyAndValidate(
        PropertyEstimate estimate, NewPropertyEstimate source, Property property, Guid? excludeEstimateId,
        CancellationToken cancellationToken)
    {
        if (source.Value < 0m)
            throw new DomainValidationException("An estimated value must be greater than or equal to zero.");

        // The currency defaults to the property currency and must match it when supplied.
        var requested = string.IsNullOrWhiteSpace(source.CurrencyCode) ? property.CurrencyCode : source.CurrencyCode;
        var normalized = CurrencyValidationService.Normalize(requested);
        await CurrencyValidationService.EnsureSupportedAndActive(
            context, normalized, nameof(source.CurrencyCode), cancellationToken);

        var propertyCurrency = CurrencyValidationService.Normalize(property.CurrencyCode);
        if (!string.Equals(normalized, propertyCurrency, StringComparison.Ordinal))
            throw new DomainValidationException(
                $"An estimate must be recorded in the property currency ('{propertyCurrency}'), not '{normalized}'.");

        var effectiveFrom = DateTimeNormalization.NormalizeToUtc(source.EffectiveFrom);

        var duplicateExists = await EstimateEffectiveDating.HasConflictAsync(
            context.PropertyEstimates.Where(existing =>
                existing.PropertyId == property.PropertyId
                && (excludeEstimateId == null || existing.PropertyEstimateId != excludeEstimateId)),
            effectiveFrom,
            cancellationToken);
        if (duplicateExists)
            throw new DomainConflictException(
                $"An estimate effective from {effectiveFrom:yyyy-MM-dd} already exists for this property.");

        estimate.Value = source.Value;
        estimate.CurrencyCode = normalized;
        estimate.EffectiveFrom = effectiveFrom;
        estimate.Note = source.Note;
    }
}
