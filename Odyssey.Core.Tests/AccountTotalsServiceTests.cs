using Odyssey.Core;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Xunit;
using AccountType = Odyssey.Context.AccountType;
using Odyssey.Core.Finance;

namespace Odyssey.Core.Tests;

public class AccountTotalsServiceTests
{
    private static Account NewAccount(Guid id, string name, AccountType type, string currency, DateTime? archived = null) => new()
    {
        AccountId = id,
        Name = name,
        Description = name,
        Opened = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        AccountType = type,
        CurrencyCode = currency,
        Archived = archived,
    };

    private static Transaction NewTransaction(Guid accountId, decimal amount) => new()
    {
        TransactionId = Guid.NewGuid(),
        Description = "tx",
        Amount = amount,
        TimeStamp = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        AccountId = accountId,
    };

    private static AccountEstimate NewEstimate(Guid accountId, decimal value, string currency, DateTime effectiveFrom) => new()
    {
        AccountEstimateId = Guid.NewGuid(),
        AccountId = accountId,
        Value = value,
        CurrencyCode = currency,
        EffectiveFrom = effectiveFrom,
        CreatedAtUtc = effectiveFrom,
    };

    [Fact]
    public async Task Compute_ConvertsAssetsAndLiabilities_FlagsUnconverted_ExcludesArchived()
    {
        await using var context = TestContextFactory.Create();

        var usdChecking = Guid.NewGuid();   // asset, main currency, 1:1
        var eurSavings = Guid.NewGuid();     // asset, converted via EUR->USD
        var sekCard = Guid.NewGuid();        // liability, converted via SEK->USD
        var gbpAccount = Guid.NewGuid();     // asset, no rate to USD -> unconverted
        var archivedUsd = Guid.NewGuid();    // archived -> excluded

        context.Accounts.AddRange(
            NewAccount(usdChecking, "USD Checking", AccountType.CheckingAccount, "USD"),
            NewAccount(eurSavings, "EUR Savings", AccountType.SavingsAccount, "EUR"),
            NewAccount(sekCard, "SEK Card", AccountType.CreditCard, "SEK"),
            NewAccount(gbpAccount, "GBP Brokerage", AccountType.InvestmentAccount, "GBP"),
            NewAccount(archivedUsd, "Old USD", AccountType.CheckingAccount, "USD",
                archived: new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc)));

        context.Transactions.AddRange(
            NewTransaction(usdChecking, 1000m),
            NewTransaction(eurSavings, 200m),
            NewTransaction(sekCard, -1000m),
            NewTransaction(gbpAccount, 50m),
            NewTransaction(archivedUsd, 9999m));

        await context.SaveChangesAsync();

        var rateService = new ExchangeRateService(context);
        await rateService.Create(new NewExchangeRate { FromCurrencyCode = "EUR", ToCurrencyCode = "USD", Rate = 1.1m });
        await rateService.Create(new NewExchangeRate { FromCurrencyCode = "SEK", ToCurrencyCode = "USD", Rate = 0.1m });

        var service = new AccountTotalsService(context, new CurrencyConversionService(context));
        var totals = await service.ComputeAsync("USD");

        Assert.Equal("USD", totals.MainCurrencyCode);
        // 1000 (USD 1:1) + 200*1.1 (EUR) = 1220
        Assert.Equal(1220m, totals.TotalAssets);
        // abs(-1000 * 0.1) = 100
        Assert.Equal(100m, totals.TotalLiabilities);
        Assert.Equal(1120m, totals.NetWorth);

