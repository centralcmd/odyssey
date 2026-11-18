using Odyssey.Core;
using Odyssey.Core.Finance;
using Odyssey.Context;

namespace Odyssey.Core.Journal;

/// <summary>
/// Projects a <see cref="RecurrencePattern"/>'s bounded rule into concrete occurrence (start, end)
/// pairs. Recurrence is always bounded (exactly one of <see cref="RecurrencePattern.RecurrenceEndDate"/>
/// / <see cref="RecurrencePattern.OccurrenceCount"/> is set, validated by the caller), and generation
/// stops as soon as it would exceed the caller-supplied occurrence cap — a single incremental pass
/// serves both as the projection check and the actual materialization, since the day-of-month/weekday
/// stepping rules that decide "how many occurrences" and "which dates" are the same rules.
/// </summary>
/// <remarks>
/// The occurrence cap is a <strong>parameter</strong>, not a constant this class reads for itself
/// (issue #434 key 11). It is database-backed and admin-editable now, and this type is a static helper
/// with no dependency-injection seam of its own, so each caller resolves one <c>JournalLimits</c>
/// snapshot per request and threads the number through. The cap is <em>tighten-only</em>: it persists
/// one calendar row per generated occurrence, so raising it would be a write multiplier available to
/// every holder of <c>calendar.create</c> whose cost survives lowering the setting back.
/// </remarks>
internal static class RecurrenceOccurrenceGenerator
{
    /// <summary>
    /// The rule's occurrences, clamping a day-of-month the month lacks to its last day (issue #519 §3 component 1). Each
    /// occurrence carries its <see cref="GeneratedOccurrence.Key"/> — the local wall-clock start — and its stored
    /// start and end: the instant on a zoned series, the wall-clock on a floating one.
    /// </summary>
    public static List<GeneratedOccurrence> Generate(RecurrencePattern pattern, int maxGeneratedOccurrences)
    {
        var frame = Frame.Of(pattern);
        var occurrences = new List<GeneratedOccurrence>();

        foreach (var key in EnumerateStarts(pattern, frame.LocalStart))
        {
            var occurrence = frame.Occurrence(key);
            if (frame.PastUntil(occurrence))
            {
                break;
            }

            occurrences.Add(occurrence);

            if (occurrences.Count > maxGeneratedOccurrences)
            {
                throw new DomainValidationException(
                    $"This recurrence would generate more than {maxGeneratedOccurrences} events — narrow the range or increase the interval.");
            }

            if (pattern.OccurrenceCount is { } count && occurrences.Count >= count)
            {
                break;
            }
        }

        return occurrences;
    }

    /// <summary>
    /// Projects the pattern's rule the way a strict RFC 5545 reader would — a month/year whose
    /// requested day-of-month doesn't exist (e.g. the 31st of a 30-day month, Feb 30) is *skipped*
    /// rather than clamped to the last valid day (which is what <see cref="Generate"/> does). Zone-aware
    /// like <see cref="Generate"/> (issue #519): the ICS exporter compares its key set with the series'
    /// (<c>CanExportWithExceptions</c>). Returns <c>null</c> if the rule can never reach its
    /// <see cref="RecurrencePattern.OccurrenceCount"/> (a rule whose day never exists), so the caller
    /// falls back to flattening.
    /// </summary>
    internal static List<GeneratedOccurrence>? GenerateRfcLiteral(
        RecurrencePattern pattern, int maxGeneratedOccurrences)
    {
        // A bounded pattern that produces valid dates finishes far below this; the guard only trips on
        // a degenerate rule (e.g. FEB 30 yearly) that skips forever, in which case flattening is right.
        const int maxSteps = 500_000;

        var frame = Frame.Of(pattern);
        var occurrences = new List<GeneratedOccurrence>();
        var steps = 0;

        foreach (var start in EnumerateRfcLiteralStarts(pattern, frame.LocalStart))
        {
            if (++steps > maxSteps)
            {
                return null;
            }

            if (start is not { } key)
            {
                continue; // a non-existent day (skipped under strict RFC rules)
            }

            var occurrence = frame.Occurrence(key);
            if (frame.PastUntil(occurrence))
            {
                break;
            }

            occurrences.Add(occurrence);

            if (occurrences.Count > maxGeneratedOccurrences)
            {
                return null;
            }

            if (pattern.OccurrenceCount is { } count && occurrences.Count >= count)
            {
                break;
            }
        }

        return occurrences;
    }

