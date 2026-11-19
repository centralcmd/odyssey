namespace Odyssey.TestShared;

/// <summary>
/// The ONE table of wall-clock + zone → instant cases the calendar's two converters must agree on (PR #528 architect
/// review): the server's <c>CalendarTimeZones</c> (Odyssey.Core), which stores the instant, and the client's
/// <c>CalendarZoneClock</c>, which words the DST hints and chooses the wall-clock a drag or resize sends. Linked into
/// Odyssey.Core.Tests and Odyssey.Client.Tests, so the two are tested against the same rows and cannot drift.
/// </summary>
/// <remarks>
/// The rules: a wall-clock in a spring gap is read at the offset in force just before the gap, so it lands past the gap by
/// its length; an ambiguous (autumn) wall-clock takes the earlier instant. The rows cover both hemispheres and half- and
/// quarter-hour offsets. Every instant here was worked out by hand from the zone's published 2026 transitions.
/// </remarks>
public static class CalendarZoneGoldenCases
{
    /// <summary>What the server does with the wall-clock.</summary>
    public enum Kind
    {
        Ordinary,
        Gap,
        Ambiguous,
    }

    /// <param name="Zone">The IANA zone.</param>
    /// <param name="Wall">The wall-clock entered, <c>Kind = Unspecified</c>.</param>
    /// <param name="Utc">The instant stored.</param>
    /// <param name="Kind">Ordinary, in a gap, or ambiguous.</param>
    /// <param name="Saved">The wall-clock the instant reads back as: moved forward for a gap, else <paramref name="Wall"/>.</param>
    public sealed record Case(string Zone, DateTime Wall, DateTime Utc, Kind Kind, DateTime Saved)
    {
        public override string ToString() => $"{Zone} {Wall:yyyy-MM-dd HH:mm} ({Kind})";
    }

    private static DateTime L(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, DateTimeKind.Unspecified);

    private static DateTime U(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    private static Case Plain(string zone, DateTime wall, DateTime utc) => new(zone, wall, utc, Kind.Ordinary, wall);

    private static Case Twice(string zone, DateTime wall, DateTime utc) => new(zone, wall, utc, Kind.Ambiguous, wall);

    private static Case Gap(string zone, DateTime wall, DateTime utc, DateTime saved) => new(zone, wall, utc, Kind.Gap, saved);

    public static IReadOnlyList<Case> All { get; } =
    [
        // Europe/Oslo: CET +1 / CEST +2; spring 29 Mar 02:00→03:00, autumn 25 Oct 03:00→02:00.
        Plain("Europe/Oslo", L(2026, 3, 28, 9, 0), U(2026, 3, 28, 8, 0)),
        Plain("Europe/Oslo", L(2026, 7, 1, 9, 0), U(2026, 7, 1, 7, 0)),
        Gap("Europe/Oslo", L(2026, 3, 29, 2, 30), U(2026, 3, 29, 1, 30), L(2026, 3, 29, 3, 30)),
        Twice("Europe/Oslo", L(2026, 10, 25, 2, 30), U(2026, 10, 25, 0, 30)),

        // America/New_York: EST −5 / EDT −4; spring 8 Mar 02:00→03:00, autumn 1 Nov 02:00→01:00.
        Plain("America/New_York", L(2026, 1, 15, 9, 0), U(2026, 1, 15, 14, 0)),
        Gap("America/New_York", L(2026, 3, 8, 2, 30), U(2026, 3, 8, 7, 30), L(2026, 3, 8, 3, 30)),
        Twice("America/New_York", L(2026, 11, 1, 1, 30), U(2026, 11, 1, 5, 30)),

        // Australia/Sydney (southern hemisphere): AEST +10 / AEDT +11; autumn 5 Apr 03:00→02:00, spring 4 Oct 02:00→03:00.
        Plain("Australia/Sydney", L(2026, 6, 15, 9, 0), U(2026, 6, 14, 23, 0)),
        Twice("Australia/Sydney", L(2026, 4, 5, 2, 30), U(2026, 4, 4, 15, 30)),
        Gap("Australia/Sydney", L(2026, 10, 4, 2, 30), U(2026, 10, 3, 16, 30), L(2026, 10, 4, 3, 30)),

        // Australia/Adelaide (southern, half-hour): ACST +9:30 / ACDT +10:30; same transition dates as Sydney.
        Twice("Australia/Adelaide", L(2026, 4, 5, 2, 30), U(2026, 4, 4, 16, 0)),
        Gap("Australia/Adelaide", L(2026, 10, 4, 2, 30), U(2026, 10, 3, 17, 0), L(2026, 10, 4, 3, 30)),

        // Pacific/Auckland (southern): NZST +12 / NZDT +13; spring 27 Sep 02:00→03:00.
        Gap("Pacific/Auckland", L(2026, 9, 27, 2, 30), U(2026, 9, 26, 14, 30), L(2026, 9, 27, 3, 30)),
        Plain("Pacific/Auckland", L(2026, 1, 15, 9, 0), U(2026, 1, 14, 20, 0)),

        // America/St_Johns (half-hour, negative): NST −3:30 / NDT −2:30; spring 8 Mar 02:00→03:00.
        Gap("America/St_Johns", L(2026, 3, 8, 2, 30), U(2026, 3, 8, 6, 0), L(2026, 3, 8, 3, 30)),
        Plain("America/St_Johns", L(2026, 7, 1, 9, 0), U(2026, 7, 1, 11, 30)),

        // Fixed half- and quarter-hour offsets, and UTC itself.
        Plain("Asia/Kolkata", L(2026, 6, 15, 9, 0), U(2026, 6, 15, 3, 30)),
        Plain("Asia/Kathmandu", L(2026, 1, 1, 12, 0), U(2026, 1, 1, 6, 15)),
        Plain("UTC", L(2026, 3, 29, 2, 30), U(2026, 3, 29, 2, 30)),
    ];
}
