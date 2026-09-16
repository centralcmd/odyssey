using Odyssey.Core;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Xunit;
using AccountType = Odyssey.Context.AccountType;
using Odyssey.Core.Finance;

namespace Odyssey.Core.Tests;

public class AccountTotalsServiceTests
{
    private static Account NewAccount(
        Guid id,
        string name,
        AccountType type,
        string currency,
        DateTime? archived = null,
        DateTime? closed = null,
        DateTime? opened = null) => new()
    {
        AccountId = id,
        Name = name,
        Description = name,
        Opened = opened ?? new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        AccountType = type,
        CurrencyCode = currency,
        Archived = archived,
        Closed = closed,
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
    public async Task Compute_ConvertsAssetsAndLiabilities_FlagsUnconverted_KeepsArchived_ExcludesClosed()
    {
        await using var context = TestContextFactory.Create();

        var usdChecking = Guid.NewGuid();   // asset, main currency, 1:1
        var eurSavings = Guid.NewGuid();     // asset, converted via EUR->USD
        var sekCard = Guid.NewGuid();        // liability, converted via SEK->USD
        var gbpAccount = Guid.NewGuid();     // asset, no rate to USD -> unconverted
        var archivedUsd = Guid.NewGuid();    // archived -> STILL COUNTS (issue #99)
        var closedUsd = Guid.NewGuid();      // closed in the past -> excluded (issue #99)

        context.Accounts.AddRange(
            NewAccount(usdChecking, "USD Checking", AccountType.CheckingAccount, "USD"),
            NewAccount(eurSavings, "EUR Savings", AccountType.SavingsAccount, "EUR"),
            NewAccount(sekCard, "SEK Card", AccountType.CreditCard, "SEK"),
            NewAccount(gbpAccount, "GBP Brokerage", AccountType.InvestmentAccount, "GBP"),
            NewAccount(archivedUsd, "Filed away", AccountType.CheckingAccount, "USD",
                archived: new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc)),
            NewAccount(closedUsd, "Closed", AccountType.CheckingAccount, "USD",
                closed: new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc)));

        context.Transactions.AddRange(
            NewTransaction(usdChecking, 1000m),
            NewTransaction(eurSavings, 200m),
            NewTransaction(sekCard, -1000m),
            NewTransaction(gbpAccount, 50m),
            NewTransaction(archivedUsd, 500m),
            NewTransaction(closedUsd, 9999m));

        await context.SaveChangesAsync();

        var rateService = new ExchangeRateService(context);
        await rateService.Create(new NewExchangeRate { FromCurrencyCode = "EUR", ToCurrencyCode = "USD", Rate = 1.1m });
        await rateService.Create(new NewExchangeRate { FromCurrencyCode = "SEK", ToCurrencyCode = "USD", Rate = 0.1m });

        var service = new AccountTotalsService(context, new CurrencyConversionService(context));
        var totals = await service.ComputeAsync("USD");

        Assert.Equal("USD", totals.MainCurrencyCode);
        // 1000 (USD 1:1) + 200*1.1 (EUR) + 500 (archived, USD 1:1) = 1720. The closed account's 9999
        // is gone: archiving files an account away, closing ends its term.
        Assert.Equal(1720m, totals.TotalAssets);
        // abs(-1000 * 0.1) = 100
        Assert.Equal(100m, totals.TotalLiabilities);
        Assert.Equal(1620m, totals.NetWorth);

