namespace Odyssey.Dtos.Finance;

/// <summary>
/// The one definition of a contract's smart-tag scope (issue #308): the date range the smart-tag
/// transaction match applies instead of the contract's term. Both bounds are calendar days, stored as
/// UTC midnight; <c>null</c> on either side means open-ended on that side.
///
/// <para>
/// Lives in the zero-reference DTO project so the contract service, the demo-data generator, the
/// <c>[Range]</c> attributes on <see cref="UpdateContract"/> and the WASM client all name one symbol.
/// <see cref="NormalizeToUtc"/> is a deliberate copy of <c>DateTimeNormalization.NormalizeToUtc</c>
/// (Core): this project cannot reference Core, and the rule is three lines a test pins. Do not
/// "tidy" this class into Core — the client and the generator would lose it.
/// </para>
/// </summary>
public static class SmartTagScopeRules
{
    /// <summary>The <c>[Range]</c> floor, as an invariant ISO string (attributes need a constant).</summary>
    public const string MinDateText = "1900-01-01";

    /// <summary>
    /// The <c>[Range]</c> ceiling: the last instant of 9999-12-31, so a time of day on the last day
    /// passes the attribute and truncates to that day rather than being refused.
    /// </summary>
    public const string MaxDateText = "9999-12-31T23:59:59.9999999";

    /// <summary>The bounds message, shared by the attribute and the post-normalisation re-check.</summary>
    public const string OutOfRangeMessage = "Enter a date between 1 Jan 1900 and 31 Dec 9999.";

    /// <summary>The order message, keyed on the scope end.</summary>
    public const string InvertedMessage = "The scope end must not be before its start.";

    /// <summary>The first calendar day a scope bound may name (matches <see cref="TaxSettlementRange.MinDate"/>).</summary>
    public static readonly DateTime MinDate = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The last calendar day a scope bound may name — the last day <see cref="PeriodBounds.ExclusiveEnd"/> saturates on.</summary>
    public static readonly DateTime MaxDate = new(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Rule 2 — a client-supplied scope date to its calendar day. Converts to UTC by
    /// <see cref="DateTime.Kind"/> first (<c>Utc</c> unchanged, <c>Local</c> converted,
    /// <c>Unspecified</c> relabelled without conversion), then truncates. A value sent with an offset
    /// binds as <c>Local</c> and is therefore converted before truncation:
    /// <c>2027-06-30T00:30:00+02:00</c> is the day 2027-06-29.
    /// </summary>
    public static DateTime? NormalizeDay(DateTime? value) =>
        value is { } v ? DateTime.SpecifyKind(NormalizeToUtc(v).Date, DateTimeKind.Utc) : null;

    /// <summary>
    /// Rule 4 — the scope a contract is created, seeded and backfilled with. A one-off
    /// (<paramref name="completionDate"/> set) is open on both sides. Otherwise each bound is the
    /// calendar day of the term date AS STORED — <c>.Date</c>, relabelled UTC, no time-zone
    /// conversion — which is exactly what the migration's <c>DATE()</c> backfill produces, so a
    /// created, a seeded and a backfilled contract with the same stored term get the same scope. An
    /// inverted legacy term keeps its start and opens its end.
    /// </summary>
    public static (DateTime? Start, DateTime? End) FromTerm(
        DateTime? startDate, DateTime? endDate, DateTime? completionDate)
    {
        if (completionDate is not null)
            return (null, null);

        DateTime? start = startDate is { } s ? DateTime.SpecifyKind(s.Date, DateTimeKind.Utc) : null;
        DateTime? end = endDate is { } e ? DateTime.SpecifyKind(e.Date, DateTimeKind.Utc) : null;
        if (start is { } from && end is { } to && to < from)
            end = null;
        return (start, end);
    }

    /// <summary>True when a normalised bound lies outside <see cref="MinDate"/>…<see cref="MaxDate"/>.</summary>
    public static bool IsOutOfRange(DateTime? day) => day is { } d && (d < MinDate || d > MaxDate);

    /// <summary>Rule 3 — start after end. Same day is a valid one-day window.</summary>
    public static bool IsInverted(DateTime? start, DateTime? end) =>
        start is { } s && end is { } e && e.Date < s.Date;

    private static DateTime NormalizeToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
