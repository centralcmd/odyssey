using Odyssey.Core;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Mapster;
using Microsoft.EntityFrameworkCore;
using ContextAccountType = Odyssey.Context.AccountType;
using ContextBillingPeriod = Odyssey.Context.BillingPeriod;
using ContextTermKind = Odyssey.Context.TermKind;
using ContextTermValueUnit = Odyssey.Context.TermValueUnit;
using DtoTermKind = Odyssey.Dtos.Finance.TermKind;

namespace Odyssey.Core.Finance;

/// <summary>
/// Business logic for time-versioned account terms (interest rates, expected returns, and fee
/// prices). Mirrors <see cref="AccountService"/>: enforces per-kind account-type eligibility and
/// value/unit/currency validation, and resolves the currently-effective value of each kind by
/// implicit supersession (latest <c>EffectiveFrom</c> on or before a date).
/// </summary>
public class AccountTermService
{
    private readonly OdysseyContext context;
    private readonly TimeProvider timeProvider;

    public AccountTermService(OdysseyContext context, TimeProvider? timeProvider = null)
    {
        this.context = context;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    // The eligibility matrix lives in code (not the database) so it can evolve without a migration.
    // Unknown is never permitted; fee kinds are permitted on every account type.
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
    public async Task<IList<ExistingAccountTerm>?> GetHistory(Guid accountId, DtoTermKind? kind = null, DateTime? asOf = null, CancellationToken cancellationToken = default)
    {
        var accountExists = await context.Accounts.AnyAsync(a => a.AccountId == accountId, cancellationToken);
        if (!accountExists)
            return null;

        var query = context.AccountTerms.AsNoTracking().Where(term => term.AccountId == accountId);

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

        return terms.Adapt<List<ExistingAccountTerm>>();
    }

    /// <summary>
    /// Returns the currently-effective value of each kind that has at least one entry on or before
    /// <paramref name="asOf"/> (default now), or <c>null</c> if the account does not exist.
    /// </summary>
    public async Task<IList<CurrentAccountTerm>?> GetCurrent(Guid accountId, DateTime? asOf = null, CancellationToken cancellationToken = default)
    {
        var accountExists = await context.Accounts.AnyAsync(a => a.AccountId == accountId, cancellationToken);
        if (!accountExists)
            return null;

        var cutoff = NormalizeToUtc(asOf ?? timeProvider.GetUtcNow().UtcDateTime);

        var terms = await context.AccountTerms
            .AsNoTracking()
            .Where(term => term.AccountId == accountId && term.EffectiveFrom <= cutoff)
            .ToListAsync(cancellationToken);

        var current = terms
            .GroupBy(term => term.TermKind)
            .Select(group => group.MostEffective()!)
            .OrderBy(term => term.TermKind)
            .ToList();

        return current.Adapt<List<CurrentAccountTerm>>();
    }

    /// <summary>
    /// Creates a new term entry on an account.
    /// </summary>
    /// <exception cref="DomainNotFoundException">The account does not exist.</exception>
    /// <exception cref="DomainValidationException">Validation or eligibility failed.</exception>
    /// <exception cref="DomainValidationException">The currency for an amount is unsupported.</exception>
    /// <exception cref="DomainConflictException">A term with the same kind and effective date exists.</exception>
    public async Task<ExistingAccountTerm> Create(Guid accountId, NewAccountTerm newTerm, CancellationToken cancellationToken = default)
    {
        var account = await context.Accounts.FirstOrDefaultAsync(a => a.AccountId == accountId, cancellationToken)
            ?? throw new DomainNotFoundException($"Account with ID {accountId} was not found.");

        var term = new AccountTerm
        {
            AccountId = accountId,
            CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
        };

        await ApplyAndValidate(term, newTerm, account, excludeTermId: null, cancellationToken);

        context.AccountTerms.Add(term);
        await context.SaveChangesAsync(cancellationToken);

        return term.Adapt<ExistingAccountTerm>();
    }

    /// <summary>
    /// Updates an existing term entry. Returns <c>false</c> if the term is not attached to the
    /// given account; otherwise applies the same validation as <see cref="Create"/>.
    /// </summary>
    public async Task<bool> Update(Guid accountId, Guid termId, NewAccountTerm putTerm, CancellationToken cancellationToken = default)
    {
        var term = await context.AccountTerms
            .FirstOrDefaultAsync(t => t.AccountTermId == termId && t.AccountId == accountId, cancellationToken);
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
        var term = await context.AccountTerms
            .FirstOrDefaultAsync(t => t.AccountTermId == termId && t.AccountId == accountId, cancellationToken);
        if (term is null)
            return false;

        context.AccountTerms.Remove(term);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task ApplyAndValidate(AccountTerm term, NewAccountTerm source, Account account, Guid? excludeTermId, CancellationToken cancellationToken = default)
    {
        var kind = source.TermKind.Adapt<ContextTermKind>();
        if (kind == ContextTermKind.Unknown)
            throw new DomainValidationException("TermKind must be a recognised value.");

        if (!IsEligible(kind, account.AccountType))
            throw new DomainValidationException(
                $"Term kind '{source.TermKind}' is not permitted for accounts of type '{account.AccountType}'.");

        var unit = source.ValueUnit.Adapt<ContextTermValueUnit>();

        // Rate kinds are percentages by definition; an amount unit would store a rate as a currency
        // value, which is semantically invalid history.
        if (unit != ContextTermValueUnit.Percentage
            && (kind == ContextTermKind.InterestRate || kind == ContextTermKind.ExpectedReturn))
            throw new DomainValidationException(
                $"Term kind '{source.TermKind}' must be expressed as a percentage, not an amount.");

        if (source.BillingPeriod is not null && (kind == ContextTermKind.InterestRate || kind == ContextTermKind.ExpectedReturn))
            throw new DomainValidationException(
                $"BillingPeriod is not allowed for term kind '{source.TermKind}'.");

        var billingPeriod = source.BillingPeriod?.Adapt<ContextBillingPeriod>();

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

        var duplicateExists = await context.AccountTerms.AnyAsync(existing =>
            existing.AccountId == account.AccountId
            && existing.TermKind == kind
            && existing.EffectiveFrom == effectiveFrom
            && (excludeTermId == null || existing.AccountTermId != excludeTermId), cancellationToken);
        if (duplicateExists)
            throw new DomainConflictException(
                $"A '{source.TermKind}' term effective from {effectiveFrom:yyyy-MM-dd} already exists for this account.");

        term.TermKind = kind;
        term.ValueUnit = unit;
        term.Value = source.Value;
        term.CurrencyCode = currencyCode;
        term.BillingPeriod = billingPeriod;
        term.EffectiveFrom = effectiveFrom;
        term.Note = source.Note;
    }

    private static bool IsEligible(ContextTermKind kind, ContextAccountType accountType) => kind switch
    {
        ContextTermKind.InterestRate => InterestRateAccountTypes.Contains(accountType),
        ContextTermKind.ExpectedReturn => ExpectedReturnAccountTypes.Contains(accountType),
        ContextTermKind.ManagementFee or ContextTermKind.ServiceFee
            or ContextTermKind.TransactionFee or ContextTermKind.OtherFee => true,
        _ => false,
    };

    private static DateTime NormalizeToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