    // The rule's local frame: the zone (null when floating), the local anchor and the nominal local duration.
    private sealed record Frame(RecurrencePattern Pattern, TimeZoneInfo? Zone, DateTime LocalStart, TimeSpan LocalDuration)
    {
        public static Frame Of(RecurrencePattern pattern)
        {
            var zone = pattern.IsAllDay ? null : CalendarTimeZones.Find(pattern.TimeZoneId);
            var localStart = zone is null
                ? DateTime.SpecifyKind(pattern.StartDateTime, DateTimeKind.Unspecified)
                : CalendarTimeZones.ToWall(pattern.StartDateTime, zone);
            var localEnd = zone is null
                ? DateTime.SpecifyKind(pattern.EndDateTime, DateTimeKind.Unspecified)
                : CalendarTimeZones.ToWall(pattern.EndDateTime, zone);
            return new Frame(pattern, zone, localStart, localEnd - localStart);
        }

        public GeneratedOccurrence Occurrence(DateTime key)
        {
            key = DateTime.SpecifyKind(key, DateTimeKind.Unspecified);
            if (Zone is null)
            {
                return new GeneratedOccurrence(key, key, key + LocalDuration, Adjusted: false);
            }

            var start = CalendarTimeZones.ToUtc(key, Zone, out var adjusted);
            var end = CalendarTimeZones.ToUtc(key + LocalDuration, Zone);
            if (end <= start)
            {
                end = start + LocalDuration; // a nominal end that a gap swallowed keeps the duration
            }

            return new GeneratedOccurrence(key, start, end, adjusted);
        }

        // UNTIL is inclusive: an instant on a zoned series, a wall-clock on a floating one (#519 v3 §C).
        public bool PastUntil(GeneratedOccurrence occurrence) =>
            Pattern.RecurrenceEndDate is { } until && occurrence.Start > until;
    }

