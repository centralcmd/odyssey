using System.Globalization;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>A term kind's high-level grouping: an interest/return rate, or a service fee.</summary>
public enum TermGroup
{
    Rate,
    Fee,
}

/// <summary>How one <see cref="TermKind"/> renders everywhere — the summary tiles, the rate chart,
/// the history table, and the create/edit picker — so a term reads identically across the surface.
/// Mirrors the canonical term-kind registry in the Odyssey Design System (data.js · termKinds).
/// The category hues are deliberate oklch literals from the design system: they sit in the shared
/// categorical band (L~0.74–0.80) chosen to read in both light and dark themes, so — like the other
/// type registries (account / file / contact) — they are NOT tokenized.</summary>
public sealed record TermKindInfo(
    string Label,
    TermGroup Group,
    string Icon,
    string Color,
    string Soft,
    TermValueUnit DefaultUnit);

/// <summary>Display context for a <see cref="BillingPeriod"/> — the full label, a compact chip, and
/// the value suffix ("/mo", "/yr", …) shown beside a fee.</summary>
public sealed record BillingPeriodInfo(string Label, string Chip, string Suffix);

public static class TermKindVisuals
{
    private static readonly IReadOnlyDictionary<TermKind, TermKindInfo> Registry = new Dictionary<TermKind, TermKindInfo>
    {
        // ---- Rates ----
        [TermKind.InterestRate]   = new("Interest rate",   TermGroup.Rate, "percent",      "oklch(0.78 0.13 200)", "oklch(0.78 0.13 200 / 0.15)", TermValueUnit.Percentage),
        [TermKind.ExpectedReturn] = new("Expected return", TermGroup.Rate, "trending_up",  "oklch(0.72 0.16 295)", "oklch(0.72 0.16 295 / 0.15)", TermValueUnit.Percentage),
        // ---- Fees ----
        [TermKind.ManagementFee]  = new("Management fee",  TermGroup.Fee,  "pie_chart",    "oklch(0.77 0.14 55)",  "oklch(0.77 0.14 55 / 0.15)",  TermValueUnit.Percentage),
        [TermKind.ServiceFee]     = new("Service fee",     TermGroup.Fee,  "event_repeat", "oklch(0.76 0.13 225)", "oklch(0.76 0.13 225 / 0.15)", TermValueUnit.Amount),
        [TermKind.TransactionFee] = new("Transaction fee", TermGroup.Fee,  "swap_horiz",   "oklch(0.75 0.16 330)", "oklch(0.75 0.16 330 / 0.15)", TermValueUnit.Amount),
        [TermKind.OtherFee]       = new("Other fee",       TermGroup.Fee,  "receipt_long", "oklch(0.74 0.02 250)", "oklch(0.74 0.02 250 / 0.15)", TermValueUnit.Amount),
    };

    /// <summary>Term kinds in registry order (rates first), excluding <see cref="TermKind.Unknown"/>.</summary>
    public static readonly IReadOnlyList<TermKind> All = Registry.Keys.ToArray();

    public static TermKindInfo Info(TermKind kind) =>
        Registry.TryGetValue(kind, out var info)
            ? info
            : new TermKindInfo(kind.ToString(), TermGroup.Fee, "sell", "var(--mud-palette-text-secondary)", "var(--mud-palette-action-default-hover)", TermValueUnit.Amount);

    private static readonly IReadOnlyDictionary<BillingPeriod, BillingPeriodInfo> Billing = new Dictionary<BillingPeriod, BillingPeriodInfo>
    {
        [BillingPeriod.OneTime]        = new("One-time", "One-time", ""),
        [BillingPeriod.PerTransaction] = new("Per transaction", "Per txn", "/txn"),
        [BillingPeriod.Daily]          = new("Daily", "Daily", "/day"),
        [BillingPeriod.Monthly]        = new("Monthly", "Monthly", "/mo"),
        [BillingPeriod.Quarterly]      = new("Quarterly", "Quarterly", "/qtr"),
        [BillingPeriod.Annually]       = new("Annually", "Annually", "/yr"),
    };

