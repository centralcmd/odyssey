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
        // One entry: the label names the fee, so the kind only has to say "this is a price, not a
        // rate". The hue is the old ManagementFee orange — it sits in the shared categorical band and
        // is the furthest of the four from both rate hues (200 and 295), which is what matters now
        // that it is the only fee colour on the surface.
        [TermKind.Fee]            = new("Fee",             TermGroup.Fee,  "receipt_long", "oklch(0.77 0.14 55)",  "oklch(0.77 0.14 55 / 0.15)",  TermValueUnit.Amount),
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
        TermKind.Fee => true,
        _ => false,
    };

    /// <summary>The term kinds permitted for an account type, in registry order.</summary>
    public static IReadOnlyList<TermKind> EligibleKinds(AccountType accountType) =>
        All.Where(k => IsEligible(k, accountType)).ToArray();

    public static bool IsLiability(AccountType accountType) =>
        AccountTypeVisuals.Group(accountType) == AccountGroup.Liability;

    /// <summary>Interest charged on a liability is a cost, so its rate is expense-colored — but only
    /// its color. The rate itself is never re-signed: a term renders with the sign the user entered,
    /// so a genuinely negative rate stays distinguishable from an ordinary one.</summary>
    public static bool IsCostRate(ExistingAccountTerm term, ExistingAccount account) =>
        term.ValueUnit == TermValueUnit.Percentage
        && term.TermKind == TermKind.InterestRate
        && IsLiability(account.AccountType);

    /// <summary>Expense color for a cost-rate, else <c>null</c> (the caller keeps its own color).</summary>
    public static string? CostColor(ExistingAccountTerm term, ExistingAccount account) =>
        IsCostRate(term, account) ? "var(--finance-expense)" : null;

    /// <summary>A term's kind label in the context of its account: a cost-rate reads "Interest
    /// charged", every other term keeps its registry label. The expense color must never be the only
    /// cue that a liability's interest is money out (WCAG 1.4.1 Use of Color) — the sign used to be
    /// the second cue, so the word carries it now. Pair this with <see cref="CostColor"/> wherever a
    /// value is tinted, the way a balance pairs its color with a signed amount.</summary>
    public static string LabelFor(ExistingAccountTerm term, ExistingAccount account) =>
        IsCostRate(term, account) ? "Interest charged" : Info(term.TermKind).Label;

    /// <summary>The direction glyph for a rate change, from the rate as stored. A liability's rising
    /// APR trends <em>up</em>: nothing re-signs a cost rate, which is what used to invert this.</summary>
    public static string DeltaIcon(decimal current, decimal previous) =>
        current > previous ? "arrow_upward"
        : current < previous ? "arrow_downward"
        : "remove";

    /// <summary>
    /// The key one term series resolves under — the kind plus its case-folded label. Mirrors
    /// <c>AccountTermService.GetCurrent</c>: a fee kind may hold several named series at once, so
    /// grouping on the kind alone would show whichever is dated later as if it had replaced the rest.
    /// </summary>
    public static (TermKind Kind, string? LabelKey) SeriesKey(ExistingAccountTerm term) =>
        (term.TermKind, TermLabel.KeyOf(term.Label));

    /// <summary>The name a term leads with: the author's own label, falling back to the kind wording.
    /// The fallback is <see cref="LabelFor"/> rather than the bare registry label, so an unlabelled
    /// cost rate still reads "Interest charged" (WCAG 1.4.1, issue #53). A rate kind is refused a
    /// label server-side, so in practice only the fallback arm can ever be a cost rate.</summary>
    public static string DisplayName(ExistingAccountTerm term, ExistingAccount account) =>
        string.IsNullOrWhiteSpace(term.Label) ? LabelFor(term, account) : TermLabel.Normalize(term.Label)!;

    /// <summary>0.0340 → "3.40%", 0.0003 → "0.03%" (trailing zeros trimmed above 1%).</summary>
    public static string PctStr(decimal frac)
    {
        var p = frac * 100m;
        var s = Math.Abs(p) < 1m
            ? p.ToString("0.00", CultureInfo.InvariantCulture)
            : p.ToString("0.00", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
        return $"{s}%";
    }

    /// <summary>A term's value as a display string, carrying the stored sign as entered: "6.49%" on a
    /// loan, "3.40%" on savings, "−0.5%" for a genuinely negative rate, or a money amount for fee
    /// amounts (formatted via <paramref name="money"/>).</summary>
    public static string FormatValue(ExistingAccountTerm term, Func<decimal, string?, string> money)
    {
        if (term.ValueUnit != TermValueUnit.Percentage)
            return money(term.Value, term.CurrencyCode);

        return (term.Value < 0 ? "−" : "") + PctStr(Math.Abs(term.Value));
    }
}