    // Mirrors EnumerateStarts but yields null for a month/year whose requested day does not exist,
    // instead of clamping it to the last valid day.
    private static IEnumerable<DateTime?> EnumerateRfcLiteralStarts(RecurrencePattern pattern, DateTime anchor)
    {
        var timeOfDay = anchor.TimeOfDay;

        switch (pattern.Frequency)
        {
            case RecurrenceFrequency.Daily:
            case RecurrenceFrequency.Weekly:
                foreach (var start in EnumerateStarts(pattern, anchor))
                {
                    yield return start; // Daily/Weekly never clamp — identical to the generator
                }

                break;

            case RecurrenceFrequency.Monthly:
                for (var k = 0; ; k++)
                {
                    var baseMonth = new DateTime(anchor.Year, anchor.Month, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMonths(k * pattern.Interval);
                    var requestedDay = pattern.DayOfMonth ?? anchor.Day;
                    if (requestedDay > DateTime.DaysInMonth(baseMonth.Year, baseMonth.Month))
                    {
                        yield return null;
                        continue;
                    }

                    yield return new DateTime(baseMonth.Year, baseMonth.Month, requestedDay, 0, 0, 0, DateTimeKind.Unspecified) + timeOfDay;
                }

            case RecurrenceFrequency.Yearly:
                for (var k = 0; ; k++)
                {
                    var year = anchor.Year + (k * pattern.Interval);
                    var month = pattern.MonthOfYear ?? anchor.Month;
                    var requestedDay = pattern.DayOfMonth ?? anchor.Day;
                    if (requestedDay > DateTime.DaysInMonth(year, month))
                    {
                        yield return null;
                        continue;
                    }

                    yield return new DateTime(year, month, requestedDay, 0, 0, 0, DateTimeKind.Unspecified) + timeOfDay;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(pattern), pattern.Frequency, "Unknown recurrence frequency.");
        }
    }

    private static IEnumerable<DateTime> EnumerateStarts(RecurrencePattern pattern, DateTime anchor)
    {
        var timeOfDay = anchor.TimeOfDay;

        switch (pattern.Frequency)
        {
            case RecurrenceFrequency.Daily:
                for (var k = 0; ; k++)
                {
                    yield return anchor.AddDays((double)k * pattern.Interval);
                }

            case RecurrenceFrequency.Weekly:
            {
                // Only defined day bits can ever match. A mask with none (a row written before issue
                // #243's validation, or by a non-validating path) has no next occurrence, so stop
                // rather than stepping a day at a time until DateTime overflows.
                var days = (pattern.DaysOfWeek ?? DaysOfWeekFlags.None) & AllDays;
                if (days == DaysOfWeekFlags.None)
                {
                    yield break;
                }

                var anchorWeekStart = StartOfWeek(anchor.Date);
                for (var date = anchor.Date; ; date = date.AddDays(1))
                {
                    var weeksSinceAnchor = (StartOfWeek(date) - anchorWeekStart).Days / 7;
                    if (weeksSinceAnchor % pattern.Interval == 0 && HasFlag(days, date.DayOfWeek))
                    {
                        yield return date + timeOfDay;
                    }
                }
            }

            case RecurrenceFrequency.Monthly:
                // Anchor-relative stepping: each step's base month is computed from the ORIGINAL anchor
                // (anchor.AddMonths(k*Interval)), never from the previously clamped occurrence — so an
                // end-of-month anchor (e.g. the 31st) recovers its day in a longer month instead of
                // permanently drifting to the 28th after a short month (mirrors ContractService's
                // next-charge projection).
                for (var k = 0; ; k++)
                {
                    var baseMonth = new DateTime(anchor.Year, anchor.Month, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMonths(k * pattern.Interval);
                    var day = Math.Min(pattern.DayOfMonth ?? anchor.Day, DateTime.DaysInMonth(baseMonth.Year, baseMonth.Month));
                    yield return new DateTime(baseMonth.Year, baseMonth.Month, day, 0, 0, 0, DateTimeKind.Unspecified) + timeOfDay;
                }

            case RecurrenceFrequency.Yearly:
                for (var k = 0; ; k++)
                {
                    var year = anchor.Year + (k * pattern.Interval);
                    var month = pattern.MonthOfYear ?? anchor.Month;
                    var day = Math.Min(pattern.DayOfMonth ?? anchor.Day, DateTime.DaysInMonth(year, month));
                    yield return new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Unspecified) + timeOfDay;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(pattern), pattern.Frequency, "Unknown recurrence frequency.");
        }
    }

    // The persistence-side twin of DaysOfWeekFlagsExtensions.AllDays (the DTO enum is a separate type);
    // RecurrenceOccurrenceGeneratorTests pins the two to the same bits.
    internal const DaysOfWeekFlags AllDays = DaysOfWeekFlags.Monday | DaysOfWeekFlags.Tuesday
        | DaysOfWeekFlags.Wednesday | DaysOfWeekFlags.Thursday | DaysOfWeekFlags.Friday
        | DaysOfWeekFlags.Saturday | DaysOfWeekFlags.Sunday;

    // Weeks run Monday-Sunday to match DaysOfWeekFlags' Monday-first ordering.
    private static DateTime StartOfWeek(DateTime date)
    {
        var mondayBasedOffset = ((int)date.DayOfWeek + 6) % 7; // .NET DayOfWeek is Sunday=0..Saturday=6
        return date.Date.AddDays(-mondayBasedOffset);
    }

    private static bool HasFlag(DaysOfWeekFlags flags, DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => flags.HasFlag(DaysOfWeekFlags.Monday),
        DayOfWeek.Tuesday => flags.HasFlag(DaysOfWeekFlags.Tuesday),
        DayOfWeek.Wednesday => flags.HasFlag(DaysOfWeekFlags.Wednesday),
        DayOfWeek.Thursday => flags.HasFlag(DaysOfWeekFlags.Thursday),
        DayOfWeek.Friday => flags.HasFlag(DaysOfWeekFlags.Friday),
        DayOfWeek.Saturday => flags.HasFlag(DaysOfWeekFlags.Saturday),
        DayOfWeek.Sunday => flags.HasFlag(DaysOfWeekFlags.Sunday),
        _ => false,
    };
}

/// <summary>
/// One generated occurrence (issue #519 §3 component 1): its key — the local wall-clock start under the rule, with
/// <c>Kind = Unspecified</c> — and its stored start and end, the instant on a zoned series and the wall-clock on a
/// floating one. <see cref="Adjusted"/> is set when the key fell in a DST gap and the start moved forward.
/// </summary>
internal readonly record struct GeneratedOccurrence(DateTime Key, DateTime Start, DateTime End, bool Adjusted)
{
    public void Deconstruct(out DateTime start, out DateTime end)
    {
        start = Start;
        end = End;
    }
}