    /// <summary>All billing periods in enum order, for the dialog's period picker.</summary>
    public static readonly IReadOnlyList<BillingPeriod> BillingPeriods = Enum.GetValues<BillingPeriod>();

    public static BillingPeriodInfo? BillingInfo(BillingPeriod? period) =>
        period is { } p && Billing.TryGetValue(p, out var info) ? info : null;

    // Eligibility matrix — mirrors the backend (AccountTermService): interest only on
    // interest-bearing accounts, expected return on investment/pension, fees on every type.
    private static readonly IReadOnlySet<AccountType> InterestRateTypes = new HashSet<AccountType>
    {
        AccountType.CheckingAccount, AccountType.SavingsAccount, AccountType.PensionAccount,
        AccountType.CreditCard, AccountType.Mortgage, AccountType.StudentLoan,
        AccountType.PersonalLoan, AccountType.CarLoan, AccountType.TaxDebt,
    };

    private static readonly IReadOnlySet<AccountType> ExpectedReturnTypes = new HashSet<AccountType>
    {
        AccountType.InvestmentAccount, AccountType.PensionAccount,
    };

    public static bool IsEligible(TermKind kind, AccountType accountType) => kind switch
    {
        TermKind.InterestRate => InterestRateTypes.Contains(accountType),
        TermKind.ExpectedReturn => ExpectedReturnTypes.Contains(accountType),
        TermKind.ManagementFee or TermKind.ServiceFee or TermKind.TransactionFee or TermKind.OtherFee => true,
        _ => false,
    };

    /// <summary>The term kinds permitted for an account type, in registry order.</summary>
    public static IReadOnlyList<TermKind> EligibleKinds(AccountType accountType) =>
        All.Where(k => IsEligible(k, accountType)).ToArray();

    public static bool IsLiability(AccountType accountType) =>
        AccountTypeVisuals.Group(accountType) == AccountGroup.Liability;

    /// <summary>Interest charged on a liability is a cost: its rate reads negative + expense-colored,
    /// mirroring how the account balance is shown. Earned interest and expected return stay positive;
    /// fees keep their own price framing.</summary>
    public static bool IsCostRate(ExistingAccountTerm term, ExistingAccount account) =>
        term.ValueUnit == TermValueUnit.Percentage
        && term.TermKind == TermKind.InterestRate
        && IsLiability(account.AccountType);

    /// <summary>Expense color for a cost-rate, else <c>null</c> (the caller keeps its own color).</summary>
    public static string? CostColor(ExistingAccountTerm term, ExistingAccount account) =>
        IsCostRate(term, account) ? "var(--finance-expense)" : null;

    /// <summary>The percentage value with the cost sign applied (for the chart + deltas).</summary>
    public static decimal SignedValue(ExistingAccountTerm term, ExistingAccount account) =>
        IsCostRate(term, account) ? -Math.Abs(term.Value) : term.Value;

    /// <summary>0.0340 → "3.40%", 0.0003 → "0.03%" (trailing zeros trimmed above 1%).</summary>
    public static string PctStr(decimal frac)
    {
        var p = frac * 100m;
        var s = Math.Abs(p) < 1m
            ? p.ToString("0.00", CultureInfo.InvariantCulture)
            : p.ToString("0.00", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
        return $"{s}%";
    }

    /// <summary>A term's value as a display string, signed for cost-rates: "−6.49%" on a loan,
    /// "3.40%" on savings, or a money amount for fee amounts (formatted via <paramref name="money"/>).</summary>
    public static string FormatValue(ExistingAccountTerm term, ExistingAccount account, Func<decimal, string?, string> money)
    {
        if (term.ValueUnit != TermValueUnit.Percentage)
            return money(term.Value, term.CurrencyCode);

        var v = SignedValue(term, account);
        return (v < 0 ? "−" : "") + PctStr(Math.Abs(v));
    }
}
