using Odyssey.Context;
using Odyssey.TestData.Catalog;
// The one normalization rule, shared with the write path — aliased rather than imported wholesale,
// since Odyssey.Dtos.Finance also carries DTO twins of the entity enums used throughout this file.
using TermLabel = Odyssey.Dtos.Finance.TermLabel;
using static Odyssey.TestData.DemoDataDefaults;

namespace Odyssey.TestData.Generators;

/// <summary>
/// Deterministic time-versioned account terms (issue #172): interest rates, expected returns and
/// bank fees, every one of them a labelled term. The set deliberately exercises both
/// <see cref="TermValueUnit"/>s and several <see cref="Interval"/>s, and includes a couple of
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
/// accept: every term carries a label; percentages are fractions in the range [-1, 1] with no
/// currency; amounts carry a supported currency (defaulting to the account currency) and may carry a
/// cadence. Accounts are
/// referenced by their stable deterministic ids; none is created here.
/// </summary>
public static class TermGenerator
{
    private sealed record TermSpec(
        string AccountName,
        TermValueUnit Unit,
        decimal Value,
        DateTime EffectiveFrom,
        string? Label = null,
        string? Currency = null,
        Interval? Billing = null,
        int? Count = null,
        DateTime? Anchor = null,
        string? Note = null);

    /// <summary>
    /// The id of one seeded term. The label is part of the seed key because it IS the series key: two
    /// terms on one account can share a date, and would otherwise be handed the same deterministic id.
    /// </summary>
    public static Guid IdFor(string accountName, DateTime effectiveFrom, string? label = null) =>
        DeterministicGuid.From($"account-term::{accountName}::{TermLabel.Key(label) ?? ""}@{effectiveFrom:yyyy-MM-dd}");

