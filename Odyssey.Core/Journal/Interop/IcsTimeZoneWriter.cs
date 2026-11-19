using System.Globalization;
using System.Text;

namespace Odyssey.Core.Journal.Interop;

/// <summary>
/// Builds a <c>VTIMEZONE</c> for the calendar export (issue #519 §8.3) from the same <see cref="TimeZoneInfo"/> tzdata
/// the generator uses, covering only the planned years. Each distinct <c>(from offset, to offset, kind)</c> is one
/// observance whose first transition is its <c>DTSTART</c> and whose later ones are <c>RDATE</c>s — no observance
/// <c>RRULE</c> is ever written, so what a reader computes is exactly what Odyssey computed.
/// </summary>
internal static class IcsTimeZoneWriter
{
    /// <summary>The cap on one <c>VTIMEZONE</c> (S8); a zone over it is written as UTC instants instead.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>The <c>VTIMEZONE</c> text, CRLF-terminated, or null when it would exceed <see cref="MaxBytes"/>.</summary>
    public static string? Build(TimeZoneInfo zone, int fromYear, int toYear)
    {
        fromYear = Math.Clamp(fromYear, 1900, 2200);
        toYear = Math.Clamp(Math.Max(toYear, fromYear), 1900, 2200);
        var transitions = Transitions(zone, fromYear, toYear);

        var builder = new StringBuilder();
        builder.Append("BEGIN:VTIMEZONE\r\n");
        builder.Append("TZID:").Append(zone.Id).Append("\r\n");
        if (transitions.Count == 0)
        {
            var offset = zone.GetUtcOffset(new DateTime(fromYear, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            AppendObservance(builder, "STANDARD", offset, offset, new DateTime(1970, 1, 1), []);
        }
        else
        {
            var first = transitions[0];
            // The offset in force before the first planned transition, so a time before it is still covered.
            AppendObservance(builder, first.From > first.To ? "DAYLIGHT" : "STANDARD", first.From, first.From,
                new DateTime(1970, 1, 1), []);
            foreach (var group in transitions.GroupBy(t => (t.From, t.To)))
            {
                var locals = group.Select(t => t.LocalStart).ToList();
                AppendObservance(builder, group.Key.To > group.Key.From ? "DAYLIGHT" : "STANDARD",
                    group.Key.From, group.Key.To, locals[0], locals.Skip(1));
            }
        }

        builder.Append("END:VTIMEZONE\r\n");
        var text = builder.ToString();
        return Encoding.UTF8.GetByteCount(text) > MaxBytes ? null : text;
    }

    private readonly record struct Transition(DateTime LocalStart, TimeSpan From, TimeSpan To);

    // Every offset change between 1 January fromYear and 31 December toYear, to the minute: scanned daily, then hourly,
    // then bisected.
    private static List<Transition> Transitions(TimeZoneInfo zone, int fromYear, int toYear)
    {
        var transitions = new List<Transition>();
        var start = new DateTime(fromYear, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(toYear, 12, 31, 23, 59, 0, DateTimeKind.Utc);
        var offset = zone.GetUtcOffset(start);
        for (var day = start; day < end; day = day.AddDays(1))
        {
            var next = day.AddDays(1) < end ? day.AddDays(1) : end;
            var nextOffset = zone.GetUtcOffset(next);
            if (nextOffset == offset)
            {
                continue;
            }

            var low = day;
            var high = next;
            while ((high - low).TotalMinutes > 1)
            {
                var mid = low.AddMinutes(Math.Floor((high - low).TotalMinutes / 2));
                if (zone.GetUtcOffset(mid) == offset)
                {
                    low = mid;
                }
                else
                {
                    high = mid;
                }
            }

            var after = zone.GetUtcOffset(high);
            // A transition's DTSTART is the local time in force before it (RFC 5545 §3.6.5).
            transitions.Add(new Transition(DateTime.SpecifyKind(high + offset, DateTimeKind.Unspecified), offset, after));
            offset = after;
        }

        return transitions;
    }

    private static void AppendObservance(
        StringBuilder builder, string kind, TimeSpan from, TimeSpan to, DateTime localStart, IEnumerable<DateTime> rdates)
    {
        builder.Append("BEGIN:").Append(kind).Append("\r\n");
        builder.Append("DTSTART:").Append(Local(localStart)).Append("\r\n");
        foreach (var rdate in rdates)
        {
            builder.Append("RDATE:").Append(Local(rdate)).Append("\r\n");
        }

        builder.Append("TZOFFSETFROM:").Append(Offset(from)).Append("\r\n");
        builder.Append("TZOFFSETTO:").Append(Offset(to)).Append("\r\n");
        builder.Append("END:").Append(kind).Append("\r\n");
    }

    private static string Local(DateTime value) => value.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);

    private static string Offset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset.Duration();
        return abs.Seconds != 0
            ? $"{sign}{abs.Hours:00}{abs.Minutes:00}{abs.Seconds:00}"
            : $"{sign}{abs.Hours:00}{abs.Minutes:00}";
    }
}
