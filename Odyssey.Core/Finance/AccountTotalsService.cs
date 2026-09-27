using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Microsoft.EntityFrameworkCore;
using AccountType = Odyssey.Context.AccountType;

namespace Odyssey.Core.Finance;

/// <summary>
/// Computes total assets, total liabilities and net worth converted into a single (main) currency.
/// Each in-term account's current balance is converted at the latest rate; accounts with no rate to
/// the main currency contribute 0 and are reported in <see cref="AccountTotals.UnconvertedAccounts"/>.
/// When the caller may see property estimates, the in-force estimate of every property held now is
/// converted the same way and added to assets as its own line (issue #214).
/// </summary>
/// <remarks>
/// <para>
/// <b>As of now, exclusively (issue #90 G7).</b> Every input is bounded at the same instant:
/// transactions with <c>TimeStamp &lt; now</c>, the in-force account or property estimate with
/// <c>EffectiveFrom &lt; now</c>, the rate with the greatest <c>AsOf &lt; now</c>, only accounts with
/// <c>Opened &lt; now</c>, and only properties held at <c>now</c> (<see cref="PropertyMembership"/>).
/// Before this the service had no upper time bound at all, so a future-dated transaction, rate or
/// account already counted towards "today's" figure.
/// </para>
/// <para>
/// <b>Property inclusion is the caller's decision, stated at every call (issue #214 §7.3).</b>
/// <c>includeProperties</c> has no default, so no call site obtains property data by omission, and
/// with it <c>false</c> no query touches <c>Properties</c> or <c>PropertyEstimates</c> at all — which
/// is what makes "claim-decided, never data-decided" structural rather than merely observed in the
/// output. Folding property value into <c>NetWorth</c> for a caller who may not see estimates, and
/// redacting only the breakdown, would let them recover it by subtraction from account data.
/// </para>
/// <para>
/// <b>Membership is the open/closed term, not <c>Archived</c> (issue #99).</b> An account contributes
/// when <c>Opened &lt; now</c> and (<c>Closed</c> is null or <c>now &lt; Closed</c>).
/// <c>NetWorthHistoryService</c> evaluates that same rule per point, so the two endpoints now agree at
/// the shared final instant structurally rather than by two predicates being kept in step by hand —
/// the same argument that produced <see cref="AccountClassification"/>.
/// </para>
/// <para>
/// The bound is <b>exclusive</b> on both sides, and that is load-bearing rather than arbitrary:
/// <c>NetWorthHistoryService</c> measures each point at its period's exclusive upper bound, and its
/// final bound is this same <c>now</c>. An inclusive rule here would count a row stamped exactly at
/// <c>now</c> that the history excludes, and issue #90 AC2 requires the two to agree exactly.
/// </para>
/// <para>
/// The main currency is validated against the <c>Currencies</c> table, so an unsupported or archived
/// code is a <c>400</c> rather than a <c>200</c> whose figures are silently unconverted. That also
/// closes the full-roster oracle: <c>?mainCurrency=ZZZ</c> used to answer <c>200</c> listing every
/// account with a currency other than <c>ZZZ</c> — which is all of them — in
/// <c>UnconvertedAccounts</c>.
/// </para>
/// </remarks>
public class AccountTotalsService(OdysseyContext context, CurrencyConversionService conversionService, TimeProvider? injectedTimeProvider = null)
{
    private readonly TimeProvider timeProvider = injectedTimeProvider ?? TimeProvider.System;

