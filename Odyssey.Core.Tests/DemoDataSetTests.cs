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

        Assert.Equal(second.Transactions.Count, first.Transactions.Count);

        var firstSignature = first.Transactions
            .Select(transaction => (transaction.TransactionId, transaction.Amount, transaction.TimeStamp))
            .ToList();
        var secondSignature = second.Transactions
            .Select(transaction => (transaction.TransactionId, transaction.Amount, transaction.TimeStamp))
            .ToList();

        Assert.Equal(secondSignature, firstSignature);
    }

    [Fact]
    public void Accounts_CoverEveryNonSentinelAccountType()
    {
        var data = DemoDataSet.Build();

        var expected = Enum.GetValues<AccountType>()
            .Where(type => type != AccountType.Unknown)
            .ToHashSet();
        var actual = data.Accounts.Select(account => account.AccountType).ToHashSet();

        Assert.True(expected.SetEquals(actual), $"missing: {string.Join(", ", expected.Except(actual))}; unexpected: {string.Join(", ", actual.Except(expected))}");
    }

    [Fact]
    public void Budgets_AreSeededForEveryYearWithFullTemplate()
    {
        var data = DemoDataSet.Build();

        var years = DemoDataDefaults.LastYear - DemoDataDefaults.FirstYear + 1;
        Assert.Equal(years, data.Budgets.Count);
        Assert.Equal(years * 17, data.BudgetItems.Count);

        foreach (var budget in data.Budgets)
        {
            Assert.Equal(17, data.BudgetItems.Count(item => item.BudgetId == budget.BudgetId));
        }
    }

    [Fact]
    public void TagLinks_AlwaysReferenceAnExistingTagAndTransaction()
    {
        var data = DemoDataSet.Build();

        var tagIds = data.Tags.Select(tag => tag.TransactionTagId).ToHashSet();
        var transactionIds = data.Transactions.Select(transaction => transaction.TransactionId).ToHashSet();

        Assert.All(data.TransactionTagLinks, link =>
            Assert.True(tagIds.Contains(link.TransactionTagId) && transactionIds.Contains(link.TransactionId)));
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
        // plus the default main currency (AccountsController.DefaultMainCurrency = "NOK"), which is
        // what the totals/net-worth view uses out of the box.
        var displayCurrencies = accountCurrencies.Append("NOK").Distinct().ToList();

        // Demo rates are manual and only fetched rows are inverted (issue #472), so every directed
        // (account currency → display currency) pair must have a direct rate, or those accounts warn.
        foreach (var from in accountCurrencies)
        {
            foreach (var to in displayCurrencies.Where(code => code != from))
            {
                Assert.True(pairs.Contains((from, to)), $"accounts in {from} must convert to {to}");
            }
        }

        Assert.All(data.ExchangeRates, rate => Assert.True(rate.Rate > 0m));
    }

    [Fact]
    public void ExchangeRates_CoverTheNetWorthChartsDefaultWindow()
    {
        // Issue #179: the chart converts each point at the rate in force AS OF that point, so a rate
        // history shorter than its window renders the early points as understated.
        var data = DemoDataSet.Build();
        var (windowStart, _) = new NetWorthHistoryQuery()
            .ResolveWindow(DateOnly.FromDateTime(DemoDataDefaults.AnchorDate));
        var windowStartInstant = windowStart.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        foreach (var pair in data.ExchangeRates.GroupBy(rate => (rate.FromCurrencyCode, rate.ToCurrencyCode)))
        {
            Assert.True(pair.Min(rate => rate.AsOf) <= windowStartInstant,
                $"{pair.Key.FromCurrencyCode}->{pair.Key.ToCurrencyCode} needs a rate in force at the chart's first point");
            // Conversions read the latest row, which is anchored on the seed date.
            Assert.Equal(DemoDataDefaults.AnchorDate, pair.Max(rate => rate.AsOf));
        }
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

            Assert.True(balance == 0m, $"closed account '{account.Name}' should be settled to zero, was {balance}");
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
            Assert.True(transaction.TimeStamp >= account.Opened);
            if (account.Closed is not null)
            {
                Assert.True(transaction.TimeStamp <= account.Closed.Value);
            }
        }
    }

    /// <summary>
    /// AC 21 (issue #121) — the seeded parties cover both tiers of the role matrix and give at least
    /// one party a non-default term. (<c>Unspecified</c> is retired, so no party can carry it.)
    /// </summary>
    [Fact]
    public void ContractParties_CoverBothMatrixTiersAndANonDefaultTerm()
    {
        var parties = DemoDataSet.Build().ContractParties;

        var roles = parties.Select(party => party.Role).ToHashSet();

        // Suggested roles across several type columns, including all four of Insurance's.
        Assert.Superset(new HashSet<ContractPartyRole>
        {
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
        }, roles);

        // …and the ALLOWED-but-not-suggested tier, which a set covering only the suggested cells
        // would never exercise (issue #157 §4.6).
        Assert.Superset(new HashSet<ContractPartyRole> { ContractPartyRole.Guarantor, ContractPartyRole.Other }, roles);

        // The two type-specific OBJECT roles (issue #169 §14 step 11): what the house purchase bought
        // and what the car loan is secured on. The E2E tiers read seeded data, so a role absent here
        // is a role no full-stack test ever renders or round-trips — which is why this is asserted
        // rather than left to the seeder's discretion.
        Assert.Superset(new HashSet<ContractPartyRole> { ContractPartyRole.Property, ContractPartyRole.Collateral }, roles);

        Assert.Contains(parties, party => party.FromDate != null || party.ToDate != null);
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

        Assert.Empty(violations);
    }

    /// <summary>
    /// The new <c>Loan</c> type is actually seeded (AC 19's demo half), so the reading order, the
    /// registry entry and the type filter all have a row to show.
    /// </summary>
    [Fact]
    public void Contracts_IncludeALoan()
    {
        var data = DemoDataSet.Build();

        Assert.Contains(data.Contracts, contract => contract.Type == ContractType.Loan);
    }

    /// <summary>
    /// Issue #187 AC 20 — a <c>Deposit</c> is seeded with BOTH of its suggested roles and an
    /// <c>Incoming</c> term, so the type filter, the party picker's two new roles and the roll-up's
    /// incoming split under <c>Deposit</c> all have a row to show in the dev stack and the E2E tiers.
    /// </summary>
    [Fact]
    public void Contracts_IncludeADeposit_WithADepositorACustodianAndAnIncomingTerm()
    {
        var data = DemoDataSet.Build();

        // The one SPECIFIED deposit; issue #190 adds more, created for the accounts whose terms it moved.
        var deposit = Assert.Single(data.Contracts, contract => contract.Name == "Fixed-term Deposit — 12 Months");
        Assert.Equal(ContractType.Deposit, deposit.Type);

        var depositRoles = data.ContractParties.Where(party => party.ContractId == deposit.ContractId)
            .Select(party => party.Role).ToHashSet();
        Assert.Superset(new HashSet<ContractPartyRole> { ContractPartyRole.Depositor, ContractPartyRole.Custodian }, depositRoles);

        Assert.Contains(data.Terms, term =>
            term.ContractId == deposit.ContractId && term.Direction == Odyssey.Context.TermDirection.Incoming);
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
                Assert.True(party.FromDate!.Value.Date >= start.Date);
            }
        }

        foreach (var party in data.ContractParties.Where(p => p is { FromDate: not null, ToDate: not null }))
        {
            Assert.True(party.ToDate!.Value.Date >= party.FromDate!.Value.Date);
        }
    }
}
