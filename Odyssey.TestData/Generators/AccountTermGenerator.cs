using Odyssey.Context;
using Odyssey.TestData.Catalog;
// The one normalization rule, shared with the write path — aliased rather than imported wholesale,
// since Odyssey.Dtos.Finance also carries DTO twins of the entity enums used throughout this file.
using TermLabel = Odyssey.Dtos.Finance.TermLabel;
using static Odyssey.TestData.DemoDataDefaults;

namespace Odyssey.TestData.Generators;

/// <summary>
/// Deterministic time-versioned account terms (issue #172): interest rates, expected returns and
/// bank fees. The set deliberately exercises every <see cref="TermKind"/>, both
/// <see cref="TermValueUnit"/>s and several <see cref="BillingPeriod"/>s, and includes a couple of
/// rate histories (a savings rate climbing over the years) so the "current value" resolution and the
/// history listing both have something to show.
///
/// <para>
/// Every fee is named by its own <c>Label</c>, which is what makes the travel card's six charges six
/// separate series — before labels they were four kinds that collapsed to two in-force values, and
/// the rest were silently superseded. Two of the card's ATM fees share a label across two dates, so
/// per-series supersession has something to demonstrate too.
/// </para>
///
/// The shape mirrors the API's validation rules so the seeded data is one the service itself would
/// accept: rate kinds (InterestRate/ExpectedReturn) are percentages in the fraction range [-1, 1]
/// with no billing period, no currency and no label; every <see cref="TermKind.Fee"/> carries a
/// label; fee amounts carry a supported currency (defaulting to the account currency) and may carry a
/// billing period; eligibility per account type matches <c>AccountTermService</c>. Accounts are
/// referenced by their stable deterministic ids; none is created here.
/// </summary>
public static class AccountTermGenerator
{
    private sealed record TermSpec(
        string AccountName,
        TermKind Kind,
        TermValueUnit Unit,
        decimal Value,
        DateTime EffectiveFrom,
        string? Label = null,
        string? Currency = null,
        BillingPeriod? Billing = null,
        string? Note = null);

    /// <summary>
    /// The id of one seeded term. The label is part of the seed key because it is part of the series
    /// key: two fees on one account can now share a kind and a date, and would otherwise be handed
    /// the same deterministic id.
    /// </summary>
    public static Guid IdFor(string accountName, TermKind kind, DateTime effectiveFrom, string? label = null) =>
        DeterministicGuid.From($"account-term::{accountName}::{kind}::{TermLabel.Key(label) ?? ""}@{effectiveFrom:yyyy-MM-dd}");

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

            // Credit card: the purchase APR plus SIX named fees. Under one fee kind per category these
            // collapsed to two in-force values; named, they are six independent series — which is the
            // whole point of the label. "ATM withdrawal · abroad" carries two dates, so its later
            // entry supersedes only itself and the domestic charge beside it is untouched.
            new(Catalog.Accounts.TravelRewardsCard, TermKind.InterestRate, TermValueUnit.Percentage, 0.1999m, D(2018, 5, 20), Note: "Purchase APR."),
            new(Catalog.Accounts.TravelRewardsCard, TermKind.Fee, TermValueUnit.Amount, 95m, D(2018, 5, 20), Label: "Annual card fee", Currency: Currencies.Usd, Billing: BillingPeriod.Annually, Note: "Membership fee."),
            new(Catalog.Accounts.TravelRewardsCard, TermKind.Fee, TermValueUnit.Percentage, 0.0275m, D(2018, 5, 20), Label: "Currency conversion", Billing: BillingPeriod.PerTransaction, Note: "Markup on the network rate."),
            new(Catalog.Accounts.TravelRewardsCard, TermKind.Fee, TermValueUnit.Amount, 5m, D(2018, 5, 20), Label: "ATM withdrawal · domestic", Currency: Currencies.Usd, Billing: BillingPeriod.PerTransaction),
            new(Catalog.Accounts.TravelRewardsCard, TermKind.Fee, TermValueUnit.Amount, 25m, D(2018, 5, 20), Label: "ATM withdrawal · abroad", Currency: Currencies.Usd, Billing: BillingPeriod.PerTransaction),
            new(Catalog.Accounts.TravelRewardsCard, TermKind.Fee, TermValueUnit.Amount, 30m, D(2024, 3, 1), Label: "ATM withdrawal · abroad", Currency: Currencies.Usd, Billing: BillingPeriod.PerTransaction, Note: "Overseas network charge increase."),
            new(Catalog.Accounts.TravelRewardsCard, TermKind.Fee, TermValueUnit.Amount, 15m, D(2018, 5, 20), Label: "Card replacement", Currency: Currencies.Usd, Billing: BillingPeriod.OneTime),
            new(Catalog.Accounts.TravelRewardsCard, TermKind.Fee, TermValueUnit.Amount, 2m, D(2018, 5, 20), Label: "Paper statement", Currency: Currencies.Usd, Billing: BillingPeriod.Monthly),

            // Brokerage (investment): expected return + a percentage platform fee.
            new(Catalog.Accounts.BrokerageAccount, TermKind.ExpectedReturn, TermValueUnit.Percentage, 0.0700m, D(2016, 7, 1), Note: "Long-run expected annual return."),
            new(Catalog.Accounts.BrokerageAccount, TermKind.Fee, TermValueUnit.Percentage, 0.0025m, D(2016, 7, 1), Label: "Platform fee", Billing: BillingPeriod.Annually, Note: "Blended expense ratio."),

            // Pension: expected return + a scheme management charge.
            new(Catalog.Accounts.WorkplacePension, TermKind.ExpectedReturn, TermValueUnit.Percentage, 0.0500m, D(2016, 2, 10), Note: "Expected annual return."),
            new(Catalog.Accounts.WorkplacePension, TermKind.Fee, TermValueUnit.Percentage, 0.0040m, D(2016, 2, 10), Label: "Management charge", Billing: BillingPeriod.Annually, Note: "Scheme management charge."),

            // Stocks portfolio (SEK investment): percentage terms only (no currency needed).
            new(Catalog.Accounts.StocksPortfolio, TermKind.ExpectedReturn, TermValueUnit.Percentage, 0.0650m, D(2019, 11, 5), Note: "Expected annual return."),
            new(Catalog.Accounts.StocksPortfolio, TermKind.Fee, TermValueUnit.Percentage, 0.0030m, D(2019, 11, 5), Label: "Custody fee", Billing: BillingPeriod.Annually),

            // Everyday checking: a monthly maintenance fee + a per-transaction fee (amounts, USD).
            new(Catalog.Accounts.EverydayChecking, TermKind.Fee, TermValueUnit.Amount, 12m, D(2016, 4, 1), Label: "Account maintenance", Currency: Currencies.Usd, Billing: BillingPeriod.Monthly),
            new(Catalog.Accounts.EverydayChecking, TermKind.Fee, TermValueUnit.Amount, 0.30m, D(2016, 4, 1), Label: "Transaction processing", Currency: Currencies.Usd, Billing: BillingPeriod.PerTransaction),
        };

        return specs
            .Select(spec => new AccountTerm
            {
                AccountTermId = IdFor(spec.AccountName, spec.Kind, spec.EffectiveFrom, spec.Label),
                AccountId = Catalog.Accounts.IdFor(spec.AccountName),
                TermKind = spec.Kind,
                // Normalized through the same rule the write path uses, so the seed is a set the
                // service itself would have produced.
                Label = TermLabel.Normalize(spec.Label),
                LabelKey = TermLabel.Key(spec.Label),
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