    public async Task<AccountTotals> ComputeAsync(
        string mainCurrencyCode,
        bool includeProperties,
        CancellationToken cancellationToken = default)
    {
        var main = CurrencyValidationService.Normalize(mainCurrencyCode);
        await CurrencyValidationService.EnsureSupportedAndActive(context, main, "mainCurrency", cancellationToken);

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Membership is the account's OPEN/CLOSED TERM, never its Archived flag (issue #99). An
        // account counts when it had opened by `now` and had not yet closed at it. Archiving is a
        // list-filter verb shared with photos, journal entries, tags and budgets; it is not a
        // valuation event, and keying off it meant filing an account away moved the headline figure.
        // An account outside its term contributes nothing, so it is not here — and therefore is not
        // reported as unconvertible either, which would misdescribe it as a defect.
        var accounts = await context.Accounts
            .Where(account => account.Opened < now && (account.Closed == null || now < account.Closed))
            .Select(account => new
            {
                account.AccountId,
                account.Name,
                account.CurrencyCode,
                account.AccountType,
            })
            .ToListAsync(cancellationToken);

        // Per-account balance = sum of signed transaction amounts, in one grouped query.
        var accountIds = accounts.Select(account => account.AccountId).ToList();
        var balances = await context.Transactions
            .Where(transaction => accountIds.Contains(transaction.AccountId) && transaction.TimeStamp < now)
            .GroupBy(transaction => transaction.AccountId)
            .Select(group => new { AccountId = group.Key, Balance = group.Sum(transaction => transaction.Amount) })
            .ToDictionaryAsync(value => value.AccountId, value => value.Balance, cancellationToken);

        // Current estimated value per account (latest entry on or before now), in one grouped query.
        // An estimate is always in the account currency, so it converts exactly like the balance.
        var currentEstimates = await GetCurrentEstimateValuesAsync(accountIds, now, cancellationToken);

        // Properties held now and the estimate in force for each (issue #214). Skipped entirely — no
        // query issued — when the caller may not see property estimates.
        var properties = includeProperties
            ? await LoadHeldPropertiesAsync(now, cancellationToken)
            : [];

        // Latest rate for each distinct source currency → main currency, in one query covering the
        // account and the property currencies together.
        var latestRates = await conversionService.GetLatestRatesToAsync(
            main,
            accounts.Select(account => account.CurrencyCode)
                .Concat(properties.Select(property => property.CurrencyCode)),
            now,
            cancellationToken);

        var totalAssets = 0m;
        var totalLiabilities = 0m;
        var unconverted = new List<UnconvertedAccount>();

        foreach (var account in accounts)
        {
            // Replace policy (issue #182 §9): an account contributes its current estimated value when
            // one exists, otherwise its transaction balance. The estimate is in the account currency.
            var balance = currentEstimates.TryGetValue(account.AccountId, out var estimate)
                ? estimate
                : balances.TryGetValue(account.AccountId, out var value) ? value : 0m;
            var converted = Convert(balance, account.CurrencyCode, main, latestRates);

            if (converted is null)
            {
                unconverted.Add(new UnconvertedAccount
                {
                    AccountId = account.AccountId,
                    Name = account.Name,
                    CurrencyCode = account.CurrencyCode,
                });
                continue;
            }

            if (AccountClassification.IsAsset(account.AccountType))
            {
                totalAssets += converted.Value;
            }
            else if (AccountClassification.IsLiability(account.AccountType))
            {
                // Liability balances are signed (a debt is negative), so negating the converted value
                // yields a positive liability magnitude for the normal case while letting a credit
                // balance (e.g. an overpaid credit card) reduce total liabilities instead of inflating
                // them — so it correctly raises net worth rather than lowering it.
                totalLiabilities += -converted.Value;
            }
            // AccountType.Unknown (0) is Unclassified, so it is excluded from both totals.
        }

        // Properties are an asset line of their own (issue #214 D2, D6): the gross in-force estimate,
        // never a liability. A held property with no estimate in force contributes 0 and is counted
        // as unvalued — it is not unconvertible, which would tell the reader a rate is missing.
        var propertyValue = 0m;
        var contributingProperties = 0;
        var unvaluedProperties = 0;
        var unconvertedProperties = new List<UnconvertedProperty>();

        foreach (var property in properties)
        {
            if (property.Estimate is not { } estimate)
            {
                unvaluedProperties++;
                continue;
            }

            // The estimate is always in the property's currency (PropertyService refuses a currency
            // change once estimates exist), so it converts exactly as an account balance does.
            var converted = Convert(estimate, property.CurrencyCode, main, latestRates);
            if (converted is null)
            {
                unconvertedProperties.Add(new UnconvertedProperty
                {
                    PropertyId = property.PropertyId,
                    Name = property.Name,
                    CurrencyCode = property.CurrencyCode,
                });
                continue;
            }

            propertyValue += converted.Value;
            contributingProperties++;
        }

        totalAssets += propertyValue;

        return new AccountTotals
        {
            MainCurrencyCode = main,
            TotalAssets = totalAssets,
            TotalLiabilities = totalLiabilities,
            NetWorth = totalAssets - totalLiabilities,
            UnconvertedAccounts = unconverted,
            PropertiesIncluded = includeProperties,
            PropertyValue = includeProperties ? propertyValue : null,
            ContributingPropertyCount = contributingProperties,
            UnvaluedPropertyCount = unvaluedProperties,
            UnconvertedProperties = unconvertedProperties,
            // accounts.read data, emitted only alongside properties because it serves only the
            // double-count advisory (issue #214 D9) — which means nothing when properties are excluded.
            AssetTypedAccountCount = includeProperties
                ? accounts.Count(account => account.AccountType is AccountType.Property or AccountType.Vehicle)
                : null,
        };
    }

