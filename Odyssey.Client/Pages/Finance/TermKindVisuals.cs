using System.Globalization;
using Odyssey.Client.Components;
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

/// <summary>
/// Display context for an <see cref="Interval"/> — the picker label, whether the unit is
/// <paramref name="Periodic"/> (the one condition the whole cadence UI hangs off), the adverb a
/// count of one reads as ("monthly"), and the singular/plural unit noun a higher count reads as
/// ("every 3 <em>months</em>").
/// </summary>
/// <remarks>
/// Mirrors the design system's <c>OdysseyData.intervals</c> registry (data.js). <c>Chip</c> and
/// <c>Suffix</c> are gone with <c>BillingPeriod</c>: a cadence is now two fields, and "/mo" cannot
/// say "every 3 months", so every surface words it through
/// <see cref="TermKindVisuals.CadenceText"/> instead.
/// </remarks>
public sealed record IntervalInfo(string Label, bool Periodic, string Adverb, string One, string Many);

public static class TermKindVisuals
{
    private static readonly IReadOnlyDictionary<TermKind, TermKindInfo> Registry = new Dictionary<TermKind, TermKindInfo>
    {
        // ---- Rates: each is a distinct quoted number some surface must single out ----
        [TermKind.InterestRate]   = new("Interest rate",   TermGroup.Rate, "percent",      "oklch(0.78 0.13 200)", "oklch(0.78 0.13 200 / 0.15)", TermValueUnit.Percentage),
        [TermKind.ExpectedReturn] = new("Expected return", TermGroup.Rate, "trending_up",  "oklch(0.72 0.16 295)", "oklch(0.72 0.16 295 / 0.15)", TermValueUnit.Percentage),
        // ---- Fee: one kind, named by the term's own label ----
        [TermKind.Fee]            = new("Fee",             TermGroup.Fee,  "receipt_long", "oklch(0.77 0.14 55)",  "oklch(0.77 0.14 55 / 0.15)",  TermValueUnit.Amount),
    };

    /// <summary>Term kinds in registry order (rates first), excluding <see cref="TermKind.Unknown"/>.</summary>
    public static readonly IReadOnlyList<TermKind> All = Registry.Keys.ToArray();

    public static TermKindInfo Info(TermKind kind) =>
        Registry.TryGetValue(kind, out var info)
            ? info
            : new TermKindInfo(kind.ToString(), TermGroup.Fee, "sell", "var(--mud-palette-text-secondary)", "var(--mud-palette-action-default-hover)", TermValueUnit.Amount);

    /// <summary>
    /// The cadence units, in READING order rather than ordinal order — the occasions first, then the
    /// rhythms shortest to longest. The ordinals are deliberately out of sequence (<c>PerUnit</c> is
    /// 6 and <c>Weekly</c> is 7, while 4 stays retired), so ordering by them would put the picker in
    /// an order no reader expects.
    /// </summary>
    private static readonly IReadOnlyList<KeyValuePair<Interval, IntervalInfo>> Intervals =
    [
        new(Interval.OneTime,       new("One-time",       Periodic: false, "one-time",       "", "")),
        new(Interval.PerOccurrence, new("Per occurrence", Periodic: false, "per occurrence", "", "")),
        new(Interval.PerUnit,       new("Per unit",       Periodic: false, "per unit",       "", "")),
        new(Interval.Daily,         new("Daily",          Periodic: true,  "daily",          "day",   "days")),
        new(Interval.Weekly,        new("Weekly",         Periodic: true,  "weekly",         "week",  "weeks")),
        new(Interval.Monthly,       new("Monthly",        Periodic: true,  "monthly",        "month", "months")),
        new(Interval.Annually,      new("Annually",       Periodic: true,  "annually",       "year",  "years")),
    ];

    private static readonly IReadOnlyDictionary<Interval, IntervalInfo> IntervalRegistry =
        Intervals.ToDictionary(entry => entry.Key, entry => entry.Value);

