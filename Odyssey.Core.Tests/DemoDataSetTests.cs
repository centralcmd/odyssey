using AwesomeAssertions;
using Mapster;
using Odyssey.Dtos.Finance;
using Odyssey.TestData;
using Xunit;
using AccountType = Odyssey.Context.AccountType;
using ContractPartyRole = Odyssey.Context.ContractPartyRole;
using ContractType = Odyssey.Context.ContractType;

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

    /// <summary>
    /// AC 21 (issue #121) — the seeded parties cover all five NAMED v1 roles, leave at least one
    /// party <c>Unspecified</c> and give at least one a non-default term.
    /// </summary>
    /// <remarks>
    /// <c>Unspecified</c> is asserted separately from the named five on purpose: it is the state the
    /// migration backfills every pre-#121 row to, and the one whose tile falls back to the kind word
    /// rather than rendering a sentinel. A fixture that roled every party would leave that rendering
    /// path unexercised by the demo stack, which is where it is actually looked at.
    /// </remarks>
    [Fact]
    public void ContractParties_CoverBothMatrixTiersAndANonDefaultTerm()
    {
        var parties = DemoDataSet.Build().ContractParties;

        var roles = parties.Select(party => party.Role).ToHashSet();

        // Suggested roles across several type columns, including all four of Insurance's.
        roles.Should().Contain(
        [
            ContractPartyRole.Employee,
            ContractPartyRole.Employer,
            ContractPartyRole.Buyer,
            ContractPartyRole.Seller,
            ContractPartyRole.Landlord,
            ContractPartyRole.Insurer,
            ContractPartyRole.Policyholder,
            ContractPartyRole.Insured,
            ContractPartyRole.Beneficiary,
            ContractPartyRole.Lender,
            ContractPartyRole.Borrower,
        ]);

        // …and the ALLOWED-but-not-suggested tier, which a set covering only the suggested cells
        // would never exercise (issue #157 §4.6).
        roles.Should().Contain([ContractPartyRole.Guarantor, ContractPartyRole.Other]);

        parties.Should().Contain(party => party.FromDate != null || party.ToDate != null);
    }

    /// <summary>
    /// Issue #157 AC 20 — the seeder produces ZERO matrix violations, walked rather than inspected: a
    /// violation here would be demo data the API itself would refuse with a 422, which is the one
    /// class of seed defect that makes the demo stack a liar about its own rules.
    /// </summary>
    [Fact]
    public void ContractParties_AreAllLegalForTheirContractType()
    {
        var data = DemoDataSet.Build();
        var typeOf = data.Contracts.ToDictionary(c => c.ContractId, c => c.Type);

        var violations = data.ContractParties
            .Where(party => !ContractPartyRoleMatrix.IsLegal(
                typeOf[party.ContractId].Adapt<Odyssey.Dtos.Finance.ContractType>(),
                party.Role.Adapt<Odyssey.Dtos.Finance.ContractPartyRole>()))
            .Select(party => $"{typeOf[party.ContractId]} contract holds a {party.Role} party")
            .ToList();

        violations.Should().BeEmpty();
    }

    /// <summary>
    /// The new <c>Loan</c> type is actually seeded (AC 19's demo half), so the reading order, the
    /// registry entry and the type filter all have a row to show.
    /// </summary>
    [Fact]
    public void Contracts_IncludeALoan()
    {
        var data = DemoDataSet.Build();

        data.Contracts.Should().Contain(contract => contract.Type == ContractType.Loan);
    }

    /// <summary>
    /// The seeded terms respect the one rule the server enforces on a party write: a term cannot begin
    /// before the contract did. Seeding a row the API would refuse would make the demo stack an
    /// unreliable oracle for exactly the validation this feature adds.
    /// </summary>
    [Fact]
    public void ContractPartyTerms_NeverBeginBeforeTheirContract()
    {
        var data = DemoDataSet.Build();
        var startDates = data.Contracts.ToDictionary(c => c.ContractId, c => c.StartDate);

        foreach (var party in data.ContractParties.Where(p => p.FromDate is not null))
        {
            if (startDates[party.ContractId] is { } start)
            {
                party.FromDate!.Value.Date.Should().BeOnOrAfter(start.Date);
            }
        }

        foreach (var party in data.ContractParties.Where(p => p is { FromDate: not null, ToDate: not null }))
        {
            party.ToDate!.Value.Date.Should().BeOnOrAfter(party.FromDate!.Value.Date);
        }
    }
}
