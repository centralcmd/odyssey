using Odyssey.Dtos;

namespace Odyssey.Core.Journal;

/// <summary>
/// The calendar's time-zone rules (issue #519 §8.1): resolving a requested zone, and deriving an instant from a
/// wall-clock and back. One place, so the write path, the generator, the window query, the export and the import all
/// read the same tzdata through <see cref="TimeZoneInfo"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A wall-clock in a DST gap moves forward by the gap length</b> — it is read at the offset in force just before the
/// gap — and <b>an ambiguous wall-clock takes the earlier instant</b>, its first occurrence. Both are reported through
/// <c>adjusted</c> only for the gap, since an ambiguous time keeps its wall-clock.
/// </para>
/// <para>
/// Request zones pass <see cref="IanaTimeZones.IsValid"/> (the shared shape gate, then resolution), so Windows ids,
/// <c>posix/…</c>, <c>../x</c> and <c>localtime</c> are refused. Only a file's <c>TZID</c> may also be a Windows name,
/// mapped to its IANA id (<see cref="ResolveFileZone"/>).
/// </para>
/// </remarks>
public static class CalendarTimeZones
{
    /// <summary>The zone a <c>Z</c> time is stored in.</summary>
    public const string UtcId = "UTC";

    /// <summary>Resolves a stored or validated zone id; null for null. An id that no longer resolves reads as UTC.</summary>
    public static TimeZoneInfo? Find(string? id)
    {
        if (id is null)
        {
            return null;
        }

        return TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : TimeZoneInfo.Utc;
    }

    /// <summary>
    /// A request's zone, refused with a <c>400</c> keyed <paramref name="field"/> when it is not an IANA id this host
    /// resolves. The DTO's <c>[IanaTimeZone]</c> already refuses it on the HTTP path; this is the non-HTTP caller's gate.
    /// </summary>
    public static TimeZoneInfo? ResolveRequest(string? id, string field = "timeZoneId")
    {
        if (id is null)
        {
            return null;
        }

        if (!IanaTimeZones.IsValid(id) || !TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
        {
            throw new DomainValidationException("The time zone must be an IANA time zone, e.g. Europe/Oslo.", null, field);
        }

        return zone;
    }

    /// <summary>
    /// A file's <c>TZID</c> (issue #519 §8.4): an IANA id through the same gate as a request, else a Windows name mapped to
    /// its IANA id. Null for anything else — the caller skips the component as an unknown zone.
    /// </summary>
    public static TimeZoneInfo? ResolveFileZone(string? tzid)
    {
        if (string.IsNullOrWhiteSpace(tzid))
        {
            return null;
        }

        var id = tzid.Trim().Trim('"');
        if (id.StartsWith('/'))
        {
            id = id[1..]; // RFC 5545's globally-unique prefix
        }

        if (IanaTimeZones.IsValid(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
        {
            return zone;
        }

        if (id.Length <= IanaTimeZones.MaxLength
            && TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana)
            && IanaTimeZones.IsValid(iana)
            && TimeZoneInfo.TryFindSystemTimeZoneById(iana, out var mapped))
        {
            return mapped;
        }

        return null;
    }

    /// <summary>The instant of a wall-clock in <paramref name="zone"/>, by the gap and overlap rules above.</summary>
    public static DateTime ToUtc(DateTime wall, TimeZoneInfo zone) => ToUtc(wall, zone, out _);

    /// <inheritdoc cref="ToUtc(DateTime, TimeZoneInfo)"/>
    /// <param name="adjusted">The wall-clock fell in a gap and was moved forward.</param>
    public static DateTime ToUtc(DateTime wall, TimeZoneInfo zone, out bool adjusted)
    {
        adjusted = false;
        var local = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        TimeSpan offset;
        if (zone.IsInvalidTime(local))
        {
            adjusted = true;
            offset = OffsetBeforeGap(local, zone);
        }
        else if (zone.IsAmbiguousTime(local))
        {
            // The earlier instant is the one at the larger offset (wall - offset).
            offset = zone.GetAmbiguousTimeOffsets(local).Max();
        }
        else
        {
            offset = zone.GetUtcOffset(local);
        }

        return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
    }

    /// <summary>The wall-clock of an instant in <paramref name="zone"/>, with <c>Kind = Unspecified</c>.</summary>
    public static DateTime ToWall(DateTime utc, TimeZoneInfo zone) =>
        DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone), DateTimeKind.Unspecified);

    /// <summary>The wall-clock a wall-clock in a gap is shown as once stored: moved forward by the gap.</summary>
    public static DateTime Normalize(DateTime wall, TimeZoneInfo zone) => ToWall(ToUtc(wall, zone), zone);

    /// <summary>A stored value's wall-clock: itself when floating, else the instant converted to its zone.</summary>
    public static DateTime WallOf(DateTime stored, string? timeZoneId) =>
        Find(timeZoneId) is { } zone ? ToWall(stored, zone) : DateTime.SpecifyKind(stored, DateTimeKind.Unspecified);

    /// <summary>
    /// A stored value as a read returns it (issue #519 §5): the wall-clock in its zone with <c>Kind = Unspecified</c>, or
    /// — for a floating row — the stored value labelled UTC exactly as before #519, so an older client keeps working.
    /// </summary>
    public static DateTime ReadValue(DateTime stored, string? timeZoneId) =>
        Find(timeZoneId) is { } zone ? ToWall(stored, zone) : DateTime.SpecifyKind(stored, DateTimeKind.Utc);

    /// <summary>A stored value's instant: null when floating.</summary>
    public static DateTime? InstantOf(DateTime stored, string? timeZoneId) =>
        timeZoneId is null ? null : DateTime.SpecifyKind(stored, DateTimeKind.Utc);

    // The offset in force just before a gap: walk back a minute at a time until the wall-clock exists (at most a day).
    private static TimeSpan OffsetBeforeGap(DateTime wall, TimeZoneInfo zone)
    {
        var probe = wall;
        for (var i = 0; i < 24 * 60 && zone.IsInvalidTime(probe); i++)
        {
            probe = probe.AddMinutes(-1);
        }

        return zone.GetUtcOffset(probe);
    }
}