    /// <summary>All cadence units in reading order, for the dialog's interval picker.</summary>
    public static readonly IReadOnlyList<Interval> AllIntervals = [.. Intervals.Select(entry => entry.Key)];

    /// <summary>
    /// The display context for a cadence unit, or <c>null</c> when it is unset or undefined — a
    /// stale row holding the retired ordinal renders no cadence rather than borrowing another
    /// unit's wording. Named <c>InfoFor</c>, not <c>IntervalInfo</c>: a method sharing its return
    /// type's identifier compiles but reads as a constructor call at every call site.
    /// </summary>
    public static IntervalInfo? InfoFor(Interval? interval) =>
        interval is { } value && IntervalRegistry.TryGetValue(value, out var info) ? info : null;

    /// <summary>
    /// Whether a count is meaningful for this unit. The non-periodic units name an occasion rather
    /// than a rhythm, so "how many of them between charges" has no meaning — which is why the count
    /// field is absent, not merely disabled, whenever this is false.
    /// </summary>
    public static bool IsPeriodic(Interval? interval) => InfoFor(interval) is { Periodic: true };

    /// <summary>
    /// The cadence in words — the ONE place an interval and its count become copy, so a tile, a table
    /// row and a dialog can never word the same term differently.
    /// </summary>
    /// <remarks>
    /// <c>(Monthly, 1)</c> → "monthly"; <c>(Monthly, 3)</c> → "every 3 months";
    /// <c>(Weekly, 2)</c> → "every 2 weeks"; <c>(PerUnit, null)</c> → "per unit".
    /// A one-time charge and an unset interval carry no cadence at all and return <c>null</c>, which
    /// is what lets every caller render the result unconditionally.
    /// </remarks>
    public static string? CadenceText(Interval? interval, int? count)
    {
        if (InfoFor(interval) is not { } info || interval == Interval.OneTime)
            return null;

        if (!info.Periodic)
            return info.Adverb;

        var every = count ?? 1;
        return every > 1 ? $"every {every} {info.Many}" : info.Adverb;
    }

    /// <summary>The cadence of a term as stored — the shape every read surface calls.</summary>
    public static string? CadenceText(ExistingTerm term) => CadenceText(term.Interval, term.IntervalCount);

    /// <summary>The cadence unit a new fee opens on. One honest default: with a single fee kind
    /// there is nothing left to guess from, and the four kind-specific guesses were wrong three times
    /// in four.</summary>
    public const Interval DefaultFeeInterval = Interval.Monthly;

    // Eligibility matrix — mirrors the backend (TermService): interest only on
    // interest-bearing accounts, expected return on investment/pension, Fee on every type.
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

    /// <summary>
    /// The term kinds permitted on a CONTRACT, in registry order — <see cref="TermKind.Fee"/> and
    /// <see cref="TermKind.InterestRate"/> on every contract type. Mirrors the backend
    /// (<c>TermService.ContractTermKinds</c>): <see cref="TermKind.ExpectedReturn"/> prices invested
    /// principal, which a contract does not hold, so it is not offered and would be a 400 if posted.
    /// There is no per-<c>ContractType</c> matrix — the four values are coarse and none of them is
    /// financing-specific, so one would be arbitrary rather than informative.
    /// </summary>
    public static readonly IReadOnlyList<TermKind> ContractEligibleKinds =
        All.Where(k => k is TermKind.Fee or TermKind.InterestRate).ToArray();

    /// <summary>Whether a kind may be written on a contract.</summary>
    public static bool IsEligibleOnContract(TermKind kind) => ContractEligibleKinds.Contains(kind);

    public static bool IsLiability(AccountType accountType) =>
        AccountTypeVisuals.Group(accountType) == AccountGroup.Liability;

