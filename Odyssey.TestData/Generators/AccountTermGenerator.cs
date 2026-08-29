using Odyssey.Context;
using Odyssey.TestData.Catalog;
using static Odyssey.TestData.DemoDataDefaults;

namespace Odyssey.TestData.Generators;

/// <summary>
/// Deterministic time-versioned account terms (issue #172): interest rates, expected returns and
/// bank fees. The set deliberately exercises every <see cref="TermKind"/>, both
/// <see cref="TermValueUnit"/>s and several <see cref="BillingPeriod"/>s, and includes a couple of
/// rate histories (a savings rate climbing over the years) so the "current value" resolution and the
/// history listing both have something to show.
///
/// The shape mirrors the API's validation rules so the seeded data is one the service itself would
/// accept: rate kinds (InterestRate/ExpectedReturn) are percentages in the fraction range [-1, 1]
/// with no billing period and no currency; fee amounts carry a supported currency (defaulting to the
/// account currency) and may carry a billing period; eligibility per account type matches
/// <c>AccountTermService</c>. Accounts are referenced by their stable deterministic ids; none is
/// created here.
/// </summary>
public static class AccountTermGenerator
{
    private sealed record TermSpec(
        string AccountName,
        TermKind Kind,
        TermValueUnit Unit,
        decimal Value,
        DateTime EffectiveFrom,
        string? Currency = null,
        BillingPeriod? Billing = null,
        string? Note = null);

    public static Guid IdFor(string accountName, TermKind kind, DateTime effectiveFrom) =>
        DeterministicGuid.From($"account-term::{accountName}::{kind}@{effectiveFrom:yyyy-MM-dd}");

    public static List<AccountTerm> Build()
    {
        var specs = new List<TermSpec>
        {
            // Savings interest rate climbing over three years → demonstrates rate history + current resolution.
            new(Catalog.Accounts.EmergencyFund, TermKind.InterestRate, TermValueUnit.Percentage, 0.0150m, D(2023, 1, 1), Note: "Introductory savings rate."),
            new(Catalog.Accounts.EmergencyFund, TermKind.InterestRate, TermValueUnit.Percentage, 0.0250m, D(2024, 1, 1), Note: "Rate rise."),
            new(Catalog.Accounts.EmergencyFund, TermKind.InterestRate, TermValueUnit.Percentage, 0.0410m, D(2025, 6, 1), Note: "Current rate."),

            // High-yield savings (EUR): a single, higher current rate.
            new(Catalog.Accounts.HighYieldSavings, TermKind.InterestRate, TermValueUnit.Percentage, 0.0325m, D(2025, 11, 1), Note: "Promotional high-yield rate."),

            // Mortgage: a fixed rate set at origination.
            new(Catalog.Accounts.HomeMortgage, TermKind.InterestRate, TermValueUnit.Percentage, 0.0395m, D(2017, 9, 1), Note: "30-year fixed."),

            // Loans: interest rates.
            new(Catalog.Accounts.CarLoanVolvo, TermKind.InterestRate, TermValueUnit.Percentage, 0.0690m, D(2023, 2, 15), Note: "Auto loan APR."),
            new(Catalog.Accounts.RenovationPersonalLoan, TermKind.InterestRate, TermValueUnit.Percentage, 0.0810m, D(2024, 9, 1), Note: "Personal loan APR."),

            // Credit card: APR plus an annual fee (amount, billed annually).
            new(Catalog.Accounts.TravelRewardsCard, TermKind.InterestRate, TermValueUnit.Percentage, 0.1999m, D(2018, 5, 20), Note: "Purchase APR."),
            new(Catalog.Accounts.TravelRewardsCard, TermKind.ServiceFee, TermValueUnit.Amount, 95m, D(2018, 5, 20), Currency: Currencies.Usd, Billing: BillingPeriod.Annually, Note: "Annual card fee."),

            // Brokerage (investment): expected return + a percentage platform/management fee.
            new(Catalog.Accounts.BrokerageAccount, TermKind.ExpectedReturn, TermValueUnit.Percentage, 0.0700m, D(2016, 7, 1), Note: "Long-run expected annual return."),
            new(Catalog.Accounts.BrokerageAccount, TermKind.ManagementFee, TermValueUnit.Percentage, 0.0025m, D(2016, 7, 1), Note: "Platform fee."),

            // Pension: expected return + management fee.
            new(Catalog.Accounts.WorkplacePension, TermKind.ExpectedReturn, TermValueUnit.Percentage, 0.0500m, D(2016, 2, 10), Note: "Expected annual return."),
            new(Catalog.Accounts.WorkplacePension, TermKind.ManagementFee, TermValueUnit.Percentage, 0.0040m, D(2016, 2, 10), Note: "Scheme management charge."),

            // Stocks portfolio (SEK investment): percentage terms only (no currency needed).
            new(Catalog.Accounts.StocksPortfolio, TermKind.ExpectedReturn, TermValueUnit.Percentage, 0.0650m, D(2019, 11, 5), Note: "Expected annual return."),
            new(Catalog.Accounts.StocksPortfolio, TermKind.ManagementFee, TermValueUnit.Percentage, 0.0030m, D(2019, 11, 5), Note: "Custody fee."),

            // Everyday checking: a monthly service fee + a per-transaction fee (amounts, USD).
            new(Catalog.Accounts.EverydayChecking, TermKind.ServiceFee, TermValueUnit.Amount, 12m, D(2016, 4, 1), Currency: Currencies.Usd, Billing: BillingPeriod.Monthly, Note: "Monthly account maintenance fee."),
            new(Catalog.Accounts.EverydayChecking, TermKind.TransactionFee, TermValueUnit.Amount, 0.30m, D(2016, 4, 1), Currency: Currencies.Usd, Billing: BillingPeriod.PerTransaction, Note: "Per-transaction processing fee."),
        };

        return specs
            .Select(spec => new AccountTerm
            {
                AccountTermId = IdFor(spec.AccountName, spec.Kind, spec.EffectiveFrom),
                AccountId = Catalog.Accounts.IdFor(spec.AccountName),
                TermKind = spec.Kind,
                ValueUnit = spec.Unit,
                Value = spec.Value,
                // Percentage terms never carry a currency; amounts carry the account currency.
                CurrencyCode = spec.Unit == TermValueUnit.Percentage ? null : spec.Currency,
                BillingPeriod = spec.Billing,
                EffectiveFrom = spec.EffectiveFrom,
                Note = spec.Note,
                CreatedAtUtc = spec.EffectiveFrom,
            })
            .ToList();
    }

    private static DateTime D(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);
}