    /// <summary>
    /// <paramref name="value"/> in the main currency at the latest rate, or <c>null</c> when there is
    /// no rate. Same currency is 1:1 with no rate row required. Never a 1:1 fallback for a foreign
    /// currency: a silent 1:1 reads as a whole figure rather than a partial one.
    /// </summary>
    private static decimal? Convert(
        decimal value,
        string currencyCode,
        string main,
        IReadOnlyDictionary<string, decimal> latestRates)
    {
        var code = CurrencyValidationService.Normalize(currencyCode);
        if (string.Equals(code, main, StringComparison.Ordinal))
        {
            return value;
        }

        return latestRates.TryGetValue(code, out var rate) ? value * rate : null;
    }

    /// <summary>
    /// The properties held at <paramref name="now"/> (<see cref="PropertyMembership"/>) with the value
    /// of the estimate in force for each — greatest <c>EffectiveFrom</c> strictly before
    /// <paramref name="now"/>, tie-broken by newest <c>CreatedAtUtc</c> — or <c>null</c> when none is.
    /// Only the id, name and currency are read: nothing else about a property may ride along.
    /// </summary>
    /// <remarks>
    /// The cutoff is <b>exclusive</b>, unlike <c>EstimateEffectiveDating.ResolveCurrentAsync</c>'s
    /// inclusive one: the history's final bound is this same <c>now</c>, and issue #90 AC2 requires the
    /// two endpoints to agree exactly at it. The ordering is shared; the cutoff is net worth's own.
    /// </remarks>
    private async Task<List<HeldProperty>> LoadHeldPropertiesAsync(DateTime now, CancellationToken cancellationToken)
    {
        var held = await context.Properties
            .AsNoTracking()
            .Where(PropertyMembership.HeldAt(now))
            .Select(property => new { property.PropertyId, property.Name, property.CurrencyCode })
            .ToListAsync(cancellationToken);

        if (held.Count == 0)
        {
            return [];
        }

        var propertyIds = held.Select(property => property.PropertyId).ToList();
        var inForce = (await context.PropertyEstimates
                .AsNoTracking()
                .Where(estimate => propertyIds.Contains(estimate.PropertyId) && estimate.EffectiveFrom < now)
                .Select(estimate => new PropertyEstimateRow(
                    estimate.PropertyId, estimate.EffectiveFrom, estimate.CreatedAtUtc, estimate.Value))
                .ToListAsync(cancellationToken))
            .GroupBy(row => row.PropertyId)
            .ToDictionary(group => group.Key, group => group.MostEffective()!.Value);

        return held
            .Select(property => new HeldProperty(
                property.PropertyId,
                property.Name,
                property.CurrencyCode,
                inForce.TryGetValue(property.PropertyId, out var value) ? value : null))
            .ToList();
    }

    private sealed record HeldProperty(Guid PropertyId, string Name, string CurrencyCode, decimal? Estimate);

    private sealed record PropertyEstimateRow(Guid PropertyId, DateTime EffectiveFrom, DateTime CreatedAtUtc, decimal Value)
        : IEffectiveDated;

    /// <summary>
    /// Resolves the currently-effective estimated value (greatest <c>EffectiveFrom</c> strictly before
    /// <paramref name="now"/>, tie-broken by greatest <c>CreatedAtUtc</c>) for each of the given
    /// accounts that has one.
    /// </summary>
    private async Task<Dictionary<Guid, decimal>> GetCurrentEstimateValuesAsync(IReadOnlyCollection<Guid> accountIds, DateTime now, CancellationToken cancellationToken = default)
    {
        if (accountIds.Count == 0)
            return [];

        var estimates = await context.AccountEstimates
            .AsNoTracking()
            .Where(e => accountIds.Contains(e.AccountId) && e.EffectiveFrom < now)
            .ToListAsync(cancellationToken);

        return estimates
            .GroupBy(e => e.AccountId)
            .Select(group => group.MostEffective()!)
            .ToDictionary(e => e.AccountId, e => e.Value);
    }

}