    /// <summary>Interest charged on a liability is a cost, so its rate is expense-colored — but only
    /// its color. The rate itself is never re-signed: a term renders with the sign the user entered,
    /// so a genuinely negative rate stays distinguishable from an ordinary one.</summary>
    /// <remarks>
    /// <paramref name="account"/> is nullable because the same helpers serve a CONTRACT-owned term
    /// (issue #135), which has no account type and therefore no liability notion: a contract's
    /// interest rate is never re-worded as a cost. One nullable context rather than a second copy of
    /// the wording — an unlabelled rate must read identically wherever it is shown.
    /// </remarks>
    public static bool IsCostRate(ExistingTerm term, ExistingAccount? account) =>
        account is not null
        && term.ValueUnit == TermValueUnit.Percentage
        && term.TermKind == TermKind.InterestRate
        && IsLiability(account.AccountType);

    /// <summary>Expense color for a cost-rate, else <c>null</c> (the caller keeps its own color).</summary>
    public static string? CostColor(ExistingTerm term, ExistingAccount? account) =>
        IsCostRate(term, account) ? "var(--finance-expense)" : null;

    /// <summary>A term's kind label in the context of its account: a cost-rate reads "Interest
    /// charged", every other term keeps its registry label. The expense color must never be the only
    /// cue that a liability's interest is money out (WCAG 1.4.1 Use of Color) — the sign used to be
    /// the second cue, so the word carries it now. Pair this with <see cref="CostColor"/> wherever a
    /// value is tinted, the way a balance pairs its color with a signed amount.</summary>
    public static string LabelFor(ExistingTerm term, ExistingAccount? account) =>
        IsCostRate(term, account) ? "Interest charged" : Info(term.TermKind).Label;

    /// <summary>What a term is CALLED: its own label where it has one, else its kind wording. The
    /// fallback is <see cref="LabelFor"/> and not the bare registry label, so an unlabelled interest
    /// rate on a liability still reads "Interest charged" — reaching for <c>Info(kind).Label</c> here
    /// would undo that non-colour cue silently, on a surface that still looks right for every other
    /// term. A rate is refused a label, so a cost rate can only ever take the fallback arm.</summary>
    public static string DisplayName(ExistingTerm term, ExistingAccount? account) =>
        TermLabel.Normalize(term.Label) ?? LabelFor(term, account);

    /// <summary>Whether a term carries a label, and so renders its kind wording as a caption beneath
    /// its name rather than as the name itself.</summary>
    public static bool IsLabelled(ExistingTerm term) =>
        TermLabel.Normalize(term.Label) is not null;

    /// <summary>The direction glyph for a rate change, from the rate as stored. A liability's rising
    /// APR trends <em>up</em>: nothing re-signs a cost rate, which is what used to invert this.</summary>
    public static string DeltaIcon(decimal current, decimal previous) =>
        current > previous ? "arrow_upward"
        : current < previous ? "arrow_downward"
        : "remove";

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
    public static string FormatValue(ExistingTerm term, Func<decimal, string?, string> money)
    {
        if (term.ValueUnit != TermValueUnit.Percentage)
            return money(term.Value, term.CurrencyCode);

        return (term.Value < 0 ? "−" : "") + PctStr(Math.Abs(term.Value));
    }

    // ---- Direction (issue #159) -------------------------------------------------------------

    /// <summary>
    /// Whether direction MEANS something here: a <see cref="TermKind.Fee"/> owned by a CONTRACT.
    /// A rate kind is a percentage the roll-up never projects, and an account term has no surface
    /// that reads a direction — the server refuses <see cref="TermDirection.Incoming"/> on both with
    /// a <c>400</c>, so neither is offered one.
    /// </summary>
    /// <remarks>
    /// ONE predicate, so the dialog's control, the read surfaces and the refusal copy can never
    /// disagree about where a direction is a fact and where it is noise.
    /// </remarks>
    public static bool DirectionApplies(TermKind kind, bool isContractOwned) =>
        isContractOwned && kind == TermKind.Fee;

    /// <inheritdoc cref="DirectionApplies(TermKind, bool)"/>
    public static bool DirectionApplies(ExistingTerm term) =>
        DirectionApplies(term.TermKind, term.ContractId is not null);