    public static List<Term> Build()
    {
        var specs = new List<TermSpec>
        {
            // Savings interest rate climbing over three years → demonstrates rate history + current resolution.
            new(Catalog.Accounts.EmergencyFund, TermValueUnit.Percentage, 0.0150m, D(2023, 1, 1), Label: "Interest rate", Note: "Introductory savings rate."),
            new(Catalog.Accounts.EmergencyFund, TermValueUnit.Percentage, 0.0250m, D(2024, 1, 1), Label: "Interest rate", Note: "Rate rise."),
            new(Catalog.Accounts.EmergencyFund, TermValueUnit.Percentage, 0.0410m, D(2025, 6, 1), Label: "Interest rate", Note: "Current rate."),

            // High-yield savings (EUR): a single, higher current rate.
            new(Catalog.Accounts.HighYieldSavings, TermValueUnit.Percentage, 0.0325m, D(2025, 11, 1), Label: "Interest rate", Note: "Promotional high-yield rate."),

            // Mortgage: a fixed rate set at origination.
            new(Catalog.Accounts.HomeMortgage, TermValueUnit.Percentage, 0.0395m, D(2017, 9, 1), Label: "Interest rate", Note: "30-year fixed."),

            // Loans: interest rates.
            new(Catalog.Accounts.CarLoanVolvo, TermValueUnit.Percentage, 0.0690m, D(2023, 2, 15), Label: "Interest rate", Note: "Auto loan APR."),
            new(Catalog.Accounts.RenovationPersonalLoan, TermValueUnit.Percentage, 0.0810m, D(2024, 9, 1), Label: "Interest rate", Note: "Personal loan APR."),

            // Credit card: the purchase APR plus SIX named fees. Under one fee kind per category these
            // collapsed to two in-force values; named, they are six independent series — which is the
            // whole point of the label. "ATM withdrawal · abroad" carries two dates, so its later
            // entry supersedes only itself and the domestic charge beside it is untouched.
            new(Catalog.Accounts.TravelRewardsCard, TermValueUnit.Percentage, 0.1999m, D(2018, 5, 20), Label: "Interest rate", Note: "Purchase APR."),
            new(Catalog.Accounts.TravelRewardsCard, TermValueUnit.Amount, 95m, D(2018, 5, 20), Label: "Annual card fee", Currency: Currencies.Usd, Billing: Interval.Annually, Note: "Membership fee."),
            new(Catalog.Accounts.TravelRewardsCard, TermValueUnit.Percentage, 0.0275m, D(2018, 5, 20), Label: "Currency conversion", Billing: Interval.PerOccurrence, Note: "Markup on the network rate."),
            new(Catalog.Accounts.TravelRewardsCard, TermValueUnit.Amount, 5m, D(2018, 5, 20), Label: "ATM withdrawal · domestic", Currency: Currencies.Usd, Billing: Interval.PerOccurrence),
            new(Catalog.Accounts.TravelRewardsCard, TermValueUnit.Amount, 25m, D(2018, 5, 20), Label: "ATM withdrawal · abroad", Currency: Currencies.Usd, Billing: Interval.PerOccurrence),
            new(Catalog.Accounts.TravelRewardsCard, TermValueUnit.Amount, 30m, D(2024, 3, 1), Label: "ATM withdrawal · abroad", Currency: Currencies.Usd, Billing: Interval.PerOccurrence, Note: "Overseas network charge increase."),
            new(Catalog.Accounts.TravelRewardsCard, TermValueUnit.Amount, 15m, D(2018, 5, 20), Label: "Card replacement", Currency: Currencies.Usd, Billing: Interval.OneTime),
            new(Catalog.Accounts.TravelRewardsCard, TermValueUnit.Amount, 2m, D(2018, 5, 20), Label: "Paper statement", Currency: Currencies.Usd, Billing: Interval.Monthly),

            // Brokerage (investment): expected return + a percentage platform fee.
            new(Catalog.Accounts.BrokerageAccount, TermValueUnit.Percentage, 0.0700m, D(2016, 7, 1), Label: "Expected return", Note: "Long-run expected annual return."),
            new(Catalog.Accounts.BrokerageAccount, TermValueUnit.Percentage, 0.0025m, D(2016, 7, 1), Label: "Platform fee", Billing: Interval.Annually, Note: "Blended expense ratio."),

            // Pension: expected return + a scheme management charge.
            new(Catalog.Accounts.WorkplacePension, TermValueUnit.Percentage, 0.0500m, D(2016, 2, 10), Label: "Expected return", Note: "Expected annual return."),
            new(Catalog.Accounts.WorkplacePension, TermValueUnit.Percentage, 0.0040m, D(2016, 2, 10), Label: "Management charge", Billing: Interval.Annually, Note: "Scheme management charge."),

            // Stocks portfolio (SEK investment): percentage terms only (no currency needed).
            new(Catalog.Accounts.StocksPortfolio, TermValueUnit.Percentage, 0.0650m, D(2019, 11, 5), Label: "Expected return", Note: "Expected annual return."),
            new(Catalog.Accounts.StocksPortfolio, TermValueUnit.Percentage, 0.0030m, D(2019, 11, 5), Label: "Custody fee", Billing: Interval.Annually),

            // Per-unit custody charge — the unit itself is named by the label, not by a field.
            new(Catalog.Accounts.StocksPortfolio, TermValueUnit.Amount, 0.02m, D(2019, 11, 5), Label: "Custody · per share", Currency: Currencies.Sek, Billing: Interval.PerUnit, Note: "On shares held at month end."),

            // Everyday checking: a monthly maintenance fee + a per-occurrence fee (amounts, USD).
            new(Catalog.Accounts.EverydayChecking, TermValueUnit.Amount, 12m, D(2016, 4, 1), Label: "Account maintenance", Currency: Currencies.Usd, Billing: Interval.Monthly),
            new(Catalog.Accounts.EverydayChecking, TermValueUnit.Amount, 0.30m, D(2016, 4, 1), Label: "Transaction processing", Currency: Currencies.Usd, Billing: Interval.PerOccurrence),

            // A weekly cadence with a count of 2 — "every second week", which the old enum could not
            // express at all.
            new(Catalog.Accounts.EverydayChecking, TermValueUnit.Amount, 3m, D(2025, 6, 1), Label: "Cash handling · branch", Currency: Currencies.Usd, Billing: Interval.Weekly, Count: 2, Note: "Charged every second week the account is used at a counter."),

            // The case AnchorDate exists for: a quarterly charge (Monthly x 3 — what the retired
            // Quarterly value becomes) raised on the 1st but not first billed until the 15th.
            new(Catalog.Accounts.EverydayChecking, TermValueUnit.Amount, 45m, D(2026, 1, 1), Label: "Relationship service charge", Currency: Currencies.Usd, Billing: Interval.Monthly, Count: 3, Anchor: D(2026, 1, 15), Note: "Billed in arrears."),
        };

        return specs
            .Select(spec => new Term
            {
                TermId = IdFor(spec.AccountName, spec.EffectiveFrom, spec.Label),
                AccountId = Catalog.Accounts.IdFor(spec.AccountName),
                // Normalized through the same rule the write path uses, so the seed is a set the
                // service itself would have produced.
                Label = TermLabel.Normalize(spec.Label),
                LabelKey = TermLabel.Key(spec.Label),
                ValueUnit = spec.Unit,
                Value = spec.Value,
                // Percentage terms never carry a currency; amounts carry the account currency.
                CurrencyCode = spec.Unit == TermValueUnit.Percentage ? null : spec.Currency,
                Interval = spec.Billing,
                // Non-null IFF the unit is periodic — the invariant TermService enforces on write.
                IntervalCount = spec.Billing is { } cadence && cadence.IsPeriodic() ? spec.Count ?? 1 : null,
                AnchorDate = spec.Anchor,
                EffectiveFrom = spec.EffectiveFrom,
                Note = spec.Note,
                CreatedAtUtc = spec.EffectiveFrom,
            })
            .ToList();
    }

    private static DateTime D(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);
}
