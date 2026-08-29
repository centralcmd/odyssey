using Bogus;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Odyssey.TestData.Catalog;
using static Odyssey.TestData.DemoDataDefaults;
using AccountType = Odyssey.Context.AccountType;

namespace Odyssey.TestData.Generators;

/// <summary>
/// Generates transactions from a fixed set of recurring "streams" plus light
/// deterministic jitter (spec §3.11). Honours the model's correctness rules:
/// income is positive / expense negative (balance is a plain sum), tags attach via
/// <see cref="TransactionTagLink"/>, transfers are paired entries (only the outflow
/// leg is tagged so budget sums don't cancel), and each account gets an opening
/// entry while closed accounts are driven back to ≈0.
/// </summary>
public static class TransactionGenerator
{
    private enum Cadence { Weekly, Biweekly, Monthly, Quarterly, Yearly, ThriceYearly }

    private sealed record StreamDef(
        string Description,
        string AccountKey,
        string[] ContactKeys,
        string? TagName,
        int Sign,
        decimal BaseAmount,
        bool IsIncome,
        Cadence Cadence,
        bool DenseRecentOnly = false,
        DateTime? NotBefore = null,
        DateTime? NotAfter = null,
        string? TransferToAccountKey = null);

    private static DateTime D(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    private static readonly StreamDef[] Streams =
    [
        // ── Income onto the primary checking account ──
        new("Monthly salary", Accounts.EverydayChecking, [Contacts.Globex], Tags.Salary, +1, 5_000m, true, Cadence.Monthly),
        new("Annual bonus", Accounts.EverydayChecking, [Contacts.Globex], Tags.Bonus, +1, 5_000m, true, Cadence.Yearly),

        // ── Housing: rent before the mortgage, mortgage after ──
        new("Rent payment", Accounts.EverydayChecking, [Contacts.Landlord], Tags.Housing, -1, 1_200m, false, Cadence.Monthly, NotAfter: D(2017, 8, 31)),
        new("Mortgage payment", Accounts.EverydayChecking, [Contacts.FirstNationalBank], Tags.Housing, -1, 1_500m, false, Cadence.Monthly, NotBefore: D(2017, 9, 1)),

        // ── Everyday expenses ──
        new("Weekly groceries", Accounts.EverydayChecking, [Contacts.WholeFoods, Contacts.TraderJoes], Tags.Groceries, -1, 138m, false, Cadence.Weekly, DenseRecentOnly: true),
        new("Utilities bill", Accounts.EverydayChecking, [Contacts.CityPowerWater], Tags.Utilities, -1, 300m, false, Cadence.Monthly),
        new("Dining out", Accounts.EverydayChecking, [Contacts.Starbucks, Contacts.CornerBistro], Tags.DiningOut, -1, 60m, false, Cadence.Weekly, DenseRecentOnly: true),
        new("Fuel", Accounts.EverydayChecking, [Contacts.Shell], Tags.Fuel, -1, 90m, false, Cadence.Biweekly, DenseRecentOnly: true),
        new("Rideshare", Accounts.EverydayChecking, [Contacts.Uber], Tags.Transportation, -1, 200m, false, Cadence.Monthly),
        new("Streaming subscriptions", Accounts.EverydayChecking, [Contacts.Netflix, Contacts.Spotify], Tags.Subscriptions, -1, 50m, false, Cadence.Monthly),
        new("Insurance premium", Accounts.EverydayChecking, [Contacts.StateFarm], Tags.Insurance, -1, 167m, false, Cadence.Monthly),
        new("Clothing", Accounts.EverydayChecking, [Contacts.Hm], Tags.Clothing, -1, 100m, false, Cadence.Monthly),
        new("Entertainment", Accounts.EverydayChecking, [Contacts.CornerBistro], Tags.Entertainment, -1, 150m, false, Cadence.Monthly),
        new("Travel booking", Accounts.EverydayChecking, [Contacts.Delta], Tags.Travel, -1, 1_000m, false, Cadence.ThriceYearly),
        new("Doctor / pharmacy", Accounts.EverydayChecking, [Contacts.BlueCross], Tags.Healthcare, -1, 600m, false, Cadence.Quarterly),
        new("Annual tax settlement", Accounts.EverydayChecking, [Contacts.Irs], Tags.Taxes, -1, 1_200m, false, Cadence.Yearly, NotBefore: D(2016, 4, 1)),

        // ── Transfers (paired): outflow on checking is tagged, inflow on destination is not ──
        new("Savings transfer", Accounts.EverydayChecking, [Contacts.FirstNationalBank], Tags.Savings, -1, 500m, false, Cadence.Monthly, TransferToAccountKey: Accounts.EmergencyFund),
        new("Investment contribution", Accounts.EverydayChecking, [Contacts.Vanguard], Tags.Investments, -1, 500m, false, Cadence.Monthly, TransferToAccountKey: Accounts.BrokerageAccount),
        new("Credit card payment", Accounts.EverydayChecking, [Contacts.FirstNationalBank], Tags.LoanRepayment, -1, 350m, false, Cadence.Monthly, TransferToAccountKey: Accounts.TravelRewardsCard),

        // ── Investment / savings returns ──
        new("Dividend payout", Accounts.BrokerageAccount, [Contacts.Vanguard], Tags.Dividends, +1, 300m, true, Cadence.Quarterly),
        new("Interest earned", Accounts.EmergencyFund, [Contacts.FirstNationalBank], Tags.InterestIncome, +1, 25m, true, Cadence.Monthly),

        // ── Credit-card spending (builds the liability the payment stream pays down) ──
        new("Card purchase", Accounts.TravelRewardsCard, [Contacts.Hm, Contacts.Starbucks, Contacts.Uber], Tags.Clothing, -1, 80m, false, Cadence.Weekly, DenseRecentOnly: true),

        // ── Loan paydown (positive amounts reduce the negative loan balance) ──
        new("Auto-loan payment", Accounts.CarLoanVolvo, [Contacts.FirstNationalBank], Tags.LoanRepayment, +1, 450m, false, Cadence.Monthly),

        // ── Non-USD activity to exercise the budget excluded-currency path ──
        new("Stock purchase", Accounts.StocksPortfolio, [Contacts.Vanguard], Tags.Investments, -1, 2_000m, false, Cadence.Quarterly),
    ];

    // Opening balances by account type, in the account's own currency. Assets positive,
    // liabilities a negative principal. Credit cards start at 0 (built from spend).
    private static decimal OpeningAmount(AccountType type) => type switch
    {
        AccountType.CheckingAccount => 2_500m,
        AccountType.SavingsAccount => 5_000m,
        AccountType.InvestmentAccount => 10_000m,
        AccountType.PensionAccount => 15_000m,
        AccountType.Cash => 300m,
        AccountType.Property => 450_000m,
        AccountType.Vehicle => 35_000m,
        AccountType.OtherAsset => 12_000m,
        AccountType.CreditCard => 0m,
        AccountType.Mortgage => -350_000m,
        AccountType.StudentLoan => -40_000m,
        AccountType.PersonalLoan => -25_000m,
        AccountType.CarLoan => -32_000m,
        AccountType.TaxDebt => -8_000m,
        AccountType.OtherLiability => -5_000m,
        _ => 0m,
    };

    public static (List<Transaction> Transactions, List<TransactionTagLink> TagLinks) Build(
        IReadOnlyList<Account> accounts, DateTime anchor)
    {
        Randomizer.Seed = new Random(RandomizerSeed);
        var faker = new Faker();

        var byName = accounts.ToDictionary(account => account.Name);
        var transactions = new List<Transaction>();
        var tagLinks = new List<TransactionTagLink>();
        var dataStart = new DateTime(FirstYear, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Transaction Emit(Guid id, string description, decimal amount, DateTime when, Account account, Guid? contactId, string? tagName)
        {
            var status = faker.Random.WeightedRandom(
                [TransactionStatus.Approved, TransactionStatus.New, TransactionStatus.Flagged],
                [0.80f, 0.15f, 0.05f]);

            var transaction = new Transaction
            {
                TransactionId = id,
                Description = description,
                Amount = amount,
                TimeStamp = when,
                AccountId = account.AccountId,
                ContactId = contactId,
                CurrencyCode = account.CurrencyCode,
                Status = status,
                StatusComment = status == TransactionStatus.Flagged ? "Auto-flagged for review" : null,
                StatusChangedAt = when,
            };
            transactions.Add(transaction);

            if (tagName is not null)
            {
                tagLinks.Add(new TransactionTagLink
                {
                    Id = DeterministicGuid.From($"taglink::{id}::{tagName}"),
                    TransactionId = id,
                    TransactionTagId = Tags.IdFor(tagName),
                });
            }

            return transaction;
        }

        // 1) Opening / principal entries.
        foreach (var account in accounts)
        {
            var opening = OpeningAmount(account.AccountType);
            if (opening == 0m)
            {
                continue;
            }

            var contact = opening < 0m ? Contacts.IdFor(Contacts.FirstNationalBank) : (Guid?)null;
            Emit(
                DeterministicGuid.From($"opening::{account.Name}"),
                opening < 0m ? "Opening principal" : "Opening balance",
                opening,
                account.Opened,
                account,
                contact,
                tagName: null);
        }

        // 2) Recurring streams.
        var denseCutoff = anchor.AddMonths(-24);
        foreach (var stream in Streams)
        {
            var source = byName[stream.AccountKey];
            var windowStart = Max(source.Opened, dataStart, stream.NotBefore);
            var windowEnd = Min(source.Closed ?? anchor, anchor, stream.NotAfter);

            Account? destination = stream.TransferToAccountKey is null ? null : byName[stream.TransferToAccountKey];
            if (destination is not null)
            {
                // A transfer's paired legs must fall within BOTH accounts' lifetimes.
                windowStart = Max(windowStart, destination.Opened, null);
                windowEnd = Min(windowEnd, destination.Closed ?? anchor, null);
            }

            if (windowStart > windowEnd)
            {
                continue;
            }

            foreach (var (when, summarized) in Occurrences(stream, windowStart, windowEnd, denseCutoff))
            {
                var perOccurrence = Escalate(stream.BaseAmount, when.Year, stream.IsIncome);
                if (summarized)
                {
                    perOccurrence = Math.Round(perOccurrence * MonthlyFactor(stream.Cadence), 2, MidpointRounding.AwayFromZero);
                }

                var jitter = 1m + (decimal)faker.Random.Double(-0.10, 0.10);
                var magnitude = Math.Round(perOccurrence * jitter, 2, MidpointRounding.AwayFromZero);
                var contactKey = faker.PickRandom(stream.ContactKeys);
                var contactId = Contacts.IdFor(contactKey);

                var sourceId = DeterministicGuid.From($"txn::{stream.AccountKey}::{stream.Description}::{when:O}");
                Emit(sourceId, stream.Description, stream.Sign * magnitude, when, source, contactId, stream.TagName);

                if (destination is not null)
                {
                    // Inflow leg: opposite sign, untagged, so paired tag sums don't cancel in budgets.
                    var destId = DeterministicGuid.From($"txn::{stream.TransferToAccountKey}::{stream.Description}::{when:O}::in");
                    Emit(destId, stream.Description, -stream.Sign * magnitude, when, destination, contactId, tagName: null);
                }
            }
        }

        // 3) Drive closed accounts back to ≈0 with a final settlement on the close date.
        foreach (var account in accounts.Where(account => account.Closed is not null))
        {
            var balance = transactions
                .Where(transaction => transaction.AccountId == account.AccountId)
                .Sum(transaction => transaction.Amount);
            if (balance == 0m)
            {
                continue;
            }

            Emit(
                DeterministicGuid.From($"closing::{account.Name}"),
                "Account closed — final settlement",
                -balance,
                account.Closed!.Value,
                account,
                contactId: null,
                tagName: null);
        }

        return (transactions, tagLinks);
    }

    private static decimal MonthlyFactor(Cadence cadence) => cadence switch
    {
        Cadence.Weekly => 52m / 12m,
        Cadence.Biweekly => 26m / 12m,
        _ => 1m,
    };

    private static IEnumerable<(DateTime When, bool Summarized)> Occurrences(
        StreamDef stream, DateTime start, DateTime end, DateTime denseCutoff)
    {
        switch (stream.Cadence)
        {
            case Cadence.Weekly:
            case Cadence.Biweekly:
                var step = stream.Cadence == Cadence.Weekly ? 7 : 14;
                if (stream.DenseRecentOnly)
                {
                    // Older history: one summarized monthly entry; recent: real high-frequency entries.
                    var summarizedEnd = Min(denseCutoff, end, null);
                    foreach (var when in Monthly(start, summarizedEnd, 14))
                    {
                        yield return (when, true);
                    }

                    var denseStart = Max(start, denseCutoff, null);
                    for (var when = denseStart; when <= end; when = when.AddDays(step))
                    {
                        yield return (when, false);
                    }
                }
                else
                {
                    for (var when = start; when <= end; when = when.AddDays(step))
                    {
                        yield return (when, false);
                    }
                }

                break;

            case Cadence.Monthly:
                foreach (var when in Monthly(start, end, 14))
                {
                    yield return (when, false);
                }

                break;

            case Cadence.Quarterly:
                foreach (var when in InMonths(start, end, [2, 5, 8, 11], 10))
                {
                    yield return (when, false);
                }

                break;

            case Cadence.ThriceYearly:
                foreach (var when in InMonths(start, end, [3, 7, 11], 15))
                {
                    yield return (when, false);
                }

                break;

            case Cadence.Yearly:
                var month = stream.Description.Contains("tax", StringComparison.OrdinalIgnoreCase) ? 4 : 12;
                foreach (var when in InMonths(start, end, [month], 10))
                {
                    yield return (when, false);
                }

                break;
        }
    }

    private static IEnumerable<DateTime> Monthly(DateTime start, DateTime end, int day)
    {
        var year = start.Year;
        var month = start.Month;
        while (true)
        {
            var when = new DateTime(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)), 0, 0, 0, DateTimeKind.Utc);
            if (when > end)
            {
                yield break;
            }

            if (when >= start)
            {
                yield return when;
            }

            month++;
            if (month > 12)
            {
                month = 1;
                year++;
            }
        }
    }

    private static IEnumerable<DateTime> InMonths(DateTime start, DateTime end, int[] months, int day)
    {
        for (var year = start.Year; year <= end.Year; year++)
        {
            foreach (var month in months)
            {
                var when = new DateTime(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)), 0, 0, 0, DateTimeKind.Utc);
                if (when >= start && when <= end)
                {
                    yield return when;
                }
            }
        }
    }

    private static DateTime Max(DateTime a, DateTime b, DateTime? c)
    {
        var result = a > b ? a : b;
        return c is not null && c.Value > result ? c.Value : result;
    }

    private static DateTime Min(DateTime a, DateTime b, DateTime? c)
    {
        var result = a < b ? a : b;
        return c is not null && c.Value < result ? c.Value : result;
    }
}
