using System.Globalization;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Components;

/// <summary>A contract's smart-tag scope as the client handles it: two calendar days, null = open.</summary>
public readonly record struct OdsScopeRange(DateTime? Start, DateTime? End)
{
    public static OdsScopeRange Open => new(null, null);

    /// <summary>Both sides reduced to their calendar day, so a UTC and an unspecified value compare equal.</summary>
    public OdsScopeRange Days => new(Start?.Date, End?.Date);

    public bool IsOpen => Start is null && End is null;

    public bool SameAs(OdsScopeRange other) => Days == other.Days;
}

/// <summary>The term the scope is compared with — never validated against (issue #308 §8 rule 5).</summary>
public readonly record struct OdsScopeTerm(DateTime? StartDate, DateTime? EndDate, DateTime? CompletionDate)
{
    public bool IsOneOff => CompletionDate is not null;

    public static OdsScopeTerm Of(ExistingContract contract) =>
        new(contract.StartDate, contract.EndDate, contract.CompletionDate);
}

/// <summary>
/// The display half of the design system's <c>SMART_TAG_SCOPE_RULES</c> (Odyssey Design System ·
/// components/SmartTagScopeField). The DATA rules — the copy from the term, the bounds, the order —
/// are the server's own <see cref="SmartTagScopeRules"/>, read here rather than restated: a client
/// copy of a server rule is the defect CLAUDE.md forbids. What lives here is presentation only: the
/// day and range formats, the scope-vs-term sentence, the scope line's note and the wire form.
/// </summary>
public static class OdsSmartTagScope
{
    /// <summary>The scope a contract with this term would be created with (§8 rule 4).</summary>
    public static OdsScopeRange FromTerm(OdsScopeTerm term)
    {
        var (start, end) = SmartTagScopeRules.FromTerm(term.StartDate, term.EndDate, term.CompletionDate);
        return new(start?.Date, end?.Date);
    }

    /// <summary>The stored scope of a loaded contract.</summary>
    public static OdsScopeRange Of(ExistingContract contract) =>
        new OdsScopeRange(contract.SmartTagScopeStart, contract.SmartTagScopeEnd).Days;

    /// <summary>
    /// A calendar day as the wire wants it: UTC midnight, serialised with a trailing <c>Z</c>. The
    /// client never sends an offset, so the server's offset conversion can never move the day.
    /// </summary>
    public static DateTime? ToWire(DateTime? day) =>
        day is { } d ? DateTime.SpecifyKind(d.Date, DateTimeKind.Utc) : null;

    /// <summary>Rules 1 + 3, in the server's order and words: bounds per side, then start ≤ end keyed on the end.</summary>
    public static (string? Start, string? End) Validate(OdsScopeRange scope)
    {
        string? start = SmartTagScopeRules.IsOutOfRange(scope.Start) ? SmartTagScopeRules.OutOfRangeMessage : null;
        string? end = SmartTagScopeRules.IsOutOfRange(scope.End) ? SmartTagScopeRules.OutOfRangeMessage : null;
        if (start is null && end is null && SmartTagScopeRules.IsInverted(scope.Start, scope.End))
            end = SmartTagScopeRules.InvertedMessage;
        return (start, end);
    }

    /// <summary>"1 Sep 2025" — invariant, so the sentence reads the same in every browser locale.</summary>
    public static string FormatDay(DateTime day) => day.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>"1 Sep 2025 – 31 Aug 2026" · "from 1 Sep 2025, no end" · "until 31 Aug 2026" · "any date".</summary>
    public static string FormatRange(OdsScopeRange scope) => (scope.Start, scope.End) switch
    {
        ({ } s, { } e) => $"{FormatDay(s)} – {FormatDay(e)}",
        ({ } s, null) => $"from {FormatDay(s)}, no end",
        (null, { } e) => $"until {FormatDay(e)}",
        _ => "any date",
    };

    /// <summary>The scope against the term, as one sentence. Never a validation.</summary>
    public static string Describe(OdsScopeRange scope, OdsScopeTerm term)
    {
        var (start, end) = scope.Days;
        if (term.IsOneOff)
        {
            return start is not null || end is not null
                ? "A one-off contract has no term, so this range is the only date rule."
                : "A one-off contract has no term, so transactions of any date match.";
        }

        var ts = term.StartDate?.Date;
        var te = term.EndDate?.Date;

        var a = start switch
        {
            null => ts is not null ? "No start limit — transactions before the term match too" : "No start limit",
            { } s when ts is null => $"Starts {FormatDay(s)}",
            { } s when s == ts => "Starts with the term",
            { } s when s < ts => $"Starts {Gap(s, ts!.Value)} before the term",
            { } s => $"Starts {Gap(ts!.Value, s)} after the term starts",
        };

        var b = end switch
        {
            null => te is { } termEnd ? $"stays open after the term ends ({FormatDay(termEnd)})" : "stays open, like the term",
            { } e when te is null => $"ends {FormatDay(e)}, while the term has no end",
            { } e when e == te => "ends with it",
            { } e when e > te => $"runs {Gap(te!.Value, e)} past the term’s end ({FormatDay(te.Value)})",
            { } e => $"ends {Gap(e, te!.Value)} before the term does",
        };

        return $"{a}, and {b}.";
    }

    /// <summary>
    /// The muted note on the Smart tags section's <c>Dated</c> item, placing the stored scope against
    /// the term so a widened scope is visible without opening the editor.
    /// </summary>
    public static string Relation(OdsScopeRange scope, OdsScopeTerm term)
    {
        var (start, end) = scope.Days;
        if (term.IsOneOff)
            return start is not null || end is not null ? "one-off contract" : "one-off contract, no date rule";
        if (scope.SameAs(FromTerm(term)))
            return "same as the term";

        var ts = term.StartDate?.Date;
        var te = term.EndDate?.Date;
        var beyond = new List<string>(2);
        if (ts is { } termStart && (start is null || start < termStart))
            beyond.Add("before");
        if (te is { } termEnd && (end is null || end > termEnd))
            beyond.Add("after");
        return beyond.Count > 0 ? $"includes dates {string.Join(" and ", beyond)} the term" : "narrower than the term";
    }

    // The design system's stsGap (JS Math.round, hence AwayFromZero): days under ~2 months, months under 2 years, then years.
    private static string Gap(DateTime from, DateTime to)
    {
        var days = (int)Math.Round(Math.Abs((to - from).TotalDays), MidpointRounding.AwayFromZero);
        if (days < 62)
            return $"{days} day{(days == 1 ? "" : "s")}";
        var months = (int)Math.Round(days / 30.44, MidpointRounding.AwayFromZero);
        if (months < 24)
            return $"{months} months";
        return $"{(int)Math.Round(months / 12.0, MidpointRounding.AwayFromZero)} years";
    }
}
