using Odyssey.Core;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Mapster;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Core.Finance;

/// <summary>
/// Business logic for time-versioned account estimates (user-supplied values for non-transactional
/// assets such as property or vehicles). Mirrors <see cref="AccountTermService"/>: validates the
/// value and currency, and resolves the currently-effective estimate by implicit supersession (the
/// latest <c>EffectiveFrom</c> on or before a date). Unlike terms, an estimate is always a single
/// money amount in the account currency, so there is no kind/unit/billing dimension or eligibility
/// matrix — every account type may carry estimates.
/// </summary>
public class AccountEstimateService
{
    private readonly OdysseyContext context;
    private readonly TimeProvider timeProvider;

    public AccountEstimateService(OdysseyContext context, TimeProvider? timeProvider = null)
    {
        this.context = context;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Returns the full estimate history for an account (newest <c>EffectiveFrom</c> first), or
    /// <c>null</c> if the account does not exist. Optionally filtered by an as-of date.
    /// </summary>
    public async Task<IList<ExistingAccountEstimate>?> GetHistory(Guid accountId, DateTime? asOf = null, CancellationToken cancellationToken = default)
    {
        var accountExists = await context.Accounts.AnyAsync(a => a.AccountId == accountId, cancellationToken);
        if (!accountExists)
            return null;

        var query = context.AccountEstimates.AsNoTracking().Where(estimate => estimate.AccountId == accountId);

        if (asOf is not null)
        {
            var cutoff = DateTimeNormalization.NormalizeToUtc(asOf.Value);
            query = query.Where(estimate => estimate.EffectiveFrom <= cutoff);
        }

        var estimates = await query
            .OrderByDescending(estimate => estimate.EffectiveFrom)
            .ThenByDescending(estimate => estimate.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        return estimates.Adapt<List<ExistingAccountEstimate>>();
    }

    /// <summary>
    /// Returns the currently-effective estimate as of <paramref name="asOf"/> (default now), or
    /// <c>null</c> if the account does not exist or has no estimate on or before the cutoff.
    /// </summary>
    public async Task<CurrentAccountEstimate?> GetCurrent(Guid accountId, DateTime? asOf = null, CancellationToken cancellationToken = default)
    {
        var accountExists = await context.Accounts.AnyAsync(a => a.AccountId == accountId, cancellationToken);
        if (!accountExists)
            return null;

        var cutoff = DateTimeNormalization.NormalizeToUtc(asOf ?? timeProvider.GetUtcNow().UtcDateTime);

        var estimate = await context.AccountEstimates
            .AsNoTracking()
            .Where(e => e.AccountId == accountId && e.EffectiveFrom <= cutoff)
            .OrderByDescending(e => e.EffectiveFrom)
            .ThenByDescending(e => e.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return estimate?.Adapt<CurrentAccountEstimate>();
    }

    /// <summary>
    /// Creates a new estimate entry on an account.
    /// </summary>
    /// <exception cref="DomainNotFoundException">The account does not exist.</exception>
    /// <exception cref="DomainValidationException">The value is negative.</exception>
    /// <exception cref="DomainValidationException">The currency is unsupported or differs from the account currency.</exception>
    /// <exception cref="DomainConflictException">An estimate with the same effective date exists.</exception>
    public async Task<ExistingAccountEstimate> Create(Guid accountId, NewAccountEstimate newEstimate, CancellationToken cancellationToken = default)
    {
        var account = await context.Accounts.FirstOrDefaultAsync(a => a.AccountId == accountId, cancellationToken)
            ?? throw new DomainNotFoundException($"Account with ID {accountId} was not found.");

        var estimate = new AccountEstimate
        {
            AccountId = accountId,
            CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
        };

        await ApplyAndValidate(estimate, newEstimate, account, excludeEstimateId: null, cancellationToken);

        context.AccountEstimates.Add(estimate);
        await context.SaveChangesAsync(cancellationToken);

        return estimate.Adapt<ExistingAccountEstimate>();
    }

    /// <summary>
    /// Updates an existing estimate entry. Returns <c>false</c> if the estimate is not attached to the
    /// given account; otherwise applies the same validation as <see cref="Create"/>.
    /// </summary>
    public async Task<bool> Update(Guid accountId, Guid estimateId, NewAccountEstimate putEstimate, CancellationToken cancellationToken = default)
    {
        var estimate = await context.AccountEstimates
            .FirstOrDefaultAsync(e => e.AccountEstimateId == estimateId && e.AccountId == accountId, cancellationToken);
        if (estimate is null)
            return false;

        var account = await context.Accounts.FirstOrDefaultAsync(a => a.AccountId == accountId, cancellationToken);
        if (account is null)
            return false;

        await ApplyAndValidate(estimate, putEstimate, account, excludeEstimateId: estimateId, cancellationToken);

        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Deletes an estimate entry. Returns <c>false</c> if the estimate is not attached to the given account.
    /// </summary>
    public async Task<bool> Delete(Guid accountId, Guid estimateId, CancellationToken cancellationToken = default)
    {
        var estimate = await context.AccountEstimates
            .FirstOrDefaultAsync(e => e.AccountEstimateId == estimateId && e.AccountId == accountId, cancellationToken);
        if (estimate is null)
            return false;

        context.AccountEstimates.Remove(estimate);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task ApplyAndValidate(AccountEstimate estimate, NewAccountEstimate source, Account account, Guid? excludeEstimateId, CancellationToken cancellationToken = default)
    {
        if (source.Value < 0m)
            throw new DomainValidationException("An estimated value must be greater than or equal to zero.");

        // The currency defaults to the account currency and must match it when supplied — an estimate
        // is always recorded in the account's own currency.
        var requested = string.IsNullOrWhiteSpace(source.CurrencyCode) ? account.CurrencyCode : source.CurrencyCode;
        var normalized = CurrencyValidationService.Normalize(requested);
        await CurrencyValidationService.EnsureSupportedAndActive(context, normalized, nameof(source.CurrencyCode));

        var accountCurrency = CurrencyValidationService.Normalize(account.CurrencyCode);
        if (!string.Equals(normalized, accountCurrency, StringComparison.Ordinal))
            throw new DomainValidationException(
                $"An estimate must be recorded in the account currency ('{accountCurrency}'), not '{normalized}'.");

        var effectiveFrom = DateTimeNormalization.NormalizeToUtc(source.EffectiveFrom);

        var duplicateExists = await context.AccountEstimates.AnyAsync(existing =>
            existing.AccountId == account.AccountId
            && existing.EffectiveFrom == effectiveFrom
            && (excludeEstimateId == null || existing.AccountEstimateId != excludeEstimateId), cancellationToken);
        if (duplicateExists)
            throw new DomainConflictException(
                $"An estimate effective from {effectiveFrom:yyyy-MM-dd} already exists for this account.");

        estimate.Value = source.Value;
        estimate.CurrencyCode = normalized;
        estimate.EffectiveFrom = effectiveFrom;
        estimate.Note = source.Note;
    }
}