    /// <summary>
    /// Why direction is refused here, in the words the <c>400</c> uses; <c>null</c> when it is
    /// allowed. The copy is the server's rule restated, so a user never meets a refusal the dialog
    /// did not predict.
    /// </summary>
    public static string? DirectionRefusal(TermKind kind, bool isContractOwned) =>
        !isContractOwned
            ? "Direction applies to a contract term. An account term is always money out."
            : kind != TermKind.Fee
                ? "Direction applies to a fee term only — a rate is a percentage, not a movement."
                : null;

    /// <summary>
    /// Mint wherever an INCOMING term's own value is printed; <c>null</c> everywhere else, so every
    /// surface keeps the colour it already had and nothing that existed before this field changes
    /// appearance.
    /// </summary>
    public static string? DirectionColor(ExistingTerm term) =>
        DirectionApplies(term) && term.Direction == TermDirection.Incoming
            ? "var(--finance-income)"
            : null;
}

/// <summary>
/// How one <see cref="TermDirection"/> renders — its word, its short form for the slot a sign would
/// occupy, and the finance hue of its side. Mirrors the design system's <c>termDirections</c>
/// registry (data.js).
/// </summary>
/// <param name="Label">The word a read surface states ("Incoming").</param>
/// <param name="Short">The two- or three-letter form for the money field's lead ("in").</param>
/// <param name="Sentence">What the direction MEANS, for the dialog's helper line.</param>
/// <param name="Tone">"income" / "expense" — the finance semantics, never the brand hues.</param>
public sealed record TermDirectionInfo(string Label, string Short, string Sentence, string Tone)
{
    /// <summary>The colour of this side's figures.</summary>
    public string Color => Tone == "income" ? "var(--finance-income)" : "var(--finance-expense)";
}

/// <summary>
/// The <see cref="TermDirection"/> registry (issue #159) — which way the money moves, stated from the
/// HOUSEHOLD's perspective and never from either named party's.
/// </summary>
/// <remarks>
/// <para>
/// <b>NO GLYPH, deliberately.</b> Every arrow reads against VALUE rather than against the household:
/// an up arrow says "gain" before it says "leaves here", and a down arrow says "loss". The
/// unambiguous pairs are emoji, which product chrome bars. So direction is carried by its WORD plus
/// the finance hue — which is what a reader parses first anyway.
/// </para>
/// <para>
/// <b>Read with a DEFAULT, never a truthiness test.</b> A row written before the field existed, and
/// every account term, is <see cref="TermDirection.Outgoing"/>.
/// </para>
/// </remarks>
public static class TermDirectionVisuals
{
    private static readonly IReadOnlyDictionary<TermDirection, TermDirectionInfo> Registry =
        new Dictionary<TermDirection, TermDirectionInfo>
        {
            [TermDirection.Outgoing] = new("Outgoing", "out", "money leaves the household", "expense"),
            [TermDirection.Incoming] = new("Incoming", "in", "money arrives", "income"),
        };

    /// <summary>Both directions in registry order — Outgoing (the default) first.</summary>
    public static readonly IReadOnlyList<TermDirection> All = [TermDirection.Outgoing, TermDirection.Incoming];

    public static TermDirectionInfo Info(TermDirection direction) =>
        Registry.TryGetValue(direction, out var info) ? info : Registry[TermDirection.Outgoing];

    /// <summary>
    /// The pair as a money / amount field's LEAD — each state's own short word in the slot a sign
    /// would occupy, since the record stores a direction and not a sign.
    /// </summary>
    public static readonly IReadOnlyList<OdsDirectionOption> LeadOptions =
        [.. All.Select(d =>
        {
            var info = Info(d);
            return new OdsDirectionOption
            {
                Value = d.ToString(),
                Label = info.Label,
                Short = info.Short,
                Tone = info.Tone,
            };
        })];

    /// <summary>Parses the lead's string value back to the enum; anything unrecognised is the default.</summary>
    public static TermDirection Parse(string? value) =>
        Enum.TryParse<TermDirection>(value, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : TermDirection.Outgoing;
}
