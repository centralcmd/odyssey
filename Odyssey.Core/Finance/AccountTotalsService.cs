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
public class AccountTotalsService(OdysseyContext context, CurrencyConversionService conversionService, TimeProvider? injectedTimeProvider = null)
{
    private readonly TimeProvider timeProvider = injectedTimeProvider ?? TimeProvider.System;

    public async Task<AccountTotals> ComputeAsync(string mainCurrencyCode, CancellationToken cancellationToken = default)
    {
        var main = CurrencyValidationService.Normalize(mainCurrencyCode);

        // Active = not archived (closed accounts still count), matching the Accounts page aggregation.
        var accounts = await context.Accounts
            .Where(account => account.Archived == null)
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
            .Where(transaction => accountIds.Contains(transaction.AccountId))
            .GroupBy(transaction => transaction.AccountId)
            .Select(group => new { AccountId = group.Key, Balance = group.Sum(transaction => transaction.Amount) })
            .ToDictionaryAsync(value => value.AccountId, value => value.Balance, cancellationToken);

        // Current estimated value per account (latest entry on or before now), in one grouped query.
        // An estimate is always in the account currency, so it converts exactly like the balance.
        var currentEstimates = await GetCurrentEstimateValuesAsync(accountIds, cancellationToken);

        // Latest rate for each distinct source currency → main currency, in one query.
        var latestRates = await conversionService.GetLatestRatesToAsync(
            main, accounts.Select(account => account.CurrencyCode));

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
    /// Resolves the currently-effective estimated value (latest <c>EffectiveFrom</c> on or before
    /// now, tie-broken by greatest <c>CreatedAtUtc</c>) for each of the given accounts that has one.
    /// </summary>
    private async Task<Dictionary<Guid, decimal>> GetCurrentEstimateValuesAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken = default)
    {
        if (accountIds.Count == 0)
            return [];

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var estimates = await context.AccountEstimates
            .AsNoTracking()
            .Where(e => accountIds.Contains(e.AccountId) && e.EffectiveFrom <= now)
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
