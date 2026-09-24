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
/// <remarks>
/// The unit hue is a GLYPH hue only. A priced term's figure and chart line take its direction's
/// finance hue (<see cref="TermVisuals.ValueColor"/>); a Text or DateTime term's value takes the
/// per-theme <c>--trm-fact-ink</c> (issue #192) — so the unit hue is never text and needs no
/// per-theme ink of its own.
/// </remarks>
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

    // The two NON-NUMERIC kinds (issue #192). Both take the neutral orange of the Amount hue: they have
    // no direction, so there is no finance hue to give them, and the glyph tells them apart.
    private static readonly TermInfo TextInfo =
        new("Text", "short_text", "oklch(0.77 0.14 55)", "oklch(0.77 0.14 55 / 0.15)");

    private static readonly TermInfo DateTimeInfo =
        new("Date & time", "event", "oklch(0.77 0.14 55)", "oklch(0.77 0.14 55 / 0.15)");

    /// <summary>An unknown member — a newer server than this client. Never guessed at.</summary>
    private static readonly TermInfo UnknownInfo =
        new("Unknown", "help_outline", "var(--mud-palette-text-secondary)", "var(--mud-palette-action-default-hover)");

    /// <summary>
    /// The glyph and hue for a unit — exhaustive over the four kinds. An undefined value reads as
    /// unknown, never as an amount: a Text term rendered with the money glyph is the misrepresentation
    /// issue #192 exists to remove.
    /// </summary>
    public static TermInfo UnitInfo(TermValueUnit unit) => unit switch
    {
        TermValueUnit.Percentage => PercentageInfo,
        TermValueUnit.Amount => AmountInfo,
        TermValueUnit.Text => TextInfo,
        TermValueUnit.DateTime => DateTimeInfo,
        _ => UnknownInfo,
    };

    /// <summary>The glyph and hue a term renders with — its unit's.</summary>
    public static TermInfo Info(ExistingTerm term) => UnitInfo(term.ValueUnit);

    /// <summary>
    /// The four kinds in the dialog's picker order. The ordinals happen to match; the list is the
    /// registry so a later member is placed deliberately rather than by its number.
    /// </summary>
    public static readonly IReadOnlyList<TermValueUnit> AllUnits =
        [TermValueUnit.Percentage, TermValueUnit.Amount, TermValueUnit.Text, TermValueUnit.DateTime];

    /// <summary>
    /// Whether a unit carries a number — the only kinds with a <c>value</c>, a chart point, a
    /// direction, a currency or a cadence (issue #192).
    /// </summary>
    public static bool IsNumeric(TermValueUnit unit) =>
        unit is TermValueUnit.Percentage or TermValueUnit.Amount;

    /// <summary>Whether a term carries a number. See <see cref="IsNumeric(TermValueUnit)"/>.</summary>
    public static bool IsNumeric(ExistingTerm term) => IsNumeric(term.ValueUnit);

    /// <summary>
    /// The CSS modifier a non-numeric value takes on a read surface — <c>trm-v-text</c>,
    /// <c>trm-v-datetime</c> or <c>trm-v-unknown</c> — or <c>null</c> for a number, which keeps the
    /// mono figure and its direction hue.
    /// </summary>
    public static string? ValueKindClass(ExistingTerm term) => term.ValueUnit switch
    {
        TermValueUnit.Percentage or TermValueUnit.Amount => null,
        TermValueUnit.Text => "trm-v-text",
        TermValueUnit.DateTime => "trm-v-datetime",
        _ => "trm-v-unknown",
    };

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

    /// <summary>What a value that cannot be shown reads as — never 0, never money, never a percentage.</summary>
    public const string NoValue = "\u2014";

    /// <summary>
    /// A term's value as a display string — exhaustive over the four kinds (issue #192). A number
    /// carries the stored sign as entered: "6.49%", "−0.5%" for a genuinely negative percentage, or a
    /// money amount (formatted via <paramref name="money"/>). A Text term reads its text as plain
    /// text; a DateTime term its instant in the viewer's local time with the offset named. A null
    /// value on a numeric kind and an unknown kind both read <see cref="NoValue"/>.
    /// </summary>
    public static string FormatValue(ExistingTerm term, Func<decimal, string?, string> money) =>
        FormatValue(term.ValueUnit, term.Value, term.CurrencyCode, term.TextValue, term.DateTimeValue, money);

    /// <inheritdoc cref="FormatValue(ExistingTerm, Func{decimal, string?, string})"/>
    public static string FormatValue(
        TermValueUnit unit, decimal? value, string? currencyCode, string? textValue, DateTime? dateTimeValue,
        Func<decimal, string?, string> money) => unit switch
    {
        TermValueUnit.Percentage => value is { } pct ? (pct < 0 ? "−" : "") + PctStr(Math.Abs(pct)) : NoValue,
        TermValueUnit.Amount => value is { } amount ? money(amount, currencyCode) : NoValue,
        TermValueUnit.Text => string.IsNullOrWhiteSpace(textValue) ? NoValue : textValue,
        TermValueUnit.DateTime => FormatDateTime(dateTimeValue),
        _ => NoValue,
    };

    // ---- Date & time (issue #192) -------------------------------------------------------------

    /// <summary>
    /// A DateTime term's instant in the VIEWER's local time, with the offset in force at that instant
    /// named — "31 Mar 2027, 12:00 UTC+02:00". The stored value is UTC; the offset is shown so two
    /// readers in different zones can tell they are looking at one instant.
    /// </summary>
    public static string FormatDateTime(DateTime? utc, TimeZoneInfo? zone = null)
    {
        if (utc is not { } instant)
            return NoValue;

        var local = ToLocal(instant, zone ?? TimeZoneInfo.Local);
        return $"{local.ToString("dd MMM yyyy, HH:mm", CultureInfo.InvariantCulture)} {OffsetLabel(local.Offset)}";
    }

    /// <summary>The event catalogue's invariant form: "2027-03-31 10:00 UTC".</summary>
    public static string UtcStamp(DateTime? utc) =>
        utc is { } instant
            ? AsUtc(instant).ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)
            : NoValue;

    /// <summary>"UTC+02:00", "UTC−05:00", "UTC+00:00" — the offset as the dialog and the value state it.</summary>
    public static string OffsetLabel(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "−" : "+";
        var abs = offset.Duration();
        return $"UTC{sign}{abs.Hours:00}:{abs.Minutes:00}";
    }

    /// <summary>A UTC instant as the viewer's local wall-clock time, carrying the offset in force then.</summary>
    public static DateTimeOffset ToLocal(DateTime utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(new DateTimeOffset(AsUtc(utc)), zone);

    /// <summary>
    /// A local date and time-of-day in <paramref name="zone"/> as the UTC instant it names, or
    /// <c>null</c> when either half is missing. A wall-clock time skipped by a daylight-saving jump is
    /// read with the offset in force before the jump.
    /// </summary>
    public static DateTime? LocalToUtc(DateTime? date, TimeSpan? time, TimeZoneInfo zone)
    {
        if (date is not { } d || time is not { } t)
            return null;

        var wall = DateTime.SpecifyKind(d.Date + t, DateTimeKind.Unspecified);
        var offset = zone.IsInvalidTime(wall)
            ? zone.GetUtcOffset(wall.AddHours(-1))
            : zone.GetUtcOffset(wall);
        return DateTime.SpecifyKind(wall - offset, DateTimeKind.Utc);
    }

    /// <summary>
    /// "in 2 months", "in 12 days", "today", "3 days ago" — the distance to a DateTime term's instant
    /// for the tile foot. Display only: nothing is scheduled off it.
    /// </summary>
    public static string Distance(DateTime utc, DateTime nowUtc)
    {
        var days = (int)Math.Round((AsUtc(utc) - AsUtc(nowUtc)).TotalDays);
        if (days == 0)
            return "today";

        var abs = Math.Abs(days);
        var span = abs < 60 ? $"{abs} {(abs == 1 ? "day" : "days")}" : $"{(int)Math.Round(abs / 30.4)} months";
        return days > 0 ? $"in {span}" : $"{span} ago";
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    // ---- Direction (issue #159) -------------------------------------------------------------

    /// <summary>
    /// Whether this term brings money IN. A predicate of its own rather than a test on
    /// <see cref="DirectionColor"/>: a caller that wants the fact should ask for the fact, or a later
    /// change to what the colour helper returns silently changes what the caller counts. A Text or
    /// DateTime term has no direction and is never incoming.
    /// </summary>
    public static bool IsIncoming(ExistingTerm term) =>
        IsNumeric(term) && term.Direction == TermDirection.Incoming;

    /// <summary>
    /// Whether the term states a direction at all — only a priced one does. A Text or DateTime term
    /// records a fact, not a movement of money, so no direction word is shown for it (issue #192).
    /// </summary>
    public static bool HasDirection(ExistingTerm term) => IsNumeric(term);

    /// <summary>
    /// The finance hue of the term's direction — coral out, mint in. Every priced term states one: a
    /// term is owned by a contract, and since issue #190 by nothing else. One helper, so the tiles,
    /// the table rows and the chart cannot disagree about which figure is which colour.
    /// </summary>
    public static string DirectionColor(ExistingTerm term) => TermDirectionVisuals.Info(term.Direction).Color;

    /// <summary>The soft ground behind a term's glyph, in its direction's hue.</summary>
    public static string DirectionSoft(ExistingTerm term) => TermDirectionVisuals.Info(term.Direction).Soft;

    /// <summary>
    /// The figure's colour: its direction's hue on a priced term. A non-numeric value takes the
    /// per-theme fact ink instead — no direction, so no finance hue.
    /// </summary>
    public static string ValueColor(ExistingTerm term) =>
        IsNumeric(term) ? DirectionColor(term) : "var(--trm-fact-ink)";

    /// <summary>
    /// The glyph's colour and ground — the direction's on a priced term, the kind's own on a Text or
    /// DateTime term.
    /// </summary>
    public static (string Color, string Soft) IconColors(ExistingTerm term)
    {
        if (IsNumeric(term))
            return (DirectionColor(term), DirectionSoft(term));

        var info = Info(term);
        return (info.Color, info.Soft);
    }
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
/// <b>Read with a DEFAULT, never a truthiness test.</b> A row written before the field existed is
/// <see cref="TermDirection.Outgoing"/>.
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
