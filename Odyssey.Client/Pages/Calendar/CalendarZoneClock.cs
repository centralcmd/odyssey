using System.Globalization;
using Odyssey.Client.Pages.Journal;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Calendar;

/// <summary>What the server will do with a wall-clock in a zone (issue #519 §8.1), mirrored only to word a hint.</summary>
public enum ZoneAdjustment
{
    None,

    /// <summary>The wall-clock does not exist that day (spring forward): it moves forward by the gap.</summary>
    Gap,

    /// <summary>The wall-clock happens twice that day (fall back): the earlier instant is taken.</summary>
    Ambiguous,
}

/// <summary>
/// The calendar's one zone helper (issue #520 §4: "no second converter"): event-zone wall-clock ↔ instant ↔ viewer
/// wall-clock, the picker's offsets and search keys, and the short date and time formats the zone copy uses. It reads
/// zones through <see cref="LocationVisuals.FindZone"/> and offsets through <see cref="LocationVisuals.UtcOffset"/>,
/// the helpers the location and weather-point pickers already use, so there is one zone lookup in the client.
/// </summary>
/// <remarks>
/// The gap and overlap rules are the server's (<c>CalendarTimeZones</c>): a gap is read at the offset in force just
/// before it, so it lands past the gap by exactly its length, and an ambiguous time takes the earlier instant. The client
/// uses them for more than wording: the DST hints, the two-zone line, and — in <c>CalendarPage.ResizeUpdate</c> — the
/// wall-clock a time-grid drag or resize SENDS, converted from the drop position in the viewer's zone into the event's own.
/// So the two must agree; <c>CalendarZoneGoldenCases</c> (TestShared) is the one table both are tested against. The
/// server's returned value is still what is shown after a save (#520 SX1).
/// </remarks>
public static class CalendarZoneClock
{
    /// <summary>The combobox value of the "No time zone (floating)" row.</summary>
    public const string FloatingValue = "\u001ffloating";

    public const string FloatingLabel = "No time zone (floating)";

    /// <summary>The instant of <paramref name="wall"/> in <paramref name="zone"/>, by the server's gap and overlap rules.</summary>
    public static DateTime ToUtc(DateTime wall, TimeZoneInfo zone) => ToUtc(wall, zone, out _);

    /// <inheritdoc cref="ToUtc(DateTime, TimeZoneInfo)"/>
    public static DateTime ToUtc(DateTime wall, TimeZoneInfo zone, out ZoneAdjustment adjustment)
    {
        var local = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        TimeSpan offset;
        if (zone.IsInvalidTime(local))
        {
            adjustment = ZoneAdjustment.Gap;
            var probe = local;
            for (var i = 0; i < 24 * 60 && zone.IsInvalidTime(probe); i++)
            {
                probe = probe.AddMinutes(-1);
            }

            offset = zone.GetUtcOffset(probe);
        }
        else if (zone.IsAmbiguousTime(local))
        {
            adjustment = ZoneAdjustment.Ambiguous;
            offset = zone.GetAmbiguousTimeOffsets(local).Max();
        }
        else
        {
            adjustment = ZoneAdjustment.None;
            offset = zone.GetUtcOffset(local);
        }

        return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
    }

    /// <summary>The wall-clock of an instant in <paramref name="zone"/>, <c>Kind = Unspecified</c>.</summary>
    public static DateTime ToWall(DateTime utc, TimeZoneInfo zone) =>
        DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone), DateTimeKind.Unspecified);

    /// <summary>A wall-clock in one zone as the wall-clock in another (the same instant).</summary>
    public static DateTime Convert(DateTime wall, TimeZoneInfo from, TimeZoneInfo to) => ToWall(ToUtc(wall, from), to);

    /// <summary>The wall-clock the server will store for <paramref name="wall"/>: moved forward when it falls in a gap.</summary>
    public static DateTime Saved(DateTime wall, TimeZoneInfo zone) => ToWall(ToUtc(wall, zone), zone);

    /// <summary>
    /// When an event is drawn for the viewer (issue #520 §2): a zoned event at its instant in the viewer's zone, a floating
    /// or all-day event at its stored clock time, unconverted.
    /// </summary>
    public static (DateTime Start, DateTime End) Displayed(ExistingCalendarEvent e, TimeZoneInfo view)
    {
        if (e.IsAllDay || e.TimeZoneId is null || e.StartUtc is not { } startUtc || e.EndUtc is not { } endUtc)
        {
            return (e.StartDateTime, e.EndDateTime);
        }

        return (ToWall(startUtc, view), ToWall(endUtc, view));
    }

    /// <summary>The event is drawn converted: it has a zone and a time.</summary>
    public static bool IsZoned(ExistingCalendarEvent e) => !e.IsAllDay && e.TimeZoneId is not null;

    /// <summary>"New York" for <c>America/New_York</c> — the part after the last '/', underscores as spaces.</summary>
    public static string CityAlias(string id) => id[(id.LastIndexOf('/') + 1)..].Replace('_', ' ');

    /// <summary>What the zone picker matches a query against: the id and its city.</summary>
    public static string SearchKey(string id) => $"{id} {CityAlias(id)}";

    /// <summary>The zone's UTC offset ON <paramref name="date"/> (taken at noon UTC), never today's (#520 AX3).</summary>
    public static string? OffsetOn(string id, DateTime date) =>
        LocationVisuals.UtcOffset(id, DateTime.SpecifyKind(date.Date.AddHours(12), DateTimeKind.Utc));

    /// <summary>"09:00".</summary>
    public static string Hm(DateTime value) => value.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"Tue 4 Jun".</summary>
    public static string DayLabel(DateTime value) => value.ToString("ddd d MMM", CultureInfo.InvariantCulture);

    /// <summary>"30 Mar".</summary>
    public static string ShortDate(DateTime value) => value.ToString("d MMM", CultureInfo.InvariantCulture);
}
