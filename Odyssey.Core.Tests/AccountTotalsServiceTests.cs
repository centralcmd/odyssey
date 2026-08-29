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
}
