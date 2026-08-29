using AwesomeAssertions;
using Odyssey.TestData;
using Xunit;
using AccountType = Odyssey.Context.AccountType;

namespace Odyssey.Core.Tests;

public class DemoDataSetTests
{
    [Fact]
    public void Build_IsDeterministic()
    {
        var first = DemoDataSet.Build();
        var second = DemoDataSet.Build();

        first.Transactions.Should().HaveCount(second.Transactions.Count);

        var firstSignature = first.Transactions
            .Select(transaction => (transaction.TransactionId, transaction.Amount, transaction.TimeStamp))
            .ToList();
        var secondSignature = second.Transactions
            .Select(transaction => (transaction.TransactionId, transaction.Amount, transaction.TimeStamp))
            .ToList();

        firstSignature.Should().Equal(secondSignature);
    }

    [Fact]
    public void Accounts_CoverEveryNonSentinelAccountType()
    {
        var data = DemoDataSet.Build();

        var expected = Enum.GetValues<AccountType>()
            .Where(type => type != AccountType.Unknown)
            .ToHashSet();
        var actual = data.Accounts.Select(account => account.AccountType).ToHashSet();

        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void Budgets_AreSeededForEveryYearWithFullTemplate()
    {
        var data = DemoDataSet.Build();

        var years = DemoDataDefaults.LastYear - DemoDataDefaults.FirstYear + 1;
        data.Budgets.Should().HaveCount(years);
        data.BudgetItems.Should().HaveCount(years * 17);

        foreach (var budget in data.Budgets)
        {
            data.BudgetItems.Count(item => item.BudgetId == budget.BudgetId).Should().Be(17);
        }
    }

    [Fact]
    public void TagLinks_AlwaysReferenceAnExistingTagAndTransaction()
    {
        var data = DemoDataSet.Build();

        var tagIds = data.Tags.Select(tag => tag.TransactionTagId).ToHashSet();
        var transactionIds = data.Transactions.Select(transaction => transaction.TransactionId).ToHashSet();

        data.TransactionTagLinks.Should().OnlyContain(link =>
            tagIds.Contains(link.TransactionTagId) && transactionIds.Contains(link.TransactionId));
    }

    [Fact]
    public void ExchangeRates_CoverEveryDirectedAccountCurrencyPair()
    {
        var data = DemoDataSet.Build();

        var accountCurrencies = data.Accounts.Select(account => account.CurrencyCode).Distinct().ToList();
        var pairs = data.ExchangeRates
            .Select(rate => (rate.FromCurrencyCode, rate.ToCurrencyCode))
            .ToHashSet();

        // Display currencies an account balance may need to convert into: any account currency,
        // plus the default main currency (AccountController.DefaultMainCurrency = "NOK"), which is
        // what the totals/net-worth view uses out of the box.
        var displayCurrencies = accountCurrencies.Append("NOK").Distinct().ToList();

        // The conversion service does no inversion/triangulation, so every directed
        // (account currency → display currency) pair must have a direct rate, or those accounts warn.
        foreach (var from in accountCurrencies)
        {
            foreach (var to in displayCurrencies.Where(code => code != from))
            {
                pairs.Should().Contain((from, to), "accounts in {0} must convert to {1}", from, to);
            }
        }

        data.ExchangeRates.Should().OnlyContain(rate => rate.Rate > 0m);
    }

    [Fact]
    public void ClosedAccounts_NetToZero()
    {
        var data = DemoDataSet.Build();

        foreach (var account in data.Accounts.Where(account => account.Closed is not null))
        {
            var balance = data.Transactions
                .Where(transaction => transaction.AccountId == account.AccountId)
                .Sum(transaction => transaction.Amount);

            balance.Should().Be(0m, "closed account '{0}' should be settled to zero", account.Name);
        }
    }

    [Fact]
    public void Transactions_StayWithinTheirAccountLifetime()
    {
        var data = DemoDataSet.Build();
        var accountsById = data.Accounts.ToDictionary(account => account.AccountId);

        foreach (var transaction in data.Transactions)
        {
            var account = accountsById[transaction.AccountId];
            transaction.TimeStamp.Should().BeOnOrAfter(account.Opened);
            if (account.Closed is not null)
            {
                transaction.TimeStamp.Should().BeOnOrBefore(account.Closed.Value);
            }
        }
    }
}
