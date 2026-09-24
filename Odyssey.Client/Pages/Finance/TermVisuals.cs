using System.Globalization;
using Odyssey.Client.Components;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>How a term renders everywhere — the summary tiles, the history table, the chart and the
/// create/edit dialog — so a term reads identically across the surface. There is no kind taxonomy: the
/// glyph and hue follow the term's <see cref="TermValueUnit"/>, so a rate and a price still read apart
/// at a glance (the design system's <c>OdysseyData.termUnits</c>). The hues are deliberate oklch
/// literals in the shared categorical band (L~0.74–0.80) chosen to read in both light and dark themes,
/// so — like the other type registries (account / file / contact) — they are NOT tokenized.</summary>
public sealed record TermInfo(string Label, string Icon, string Color, string Soft);

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
/// <see cref="TermVisuals.CadenceText"/> instead.
/// </remarks>
public sealed record IntervalInfo(string Label, bool Periodic, string Adverb, string One, string Many);

public static class TermVisuals
{
    /// <summary>What an unnamed term reads as — only a row predating the required label.</summary>
    public const string DefaultName = "Term";

    private static readonly TermInfo PercentageInfo =
        new("Percentage", "percent", "oklch(0.78 0.13 200)", "oklch(0.78 0.13 200 / 0.15)");

    private static readonly TermInfo AmountInfo =
        new("Amount", "payments", "oklch(0.77 0.14 55)", "oklch(0.77 0.14 55 / 0.15)");

    /// <summary>The glyph and hue for a unit. An undefined value reads as an amount, the default unit.</summary>
    public static TermInfo UnitInfo(TermValueUnit unit) =>
        unit == TermValueUnit.Percentage ? PercentageInfo : AmountInfo;

    /// <summary>The glyph and hue a term renders with — its unit's.</summary>
    public static TermInfo Info(ExistingTerm term) => UnitInfo(term.ValueUnit);

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

    /// <summary>The cadence unit a new term opens on.</summary>
    public const Interval DefaultInterval = Interval.Monthly;

    /// <summary>What a term is CALLED: its own label. Every term carries one; a row predating that
    /// rule reads as the plain noun rather than as blank.</summary>
    public static string DisplayName(ExistingTerm term) =>
        TermLabel.Normalize(term.Label) ?? DefaultName;

    /// <summary>0.0340 → "3.40%", 0.0003 → "0.03%" (trailing zeros trimmed above 1%).</summary>
    public static string PctStr(decimal frac)
    {
        var p = frac * 100m;
        var s = Math.Abs(p) < 1m
            ? p.ToString("0.00", CultureInfo.InvariantCulture)
            : p.ToString("0.00", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
        return $"{s}%";
    }

    /// <summary>A term's value as a display string, carrying the stored sign as entered: "6.49%",
    /// "−0.5%" for a genuinely negative percentage, or a money amount (formatted via
    /// <paramref name="money"/>).</summary>
    public static string FormatValue(ExistingTerm term, Func<decimal, string?, string> money)
    {
        if (term.ValueUnit != TermValueUnit.Percentage)
            return money(term.Value, term.CurrencyCode);

        return (term.Value < 0 ? "−" : "") + PctStr(Math.Abs(term.Value));
    }

    // ---- Direction (issue #159) -------------------------------------------------------------

    /// <summary>
    /// Whether direction MEANS something here: a term owned by a CONTRACT. An account term has no
    /// surface that reads a direction — the server refuses <see cref="TermDirection.Incoming"/> there
    /// with a <c>400</c>, so it is not offered one.
    /// </summary>
    /// <remarks>
    /// ONE predicate, so the dialog's control, the read surfaces and the refusal copy can never
    /// disagree about where a direction is a fact and where it is noise.
    /// </remarks>
    public static bool DirectionApplies(bool isContractOwned) => isContractOwned;

    /// <inheritdoc cref="DirectionApplies(bool)"/>
    public static bool DirectionApplies(ExistingTerm term) => DirectionApplies(term.ContractId is not null);

    /// <summary>
    /// Why direction is refused here, in the words the <c>400</c> uses; <c>null</c> when it is
    /// allowed. The copy is the server's rule restated, so a user never meets a refusal the dialog
    /// did not predict.
    /// </summary>
    public static string? DirectionRefusal(bool isContractOwned) =>
        isContractOwned
            ? null
            : "Direction applies to a contract term. An account term is always money out.";

    /// <summary>
    /// Whether this term brings money IN — direction applies here AND it is
    /// <see cref="TermDirection.Incoming"/>. A predicate of its own rather than a null-test on
    /// <see cref="DirectionColor"/>: a caller that wants the fact should ask for the fact, or a later
    /// change to what the colour helper returns silently changes what the caller counts.
    /// </summary>
    public static bool IsIncoming(ExistingTerm term) =>
        DirectionApplies(term) && term.Direction == TermDirection.Incoming;

    /// <summary>
    /// The finance hue of the term's direction wherever direction is stated — coral out, mint in, on
    /// every contract term — and <c>null</c> on an account term, which keeps its unit hue. One helper,
    /// so the tiles, the table rows and the chart cannot disagree about which figure is which colour.
    /// </summary>
    public static string? DirectionColor(ExistingTerm term) =>
        DirectionApplies(term) ? TermDirectionVisuals.Info(term.Direction).Color : null;

    /// <summary>The soft ground behind a directed term's glyph; <c>null</c> where direction does not
    /// apply.</summary>
    public static string? DirectionSoft(ExistingTerm term) =>
        DirectionApplies(term) ? TermDirectionVisuals.Info(term.Direction).Soft : null;

    /// <summary>The figure's colour: its direction's hue where one is stated, else its unit's.</summary>
    public static string ValueColor(ExistingTerm term) => DirectionColor(term) ?? Info(term).Color;

    /// <summary>The glyph's colour and ground — the direction's where one is stated, else the unit's.</summary>
    public static (string Color, string Soft) IconColors(ExistingTerm term) =>
        DirectionApplies(term)
            ? (DirectionColor(term)!, DirectionSoft(term)!)
            : (Info(term).Color, Info(term).Soft);
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

    /// <summary>The soft ground behind a glyph in this side's hue.</summary>
    public string Soft => Tone == "income" ? "var(--finance-income-soft)" : "var(--finance-expense-soft)";
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
