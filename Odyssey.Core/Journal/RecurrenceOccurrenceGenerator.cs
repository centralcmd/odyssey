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
    public static List<(DateTime Start, DateTime End)> Generate(RecurrencePattern pattern, int maxGeneratedOccurrences)
    {
        var duration = pattern.EndDateTime - pattern.StartDateTime;
        var occurrences = new List<(DateTime Start, DateTime End)>();

        foreach (var start in EnumerateStarts(pattern))
        {
            if (pattern.RecurrenceEndDate is { } endDate && start > endDate)
            {
                break;
            }

            occurrences.Add((start, start + duration));

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
    /// rather than clamped to the last valid day (which is what <see cref="Generate"/> does). Used by
    /// the ICS exporter (issue #330) to decide whether a stored series can be faithfully re-emitted as
    /// a single <c>RRULE</c>: only when its actual generated rows exactly match this unclamped
    /// projection is the RRULE safe to export (otherwise an external reader would compute different
    /// dates than Odyssey stores). Returns <c>null</c> if the rule can never reach its
    /// <see cref="RecurrencePattern.OccurrenceCount"/> (a rule whose day never exists), so the caller
    /// falls back to flattening.
    /// </summary>
    internal static List<(DateTime Start, DateTime End)>? GenerateRfcLiteral(
        RecurrencePattern pattern, int maxGeneratedOccurrences)
    {
        // A bounded pattern that produces valid dates finishes far below this; the guard only trips on
        // a degenerate rule (e.g. FEB 30 yearly) that skips forever, in which case flattening is right.
        const int maxSteps = 500_000;

        var duration = pattern.EndDateTime - pattern.StartDateTime;
        var occurrences = new List<(DateTime, DateTime)>();
        var steps = 0;

        foreach (var start in EnumerateRfcLiteralStarts(pattern))
        {
            if (++steps > maxSteps)
            {
                return null;
            }

            if (start is not { } value)
            {
                continue; // a non-existent day (skipped under strict RFC rules)
            }

            if (pattern.RecurrenceEndDate is { } endDate && value > endDate)
            {
                break;
            }

            occurrences.Add((value, value + duration));

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

    // Mirrors EnumerateStarts but yields null for a month/year whose requested day does not exist,
    // instead of clamping it to the last valid day.
    private static IEnumerable<DateTime?> EnumerateRfcLiteralStarts(RecurrencePattern pattern)
    {
        var anchor = pattern.StartDateTime;
        var timeOfDay = anchor.TimeOfDay;

        switch (pattern.Frequency)
        {
            case RecurrenceFrequency.Daily:
            case RecurrenceFrequency.Weekly:
                foreach (var start in EnumerateStarts(pattern))
                {
                    yield return start; // Daily/Weekly never clamp — identical to the generator
                }

                break;

            case RecurrenceFrequency.Monthly:
                for (var k = 0; ; k++)
                {
                    var baseMonth = new DateTime(anchor.Year, anchor.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(k * pattern.Interval);
                    var requestedDay = pattern.DayOfMonth ?? anchor.Day;
                    if (requestedDay > DateTime.DaysInMonth(baseMonth.Year, baseMonth.Month))
                    {
                        yield return null;
                        continue;
                    }

                    yield return new DateTime(baseMonth.Year, baseMonth.Month, requestedDay, 0, 0, 0, DateTimeKind.Utc) + timeOfDay;
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

                    yield return new DateTime(year, month, requestedDay, 0, 0, 0, DateTimeKind.Utc) + timeOfDay;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(pattern), pattern.Frequency, "Unknown recurrence frequency.");
        }
    }

    private static IEnumerable<DateTime> EnumerateStarts(RecurrencePattern pattern)
    {
        var anchor = pattern.StartDateTime;
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
                var days = pattern.DaysOfWeek ?? DaysOfWeekFlags.None;
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
                // permanently drifting to the 28th after a short month (mirrors SubscriptionService.NextBilling).
                for (var k = 0; ; k++)
                {
                    var baseMonth = new DateTime(anchor.Year, anchor.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(k * pattern.Interval);
                    var day = Math.Min(pattern.DayOfMonth ?? anchor.Day, DateTime.DaysInMonth(baseMonth.Year, baseMonth.Month));
                    yield return new DateTime(baseMonth.Year, baseMonth.Month, day, 0, 0, 0, DateTimeKind.Utc) + timeOfDay;
                }

            case RecurrenceFrequency.Yearly:
                for (var k = 0; ; k++)
                {
                    var year = anchor.Year + (k * pattern.Interval);
                    var month = pattern.MonthOfYear ?? anchor.Month;
                    var day = Math.Min(pattern.DayOfMonth ?? anchor.Day, DateTime.DaysInMonth(year, month));
                    yield return new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc) + timeOfDay;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(pattern), pattern.Frequency, "Unknown recurrence frequency.");
        }
    }

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