        var unconverted = Assert.Single(totals.UnconvertedAccounts);
        Assert.Equal(gbpAccount, unconverted.AccountId);
        Assert.Equal("GBP", unconverted.CurrencyCode);
    }

    [Fact]
    public async Task Compute_NoRates_OnlyMainCurrencyAccountsCount_OthersFlagged()
    {
        await using var context = TestContextFactory.Create();

        var usd = Guid.NewGuid();
        var eur = Guid.NewGuid();

        context.Accounts.AddRange(
            NewAccount(usd, "USD", AccountType.CheckingAccount, "USD"),
            NewAccount(eur, "EUR", AccountType.SavingsAccount, "EUR"));
        context.Transactions.AddRange(
            NewTransaction(usd, 500m),
            NewTransaction(eur, 300m));
        await context.SaveChangesAsync();

        var service = new AccountTotalsService(context, new CurrencyConversionService(context));
        var totals = await service.ComputeAsync("USD");

        Assert.Equal(500m, totals.TotalAssets); // only the USD account converts (1:1)
        Assert.Equal(0m, totals.TotalLiabilities);
        Assert.Equal(500m, totals.NetWorth);
        Assert.Single(totals.UnconvertedAccounts);
        Assert.Equal(eur, totals.UnconvertedAccounts[0].AccountId);
    }

    [Fact]
    public async Task Compute_PropertyWithEstimateAndNoTransactions_ContributesEstimateToNetWorth()
    {
        await using var context = TestContextFactory.Create();

        var property = Guid.NewGuid();
        context.Accounts.Add(NewAccount(property, "House", AccountType.Property, "USD"));
        context.AccountEstimates.Add(NewEstimate(property, 350000m, "USD", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        await context.SaveChangesAsync();

        var service = new AccountTotalsService(context, new CurrencyConversionService(context));
        var totals = await service.ComputeAsync("USD");

        // No transactions (balance 0) but the estimate replaces it as the contribution.
        Assert.Equal(350000m, totals.TotalAssets);
        Assert.Equal(350000m, totals.NetWorth);
    }

    [Fact]
    public async Task Compute_EstimateReplacesTransactionBalance()
    {
        await using var context = TestContextFactory.Create();

        var property = Guid.NewGuid();
        context.Accounts.Add(NewAccount(property, "House", AccountType.Property, "USD"));
        // A stray transaction; the estimate should replace it (not add to it) per the §9 replace policy.
        context.Transactions.Add(NewTransaction(property, 5000m));
        context.AccountEstimates.Add(NewEstimate(property, 350000m, "USD", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        await context.SaveChangesAsync();

        var service = new AccountTotalsService(context, new CurrencyConversionService(context));
        var totals = await service.ComputeAsync("USD");

        Assert.Equal(350000m, totals.TotalAssets);
    }

    [Fact]
    public async Task Compute_LatestEstimateSupersedes_AndConvertsViaAccountCurrency()
    {
        await using var context = TestContextFactory.Create();

        var property = Guid.NewGuid();
        context.Accounts.Add(NewAccount(property, "House", AccountType.Property, "EUR"));
        context.AccountEstimates.AddRange(
            NewEstimate(property, 300000m, "EUR", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            NewEstimate(property, 320000m, "EUR", new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc)));
        await context.SaveChangesAsync();

        var rateService = new ExchangeRateService(context);
        await rateService.Create(new NewExchangeRate { FromCurrencyCode = "EUR", ToCurrencyCode = "USD", Rate = 1.1m });

        var service = new AccountTotalsService(context, new CurrencyConversionService(context));
        var totals = await service.ComputeAsync("USD");

        // Latest estimate (320000 EUR) * 1.1 = 352000 USD.
        Assert.Equal(352000m, totals.TotalAssets);
    }

    [Fact]
    public async Task Compute_FutureEstimateNotYetEffective_FallsBackToBalance()
    {
        await using var context = TestContextFactory.Create();

        var property = Guid.NewGuid();
        context.Accounts.Add(NewAccount(property, "House", AccountType.Property, "USD"));
        context.Transactions.Add(NewTransaction(property, 5000m));
        // Effective far in the future → not in force now → balance is used instead.
        context.AccountEstimates.Add(NewEstimate(property, 350000m, "USD", DateTime.UtcNow.AddYears(5)));
        await context.SaveChangesAsync();

        var service = new AccountTotalsService(context, new CurrencyConversionService(context));
        var totals = await service.ComputeAsync("USD");

        Assert.Equal(5000m, totals.TotalAssets);
    }

    // ── As-of-now semantics and currency validation (issue #90 G7 / §5.7) ──────────────────────
    //
    // Before G7 the service had no upper time bound at all: a transaction, rate, estimate or account
    // dated in the future already counted towards "today's" figure. Every bound is EXCLUSIVE, which
    // matters because NetWorthHistoryService measures its final point at the same instant — an
    // inclusive rule here would count a row at exactly `now` that the history excludes, and AC2
    // requires the two to agree exactly.

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private static readonly DateTime FixedNow = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private static AccountTotalsService AsOfFixedNow(OdysseyContext context) =>
        new(context, new CurrencyConversionService(context), new FixedTimeProvider(FixedNow));

    [Fact]
    public async Task Compute_IgnoresTransactionsStampedAtOrAfterNow()
    {
        await using var context = TestContextFactory.Create();

        var checking = Guid.NewGuid();
        context.Accounts.Add(NewAccount(checking, "Checking", AccountType.CheckingAccount, "USD"));
        context.Transactions.AddRange(
            new Transaction { TransactionId = Guid.NewGuid(), Description = "past", Amount = 1000m, TimeStamp = FixedNow.AddDays(-1), AccountId = checking },
            new Transaction { TransactionId = Guid.NewGuid(), Description = "exactly now", Amount = 40m, TimeStamp = FixedNow, AccountId = checking },
            new Transaction { TransactionId = Guid.NewGuid(), Description = "future", Amount = 500m, TimeStamp = FixedNow.AddDays(1), AccountId = checking });
        await context.SaveChangesAsync();

        var totals = await AsOfFixedNow(context).ComputeAsync("USD");

        // Only the past row counts: the bound is exclusive, so the row stamped exactly at now is out.
        Assert.Equal(1000m, totals.TotalAssets);
    }

    [Fact]
    public async Task Compute_IgnoresAccountsNotYetOpened_WithoutFlaggingThemUnconvertible()
    {
        await using var context = TestContextFactory.Create();

        var open = Guid.NewGuid();
        var future = Guid.NewGuid();
        context.Accounts.AddRange(
            NewAccount(open, "Open", AccountType.CheckingAccount, "USD"),
            new Account
            {
                AccountId = future,
                Name = "Opens tomorrow",
                Description = "Opens tomorrow",
                Opened = FixedNow.AddDays(1),
                AccountType = AccountType.CheckingAccount,
                // A currency with no rate to USD, so a wrongly-included account would also show up
                // as unconvertible — the reading that misdescribes a future account as a defect.
                CurrencyCode = "GBP",
            });
        context.Transactions.AddRange(
            NewTransaction(open, 200m),
            NewTransaction(future, 9999m));
        await context.SaveChangesAsync();

        var totals = await AsOfFixedNow(context).ComputeAsync("USD");

        Assert.Equal(200m, totals.TotalAssets);
        Assert.Empty(totals.UnconvertedAccounts);
    }

    [Fact]
    public async Task Compute_UsesTheRateInForceNow_NotAFutureDatedOne()
    {
        await using var context = TestContextFactory.Create();

        var savings = Guid.NewGuid();
        context.Accounts.Add(NewAccount(savings, "EUR Savings", AccountType.SavingsAccount, "EUR"));
        context.Transactions.Add(NewTransaction(savings, 100m));
        context.ExchangeRates.AddRange(
            new ExchangeRate { FromCurrencyCode = "EUR", ToCurrencyCode = "USD", Rate = 1.1m, AsOf = FixedNow.AddDays(-1), CreatedAt = FixedNow.AddDays(-1) },
            new ExchangeRate { FromCurrencyCode = "EUR", ToCurrencyCode = "USD", Rate = 9m, AsOf = FixedNow, CreatedAt = FixedNow },
            new ExchangeRate { FromCurrencyCode = "EUR", ToCurrencyCode = "USD", Rate = 5m, AsOf = FixedNow.AddDays(1), CreatedAt = FixedNow });
        await context.SaveChangesAsync();

        var totals = await AsOfFixedNow(context).ComputeAsync("USD");

        // 100 * 1.1 — neither the rate stamped exactly at now nor the future one is in force.
        Assert.Equal(110m, totals.TotalAssets);
    }

    [Fact]
    public async Task Compute_IgnoresAnEstimateEffectiveExactlyAtNow()
    {
        await using var context = TestContextFactory.Create();

        var property = Guid.NewGuid();
        context.Accounts.Add(NewAccount(property, "House", AccountType.Property, "USD"));
        context.Transactions.Add(NewTransaction(property, 5000m));
        context.AccountEstimates.Add(NewEstimate(property, 350000m, "USD", FixedNow));
        await context.SaveChangesAsync();

        var totals = await AsOfFixedNow(context).ComputeAsync("USD");

        Assert.Equal(5000m, totals.TotalAssets);
    }

    [Fact]
    public async Task Compute_RejectsAMainCurrencyThatIsNotSupported()
    {
        await using var context = TestContextFactory.Create();
        context.Accounts.Add(NewAccount(Guid.NewGuid(), "Checking", AccountType.CheckingAccount, "USD"));
        await context.SaveChangesAsync();

        // Unvalidated, this answered 200 with every account listed as unconvertible — a roster of the
        // whole portfolio's names and currencies for any caller who guessed a code (issue #90 §10.4).
        await Assert.ThrowsAsync<DomainValidationException>(
            () => AsOfFixedNow(context).ComputeAsync("ZZZ"));
    }

    [Fact]
    public async Task Compute_RejectsAnArchivedMainCurrency()
    {
        await using var context = TestContextFactory.Create();
        context.Currencies.Add(new Currency
        {
            CurrencyCode = "ZWL",
            Name = "Zimbabwe Dollar",
            MinorUnits = 2,
            Symbol = "Z$",
            Archived = FixedNow.AddYears(-1),
        });
        await context.SaveChangesAsync();

        await Assert.ThrowsAsync<DomainValidationException>(
            () => AsOfFixedNow(context).ComputeAsync("ZWL"));
    }
}