        var unconverted = Assert.Single(totals.UnconvertedAccounts);
        Assert.Equal(gbpAccount, unconverted.AccountId);
        Assert.Equal("GBP", unconverted.CurrencyCode);
    }

    // ── Issue #99 — membership is the open/closed term, never Archived ─────────────────────────

    /// <summary>
    /// Archiving is the app's generic declutter verb — the same column photos, journal entries, tags
    /// and budgets carry. Filing an account away must leave every figure exactly where it was.
    /// </summary>
    [Fact]
    public async Task Archiving_An_Account_Does_Not_Move_The_Total()
    {
        await using var context = TestContextFactory.Create();
        var kept = Guid.NewGuid();
        var filed = Guid.NewGuid();
        context.Accounts.AddRange(
            NewAccount(kept, "Kept", AccountType.CheckingAccount, "USD"),
            NewAccount(filed, "Filed", AccountType.SavingsAccount, "USD"));
        context.Transactions.AddRange(
            NewTransaction(kept, 100m),
            NewTransaction(filed, 200_000m));
        await context.SaveChangesAsync();

        var before = await AsOfFixedNow(context).ComputeAsync("USD");

        context.Accounts.Single(account => account.AccountId == filed).Archived = FixedNow.AddDays(-1);
        await context.SaveChangesAsync();

        var after = await AsOfFixedNow(context).ComputeAsync("USD");

        Assert.Equal(200_100m, before.NetWorth);
        Assert.Equal(before.NetWorth, after.NetWorth);
        Assert.Equal(before.TotalAssets, after.TotalAssets);
    }

    /// <summary>
    /// The other half: a closed account leaves the valuation. In clean data this is a no-op, because
    /// closing normally means transferring the balance out and the receiving account picks it up. It
    /// bites only where a balance was left stranded — and there the new figure is the correct one.
    /// </summary>
    [Fact]
    public async Task A_Closed_Account_Leaves_The_Total()
    {
        await using var context = TestContextFactory.Create();
        var open = Guid.NewGuid();
        var closing = Guid.NewGuid();
        context.Accounts.AddRange(
            NewAccount(open, "Open", AccountType.CheckingAccount, "USD"),
            NewAccount(closing, "Closing", AccountType.SavingsAccount, "USD"));
        context.Transactions.AddRange(
            NewTransaction(open, 100m),
            NewTransaction(closing, 900m));
        await context.SaveChangesAsync();

        var before = await AsOfFixedNow(context).ComputeAsync("USD");

        context.Accounts.Single(account => account.AccountId == closing).Closed = FixedNow.AddDays(-1);
        await context.SaveChangesAsync();

        var after = await AsOfFixedNow(context).ComputeAsync("USD");

        Assert.Equal(1000m, before.NetWorth);
        Assert.Equal(100m, after.NetWorth);
    }

    /// <summary>
    /// A close date in the future has not happened yet, so the account is still inside its term.
    /// </summary>
    [Fact]
    public async Task An_Account_Closing_In_The_Future_Still_Counts()
    {
        await using var context = TestContextFactory.Create();
        var id = Guid.NewGuid();
        context.Accounts.Add(NewAccount(id, "Closing soon", AccountType.CheckingAccount, "USD",
            closed: FixedNow.AddDays(1)));
        context.Transactions.Add(NewTransaction(id, 750m));
        await context.SaveChangesAsync();

        var totals = await AsOfFixedNow(context).ComputeAsync("USD");

        Assert.Equal(750m, totals.NetWorth);
    }

    /// <summary>
    /// The boundary, in both directions. <c>Opened</c> is exclusive (<c>Opened &lt; now</c>) and
    /// <c>Closed</c> is its mirror (<c>now &lt; Closed</c>), so an account closed at exactly the
    /// measuring instant is already gone by it — the same convention the history's grid bounds use, and
    /// the reason the two endpoints can agree at their shared final instant.
    /// </summary>
    [Fact]
    public async Task The_Term_Boundary_Is_Exclusive_At_Both_Ends()
    {
        await using var context = TestContextFactory.Create();
        var closedAtNow = Guid.NewGuid();
        var closedAfterNow = Guid.NewGuid();
        var openedAtNow = Guid.NewGuid();

        // The offsets are whole seconds, not ticks: the columns are datetime(6), so MySqlConnector
        // truncates a 7-digit tick parameter to microseconds while EF InMemory compares full ticks —
        // a sub-microsecond offset would pass here and quietly mean equality on MariaDB.
        context.Accounts.AddRange(
            NewAccount(closedAtNow, "Closed at now", AccountType.CheckingAccount, "USD", closed: FixedNow),
            NewAccount(closedAfterNow, "Closed a second later", AccountType.CheckingAccount, "USD",
                closed: FixedNow.AddSeconds(1)),
            NewAccount(openedAtNow, "Opened at now", AccountType.CheckingAccount, "USD", opened: FixedNow));
        context.Transactions.AddRange(
            NewTransaction(closedAtNow, 1m),
            NewTransaction(closedAfterNow, 10m),
            NewTransaction(openedAtNow, 100m));
        await context.SaveChangesAsync();

        var totals = await AsOfFixedNow(context).ComputeAsync("USD");

        // Only the account still closing after now is inside its term.
        Assert.Equal(10m, totals.NetWorth);
    }

    /// <summary>
    /// An estimate effective after the close date must not keep a closed account alive — the account
    /// is out of the roster before its estimates are ever resolved.
    /// </summary>
    [Fact]
    public async Task An_Estimate_Effective_After_The_Close_Date_Does_Not_Revive_The_Account()
    {
        await using var context = TestContextFactory.Create();
        var closed = Guid.NewGuid();
        context.Accounts.Add(NewAccount(closed, "Closed", AccountType.InvestmentAccount, "USD",
            closed: new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc)));
        context.Transactions.Add(NewTransaction(closed, 400m));
        context.AccountEstimates.Add(NewEstimate(closed, 50_000m, "USD",
            new DateTime(2025, 4, 1, 0, 0, 0, DateTimeKind.Utc)));
        await context.SaveChangesAsync();

        var totals = await AsOfFixedNow(context).ComputeAsync("USD");

        Assert.Equal(0m, totals.NetWorth);
        Assert.Empty(totals.UnconvertedAccounts);
    }

    /// <summary>
    /// A closed account is out of the roster entirely, so it is not reported as unconvertible either —
    /// which would misdescribe a correctly-excluded account as a missing exchange rate.
    /// </summary>
    [Fact]
    public async Task A_Closed_Account_Is_Not_Reported_As_Unconvertible()
    {
        await using var context = TestContextFactory.Create();
        var closedGbp = Guid.NewGuid();
        context.Accounts.Add(NewAccount(closedGbp, "Closed GBP", AccountType.CheckingAccount, "GBP",
            closed: new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc)));
        context.Transactions.Add(NewTransaction(closedGbp, 50m));
        await context.SaveChangesAsync();

        var totals = await AsOfFixedNow(context).ComputeAsync("USD");

        Assert.Empty(totals.UnconvertedAccounts);
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

    // ── The asset/liability split has ONE definition (issue #90 review, architect nit #2) ──────

    /// <summary>
    /// Every <see cref="AccountType"/> is classified exactly once — asset, liability, or neither — and
    /// never both.
    ///
    /// <para>
    /// <c>AccountTotalsService</c> and <c>NetWorthHistoryService</c> used to declare the same two range
    /// checks independently, and AC2 requires the two endpoints to agree to the cent. Two copies is the
    /// one place a new <c>AccountType</c> could break that silently: extend one range, miss the other,
    /// and both compile and both answer, disagreeing only for the new type. They now share
    /// <see cref="AccountClassification"/>; this pins that a new member lands somewhere sane rather
    /// than in both buckets or, worse, quietly in neither when it belongs in one.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryAccountType_IsAssetOrLiabilityOrNeither_NeverBoth()
    {
        foreach (var type in Enum.GetValues<AccountType>())
        {
            var asset = AccountClassification.IsAsset(type);
            var liability = AccountClassification.IsLiability(type);

            Assert.False(asset && liability, $"{type} is classified as BOTH an asset and a liability.");
            Assert.Equal(!asset && !liability, AccountClassification.Unclassified(type));
        }
    }

    /// <summary>
    /// Unknown is the only unclassified type today. A new member landing outside both spans is excluded
    /// from every total silently, so this fires when one appears and makes that a decision rather than
    /// an accident.
    /// </summary>
    [Fact]
    public void OnlyUnknown_IsExcludedFromBothTotals()
    {
        var unclassified = Enum.GetValues<AccountType>()
            .Where(AccountClassification.Unclassified)
            .ToList();

        Assert.Equal([AccountType.Unknown], unclassified);
    }

    /// <summary>
    /// The two services agree because they call the same predicates, so this asserts the agreement
    /// end-to-end rather than trusting that: the same portfolio through both paths must split
    /// identically, and drop the same accounts.
    /// </summary>
    /// <remarks>
    /// <b>The portfolio contains closed accounts on purpose (issue #99).</b> Membership is the other
    /// thing the two services have to agree about, and it is the half this issue changed — but AC2 was
    /// pinned here over a portfolio where nothing ever closed, so a term rule applied in one service
    /// and not the other would have left this green. The only cover for that was the MariaDB test,
    /// which self-skips without Docker, so an ordinary `dotnet test Odyssey.Core.Tests` had none at all.
    /// The exact-instant boundary stays in the integration tier, where `datetime(6)` makes it mean
    /// something; the close date here is deliberately far from any period bound, which is a case EF
    /// InMemory is a sound oracle for.
    /// </remarks>
    [Fact]
    public async Task TheTotalsAndTheHistory_ClassifyTheSamePortfolioIdentically()
    {
        await using var context = TestContextFactory.Create();

        // One asset and one liability close mid-window; the close date sits a month before FixedNow,
        // clear of every monthly period bound and of the final bound itself.
        var closedOn = FixedNow.AddMonths(-1);
        AccountType[] closing = [AccountType.SavingsAccount, AccountType.CreditCard];

        // NewAccount's optional arguments are `archived`/`closed`/`opened`, all defaulted — every
        // account here opens at 2025-01-01, comfortably inside the default 24-month window ending at
        // FixedNow.
        foreach (var type in Enum.GetValues<AccountType>())
        {
            var id = Guid.NewGuid();
            context.Accounts.Add(NewAccount(id, type.ToString(), type, "USD",
                closed: closing.Contains(type) ? closedOn : null));
            context.Transactions.Add(NewTransaction(id, 100m));
        }

        await context.SaveChangesAsync();

        var totals = await AsOfFixedNow(context).ComputeAsync("USD");
        var history = await new NetWorthHistoryService(
            context, new CurrencyConversionService(context), new FixedTimeProvider(FixedNow))
            .ComputeAsync(new NetWorthHistoryQuery { MainCurrency = "USD" });

        var last = history.Points[^1];
        Assert.Equal(totals.TotalAssets, last.TotalAssets);
        Assert.Equal(totals.TotalLiabilities, last.TotalLiabilities);
        Assert.Equal(totals.NetWorth, last.NetWorth);

        // …and the agreement is not the vacuous kind where both kept the closed pair, or both dropped
        // everything. Each side is exactly one account short of its full roster.
        var types = Enum.GetValues<AccountType>();
        Assert.Equal((types.Count(AccountClassification.IsAsset) - 1) * 100m, totals.TotalAssets);
        Assert.Equal((types.Count(AccountClassification.IsLiability) - 1) * -100m, totals.TotalLiabilities);

        // The closed pair is in the line's past, so the series is not flat at the final figure —
        // which is what distinguishes a per-slot term from one applied once for the whole series.
        Assert.Contains(history.Points, point => point.NetWorth != last.NetWorth);
    }
}
