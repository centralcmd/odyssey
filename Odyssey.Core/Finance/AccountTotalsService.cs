using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Microsoft.EntityFrameworkCore;
using AccountType = Odyssey.Context.AccountType;

namespace Odyssey.Core.Finance;

/// <summary>
/// Computes total assets, total liabilities and net worth converted into a single (main) currency.
/// Each active account's current balance is converted at the latest rate; accounts with no rate to
/// the main currency contribute 0 and are reported in <see cref="AccountTotals.UnconvertedAccounts"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>As of now, exclusively (issue #90 G7).</b> Every one of the four inputs is bounded at the same
/// instant: transactions with <c>TimeStamp &lt; now</c>, the in-force estimate with
/// <c>EffectiveFrom &lt; now</c>, the rate with the greatest <c>AsOf &lt; now</c>, and only accounts
/// with <c>Opened &lt; now</c>. Before this the service had no upper time bound at all, so a
/// future-dated transaction, rate or account already counted towards "today's" figure.
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

    public async Task<AccountTotals> ComputeAsync(string mainCurrencyCode, CancellationToken cancellationToken = default)
    {
        var main = CurrencyValidationService.Normalize(mainCurrencyCode);
        await CurrencyValidationService.EnsureSupportedAndActive(context, main, "mainCurrency", cancellationToken);

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Active = not archived (closed accounts still count), matching the Accounts page aggregation.
        // An account that has not opened yet contributes nothing, so it is not here — and therefore is
        // not reported as unconvertible either, which would misdescribe it as a defect.
        var accounts = await context.Accounts
            .Where(account => account.Archived == null && account.Opened < now)
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

        // Latest rate for each distinct source currency → main currency, in one query.
        var latestRates = await conversionService.GetLatestRatesToAsync(
            main, accounts.Select(account => account.CurrencyCode), now, cancellationToken);

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
            var code = CurrencyValidationService.Normalize(account.CurrencyCode);

            decimal? converted;
            if (string.Equals(code, main, StringComparison.Ordinal))
            {
                converted = balance; // same currency → 1:1, no rate row required.
            }
            else if (latestRates.TryGetValue(code, out var rate))
            {
                converted = balance * rate;
            }
            else
            {
                converted = null; // no rate → contributes 0, flagged below.
            }

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

            if (IsAsset(account.AccountType))
            {
                totalAssets += converted.Value;
            }
            else if (IsLiability(account.AccountType))
            {
                // Liability balances are signed (a debt is negative), so negating the converted value
                // yields a positive liability magnitude for the normal case while letting a credit
                // balance (e.g. an overpaid credit card) reduce total liabilities instead of inflating
                // them — so it correctly raises net worth rather than lowering it.
                totalLiabilities += -converted.Value;
            }
            // AccountType.Unknown (0) is excluded from totals.
        }

        return new AccountTotals
        {
            MainCurrencyCode = main,
            TotalAssets = totalAssets,
            TotalLiabilities = totalLiabilities,
            NetWorth = totalAssets - totalLiabilities,
            UnconvertedAccounts = unconverted,
        };
    }

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

    // Asset accounts: AccountType 1–8. Liability accounts: 9–15.
    private static bool IsAsset(AccountType type) => type is >= AccountType.Cash and <= AccountType.OtherAsset;

    private static bool IsLiability(AccountType type) => type is >= AccountType.CreditCard and <= AccountType.OtherLiability;
}
