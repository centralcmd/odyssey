using Odyssey.Core;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Mapster;
using Microsoft.EntityFrameworkCore;
using ContextAccountType = Odyssey.Context.AccountType;
using ContextInterval = Odyssey.Context.Interval;
using ContextTermKind = Odyssey.Context.TermKind;
using ContextTermValueUnit = Odyssey.Context.TermValueUnit;
using DtoTermKind = Odyssey.Dtos.Finance.TermKind;

namespace Odyssey.Core.Finance;

/// <summary>
/// Business logic for time-versioned account terms (interest rates, expected returns, and fee
/// prices). Mirrors <see cref="AccountService"/>: enforces per-kind account-type eligibility and
/// value/unit/currency validation, and resolves the currently-effective value of each SERIES by
/// implicit supersession (latest <c>EffectiveFrom</c> on or before a date).
///
/// <para>
/// A series is <c>(AccountId, TermKind, LabelKey)</c>. One kind can hold several concurrently
/// in-force terms told apart by a user-authored label, and supersession happens strictly within a
/// label — a rise in the foreign ATM charge is not a change to the domestic one.
/// </para>
/// </summary>
public class TermService
{
    private readonly OdysseyContext context;
    private readonly TimeProvider timeProvider;

    public TermService(OdysseyContext context, TimeProvider? timeProvider = null)
    {
        this.context = context;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    // The eligibility matrix lives in code (not the database) so it can evolve without a migration.
    // Unknown is never permitted; Fee is permitted on every account type.
    private static readonly IReadOnlySet<ContextAccountType> InterestRateAccountTypes = new HashSet<ContextAccountType>
    {
        ContextAccountType.CheckingAccount,
        ContextAccountType.SavingsAccount,
        ContextAccountType.PensionAccount,
        ContextAccountType.CreditCard,
        ContextAccountType.Mortgage,
        ContextAccountType.StudentLoan,
        ContextAccountType.PersonalLoan,
        ContextAccountType.CarLoan,
        ContextAccountType.TaxDebt,
    };

    private static readonly IReadOnlySet<ContextAccountType> ExpectedReturnAccountTypes = new HashSet<ContextAccountType>
    {
        ContextAccountType.InvestmentAccount,
        ContextAccountType.PensionAccount,
    };

    /// <summary>
    /// Returns the full term history for an account (newest <c>EffectiveFrom</c> first), or
    /// <c>null</c> if the account does not exist. Optionally filtered by kind and/or an as-of date.
    /// </summary>
    public async Task<IList<ExistingTerm>?> GetHistory(Guid accountId, DtoTermKind? kind = null, DateTime? asOf = null, CancellationToken cancellationToken = default)
    {
        var accountExists = await context.Accounts.AnyAsync(a => a.AccountId == accountId, cancellationToken);
        if (!accountExists)
            return null;

        var query = context.Terms.AsNoTracking().Where(term => term.AccountId == accountId);

        if (kind is not null)
        {
            var contextKind = kind.Value.Adapt<ContextTermKind>();
            query = query.Where(term => term.TermKind == contextKind);
        }

        if (asOf is not null)
        {
            var cutoff = NormalizeToUtc(asOf.Value);
            query = query.Where(term => term.EffectiveFrom <= cutoff);
        }

        var terms = await query
            .OrderByDescending(term => term.EffectiveFrom)
            .ThenByDescending(term => term.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        return terms.Adapt<List<ExistingTerm>>();
    }

    /// <summary>
    /// Returns the currently-effective value of each SERIES that has at least one entry on or before
    /// <paramref name="asOf"/> (default now), or <c>null</c> if the account does not exist. One kind
    /// contributes one entry per label, so a card charging four named fees returns four.
    /// </summary>
    public async Task<IList<CurrentTerm>?> GetCurrent(Guid accountId, DateTime? asOf = null, CancellationToken cancellationToken = default)
    {
        var accountExists = await context.Accounts.AnyAsync(a => a.AccountId == accountId, cancellationToken);
        if (!accountExists)
            return null;

        var cutoff = NormalizeToUtc(asOf ?? timeProvider.GetUtcNow().UtcDateTime);

        var terms = await context.Terms
            .AsNoTracking()
            .Where(term => term.AccountId == accountId && term.EffectiveFrom <= cutoff)
            .ToListAsync(cancellationToken);

        var current = terms
            .GroupBy(term => (term.TermKind, term.LabelKey))
            .Select(group => group.MostEffective()!)
            .OrderBy(term => term.TermKind)
            .ThenBy(term => term.LabelKey, StringComparer.Ordinal)
            .ToList();

        return current.Adapt<List<CurrentTerm>>();
    }

    /// <summary>
    /// Creates a new term entry on an account.
    /// </summary>
    /// <exception cref="DomainNotFoundException">The account does not exist.</exception>
    /// <exception cref="DomainValidationException">Validation or eligibility failed.</exception>
    /// <exception cref="DomainValidationException">The currency for an amount is unsupported.</exception>
    /// <exception cref="DomainConflictException">A term in the same series with that effective date exists.</exception>
    public async Task<ExistingTerm> Create(Guid accountId, NewTerm newTerm, CancellationToken cancellationToken = default)
    {
        var account = await context.Accounts.FirstOrDefaultAsync(a => a.AccountId == accountId, cancellationToken)
            ?? throw new DomainNotFoundException($"Account with ID {accountId} was not found.");

        var term = new Term
        {
            AccountId = accountId,
            CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
        };

        await ApplyAndValidate(term, newTerm, account, excludeTermId: null, cancellationToken);

        context.Terms.Add(term);
        await context.SaveChangesAsync(cancellationToken);

        return term.Adapt<ExistingTerm>();
    }

    /// <summary>
    /// Updates an existing term entry. Returns <c>false</c> if the term is not attached to the
    /// given account; otherwise applies the same validation as <see cref="Create"/>.
    /// </summary>
    public async Task<bool> Update(Guid accountId, Guid termId, NewTerm putTerm, CancellationToken cancellationToken = default)
    {
        var term = await context.Terms
            .FirstOrDefaultAsync(t => t.TermId == termId && t.AccountId == accountId, cancellationToken);
        if (term is null)
            return false;

        var account = await context.Accounts.FirstOrDefaultAsync(a => a.AccountId == accountId, cancellationToken);
        if (account is null)
            return false;

        await ApplyAndValidate(term, putTerm, account, excludeTermId: termId, cancellationToken);

        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Deletes a term entry. Returns <c>false</c> if the term is not attached to the given account.
    /// </summary>
    public async Task<bool> Delete(Guid accountId, Guid termId, CancellationToken cancellationToken = default)
    {
        var term = await context.Terms
            .FirstOrDefaultAsync(t => t.TermId == termId && t.AccountId == accountId, cancellationToken);
        if (term is null)
            return false;

        context.Terms.Remove(term);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task ApplyAndValidate(Term term, NewTerm source, Account account, Guid? excludeTermId, CancellationToken cancellationToken = default)
    {
        // Rules 9-10 — input validation, ahead of every coherence rule below. [ApiController] model
        // validation bounds the HTTP path, but a direct (non-HTTP) caller never reaches it: without
        // this, an undefined ordinal would fall through the Mapster converter's `_ => OneTime` arm
        // and be PERSISTED as OneTime, a write-path fail-open rather than a read-path degradation.
        if (source.Interval is not null && !Enum.IsDefined(source.Interval.Value))
            throw new DomainValidationException(
                $"Interval '{(int)source.Interval.Value}' is not a recognised value.");

        if (source.IntervalCount is { } requestedCount
            && (requestedCount < TermIntervalCount.Min || requestedCount > TermIntervalCount.Max))
            throw new DomainValidationException(
                $"IntervalCount must be between {TermIntervalCount.Min} and {TermIntervalCount.Max}.");

        var kind = source.TermKind.Adapt<ContextTermKind>();
        if (kind == ContextTermKind.Unknown)
            throw new DomainValidationException("TermKind must be a recognised value.");

        if (!IsEligible(kind, account.AccountType))
            throw new DomainValidationException(
                $"Term kind '{source.TermKind}' is not permitted for accounts of type '{account.AccountType}'.");

        // The label carries a fee's taxonomy, so it is required there and refused on a rate: two
        // labelled interest rates would both be in force, and the account header, the record card and
        // the history chart each headline exactly one, with no non-arbitrary way to choose.
        var label = TermLabel.Normalize(source.Label);
        if (label is not null && label.Length > TermLabel.MaxLength)
            throw new DomainValidationException(
                $"A term label must be {TermLabel.MaxLength} characters or fewer.");

        switch (TermLabel.RuleFor(source.TermKind))
        {
            case TermLabelRule.Refused when label is not null:
                throw new DomainValidationException(
                    $"Term kind '{source.TermKind}' does not take a label.");
            case TermLabelRule.Required when label is null:
                throw new DomainValidationException(
                    $"Term kind '{source.TermKind}' requires a label naming what is charged.");
        }

        // Derived here and only here — LabelKey is on no request DTO and is never bound from one.
        var labelKey = TermLabel.Key(label);

        var unit = source.ValueUnit.Adapt<ContextTermValueUnit>();

        // Rate kinds are percentages by definition; an amount unit would store a rate as a currency
        // value, which is semantically invalid history.
        if (unit != ContextTermValueUnit.Percentage
            && (kind == ContextTermKind.InterestRate || kind == ContextTermKind.ExpectedReturn))
            throw new DomainValidationException(
                $"Term kind '{source.TermKind}' must be expressed as a percentage, not an amount.");

        var isRateKind = kind == ContextTermKind.InterestRate || kind == ContextTermKind.ExpectedReturn;

        if (source.Interval is not null && isRateKind)
            throw new DomainValidationException(
                $"Interval is not allowed for term kind '{source.TermKind}'.");

        // A rate is not billed, so a rate row must not be able to carry half a billing description.
        // The rule is KIND-based, not interval-based: a one-time fee charged on a known date is
        // precisely a case worth recording.
        if (source.AnchorDate is not null && isRateKind)
            throw new DomainValidationException(
                $"AnchorDate is not allowed for term kind '{source.TermKind}'.");

        // A count is meaningful only for a periodic unit. Stored as 1 when a periodic interval
        // arrives without one (the identity cadence), and as null — never 1 — otherwise: a
        // meaningless 1 on a one-time fee would be indistinguishable from a deliberate one, and
        // would trip this very rule on the next read-modify-write round trip.
        var interval = source.Interval?.Adapt<ContextInterval>();
        int? intervalCount;
        if (interval is not null && interval.Value.IsPeriodic())
        {
            intervalCount = source.IntervalCount ?? TermIntervalCount.Min;
        }
        else
        {
            if (source.IntervalCount is not null)
                throw new DomainValidationException(
                    "IntervalCount is only allowed for a periodic interval (Daily, Weekly, Monthly, Annually).");

            intervalCount = null;
        }

        var anchorDate = source.AnchorDate is null ? (DateTime?)null : NormalizeToUtc(source.AnchorDate.Value);

        string? currencyCode;
        if (unit == ContextTermValueUnit.Percentage)
        {
            if (source.Value < -1m || source.Value > 1m)
                throw new DomainValidationException(
                    "A percentage value must be a fraction within [-1, 1] (e.g. 0.0325 for 3.25%).");

            // Currency is meaningless for a percentage; it is always stored null.
            currencyCode = null;
        }
        else
        {
            if (source.Value < 0m)
                throw new DomainValidationException("An amount value must be greater than or equal to zero.");

            var requested = string.IsNullOrWhiteSpace(source.CurrencyCode) ? account.CurrencyCode : source.CurrencyCode;
            var normalized = CurrencyValidationService.Normalize(requested);
            await CurrencyValidationService.EnsureSupportedAndActive(context, normalized, nameof(source.CurrencyCode));
            currencyCode = normalized;
        }

        var effectiveFrom = NormalizeToUtc(source.EffectiveFrom);

        // The guard is over the SERIES key, on the folded form, so "ATM abroad", "atm abroad" and
        // "  ATM   abroad  " collide while two differently-named fees on one date do not.
        var duplicateExists = await context.Terms.AnyAsync(existing =>
            existing.AccountId == account.AccountId
            && existing.TermKind == kind
            && existing.LabelKey == labelKey
            && existing.EffectiveFrom == effectiveFrom
            && (excludeTermId == null || existing.TermId != excludeTermId), cancellationToken);
        if (duplicateExists)
            throw new DomainConflictException(label is null
                ? $"A '{source.TermKind}' term effective from {effectiveFrom:yyyy-MM-dd} already exists for this account."
                : $"'{label}' already has an entry effective from {effectiveFrom:yyyy-MM-dd} on this account.");

        term.TermKind = kind;
        term.Label = label;
        term.LabelKey = labelKey;
        term.ValueUnit = unit;
        term.Value = source.Value;
        term.CurrencyCode = currencyCode;
        term.Interval = interval;
        term.IntervalCount = intervalCount;
        term.AnchorDate = anchorDate;
        term.EffectiveFrom = effectiveFrom;
        term.Note = source.Note;
    }

    private static bool IsEligible(ContextTermKind kind, ContextAccountType accountType) => kind switch
    {
        ContextTermKind.InterestRate => InterestRateAccountTypes.Contains(accountType),
        ContextTermKind.ExpectedReturn => ExpectedReturnAccountTypes.Contains(accountType),
        ContextTermKind.Fee => true,
        _ => false,
    };

    private static DateTime NormalizeToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
