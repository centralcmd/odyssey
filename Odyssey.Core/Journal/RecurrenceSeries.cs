using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Dtos;
using ContextCalendarEvent = Odyssey.Context.CalendarEvent;

namespace Odyssey.Core.Journal;

/// <summary>
/// One series as its writers see it under the series lock (issue #519 §3 component 2): the pattern, every row still
/// linked to it, and its exclusions. Loaded tracked for a write, untracked for the preview.
/// </summary>
internal sealed class RecurrenceSeriesState
{
    public required RecurrencePattern Pattern { get; init; }

    public required List<ContextCalendarEvent> Rows { get; init; }

    public required List<RecurrenceExclusion> Exclusions { get; init; }

    public static async Task<RecurrenceSeriesState?> LoadAsync(
        OdysseyContext context, Guid patternId, bool tracked, CancellationToken cancellationToken)
    {
        var patterns = tracked ? context.RecurrencePatterns : context.RecurrencePatterns.AsNoTracking();
        var pattern = await patterns.FirstOrDefaultAsync(p => p.RecurrencePatternId == patternId, cancellationToken);
        if (pattern is null)
        {
            return null;
        }

        var rows = tracked ? context.CalendarEvents : context.CalendarEvents.AsNoTracking();
        var exclusions = tracked ? context.RecurrenceExclusions : context.RecurrenceExclusions.AsNoTracking();
        return new RecurrenceSeriesState
        {
            Pattern = pattern,
            Rows = await rows.Where(e => e.RecurrencePatternId == patternId)
                .OrderBy(e => e.StartDateTime).ThenBy(e => e.CalendarEventId)
                .ToListAsync(cancellationToken),
            Exclusions = await exclusions.Where(x => x.RecurrencePatternId == patternId).ToListAsync(cancellationToken),
        };
    }
}

/// <summary>What one write changed or would change in a series (issue #519 §5): the counts every caller reports.</summary>
internal sealed record RecurrenceSeriesCounts
{
    public int DroppedOverrides { get; set; }

    public int DroppedExclusions { get; set; }

    /// <summary>Non-override rows removed because their key left the schedule.</summary>
    public int RemovedRows { get; set; }

    public int Detached { get; set; }

    public int Adjusted { get; set; }

    /// <summary>Whether the change removes a stored row or discards a stored exception — the import's gate (§3 step 3).</summary>
    public bool RemovesOrDiscards => DroppedOverrides + DroppedExclusions + RemovedRows > 0;
}

/// <summary>
/// The series rules (issue #519 §3 component 2, §8.2, §11): keying a legacy series, and reconciling a series with a new
/// rule by key-set membership alone. Shared by the series edit, its preview, the occurrence writes, the import and the
/// backfill job, so there is one rule, not two (v3 §D).
/// </summary>
internal static class RecurrenceSeries
{
    /// <summary>
    /// The keying ceiling (AR4): the occurrence cap's maximum bound, never the admin-lowerable value, so lowering the
    /// setting cannot make a stored series unkeyable.
    /// </summary>
    public const int KeyingCeiling = SystemSettingsBounds.RecurrenceMaxGeneratedOccurrencesMax;

    /// <summary>"Now" as a key of <paramref name="pattern"/>: the local wall-clock in its zone, or the UTC wall-clock when floating.</summary>
    public static DateTime NowKey(RecurrencePattern pattern, DateTime nowUtc) =>
        !pattern.IsAllDay && CalendarTimeZones.Find(pattern.TimeZoneId) is { } zone
            ? CalendarTimeZones.ToWall(nowUtc, zone)
            : DateTime.SpecifyKind(nowUtc, DateTimeKind.Unspecified);

    /// <summary>Whether a row's own fields differ from what its series would generate for it.</summary>
    public static bool DiffersFromTemplate(ContextCalendarEvent row, RecurrencePattern pattern, GeneratedOccurrence occurrence) =>
        row.Title != pattern.Title || row.Description != pattern.Description || row.Location != pattern.Location
        || row.LocationId != pattern.LocationId || row.IsAllDay != pattern.IsAllDay || row.CalendarId != pattern.CalendarId
        || row.EndDateTime != occurrence.End;

    /// <summary>
    /// Keys an un-keyed series in place (issue #519 §11 steps 1–5): a row whose start equals a key takes it (the
    /// earliest-created of duplicates; the rest are detached), a future matched row that differs from the template
    /// becomes an override, every unmatched row is detached to a standalone event, and a future key with no row is
    /// recorded as an exclusion. Rotates <see cref="RecurrencePattern.Version"/>. A no-op on a keyed series.
    /// </summary>
    /// <returns>The rows detached; the caller adds <paramref name="addExclusion"/>'s rows to the context.</returns>
    public static int KeyInPlace(
        RecurrenceSeriesState state, DateTime nowUtc, string? userId, Action<RecurrenceExclusion>? addExclusion)
    {
        var pattern = state.Pattern;
        if (pattern.KeysBackfilledAt is not null)
        {
            return 0;
        }

        var occurrences = RecurrenceOccurrenceGenerator.Generate(pattern, KeyingCeiling);
        var nowKey = NowKey(pattern, nowUtc);
        var unmatched = state.Rows
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.CalendarEventId)
            .ToList();
        var byStart = unmatched
            .GroupBy(r => (r.StartDateTime, r.TimeZoneId))
            .ToDictionary(g => g.Key, g => new Queue<ContextCalendarEvent>(g));
        var excluded = state.Exclusions.Select(x => x.OccurrenceKey).ToHashSet();
        var matched = new HashSet<ContextCalendarEvent>();

        foreach (var occurrence in occurrences)
        {
            if (byStart.TryGetValue((occurrence.Start, pattern.TimeZoneId), out var queue) && queue.Count > 0)
            {
                var row = queue.Dequeue();
                matched.Add(row);
                row.OccurrenceKey = occurrence.Key;
                // Past rows are never flagged: their difference is history, not an edit (AR4).
                row.IsOverride = occurrence.Key >= nowKey && DiffersFromTemplate(row, pattern, occurrence);
                continue;
            }

            if (occurrence.Key >= nowKey && excluded.Add(occurrence.Key))
            {
                var exclusion = new RecurrenceExclusion
                {
                    RecurrencePatternId = pattern.RecurrencePatternId,
                    OccurrenceKey = occurrence.Key,
                    CreatedAt = nowUtc,
                    CreatedByUserId = userId,
                };
                state.Exclusions.Add(exclusion);
                addExclusion?.Invoke(exclusion);
            }
        }

        var detached = 0;
        foreach (var row in unmatched.Where(r => !matched.Contains(r)))
        {
            Detach(row);
            state.Rows.Remove(row);
            detached++;
        }

        pattern.KeysBackfilledAt = nowUtc;
        pattern.Version = Guid.NewGuid();
        return detached;
    }

    /// <summary>A row leaves its series: standalone from now on, with its date and fields unchanged (§4 invariant 4).</summary>
    public static void Detach(ContextCalendarEvent row)
    {
        row.RecurrencePatternId = null;
        row.OccurrenceKey = null;
        row.IsOverride = false;
    }

    /// <summary>
    /// Plans reconciling <paramref name="state"/> with <paramref name="target"/>'s rule and template (issue #519 §3
    /// component 2): over future keys only, every non-override row whose key survives is updated in place, a new key gets
    /// a row, a row whose key is gone is deleted, and an override or exclusion survives exactly while its key does. A
    /// zone change re-expresses every linked row's wall-clock in the new zone (v4 §5). Mutates nothing.
    /// </summary>
    public static RecurrenceSeriesPlan Plan(
        RecurrenceSeriesState state, RecurrencePattern target, DateTime nowUtc, int maxGeneratedOccurrences)
    {
        var occurrences = RecurrenceOccurrenceGenerator.Generate(target, maxGeneratedOccurrences);
        var plan = new RecurrenceSeriesPlan { Target = target, Occurrences = occurrences };
        var nowKey = NowKey(target, nowUtc);
        var byKey = occurrences.ToDictionary(o => o.Key);

        var newZoneId = target.IsAllDay ? null : target.TimeZoneId;
        if (newZoneId != state.Pattern.TimeZoneId || target.IsAllDay != state.Pattern.IsAllDay)
        {
            var newZone = CalendarTimeZones.Find(newZoneId);
            foreach (var row in state.Rows.Where(r => !r.IsAllDay && r.TimeZoneId != newZoneId))
            {
                var wallStart = CalendarTimeZones.WallOf(row.StartDateTime, row.TimeZoneId);
                var wallEnd = CalendarTimeZones.WallOf(row.EndDateTime, row.TimeZoneId);
                var adjusted = false;
                var start = newZone is null ? wallStart : CalendarTimeZones.ToUtc(wallStart, newZone, out adjusted);
                var end = newZone is null ? wallEnd : CalendarTimeZones.ToUtc(wallEnd, newZone);
                plan.Reinterpretations.Add((row, start, end));
                if (adjusted)
                {
                    plan.Counts.Adjusted++;
                }
            }
        }

        var rowKeys = new HashSet<DateTime>();
        foreach (var row in state.Rows)
        {
            if (row.OccurrenceKey is not { } key)
            {
                continue;
            }

            rowKeys.Add(key);
            if (key < nowKey)
            {
                continue;
            }

            if (byKey.TryGetValue(key, out var occurrence))
            {
                if (!row.IsOverride)
                {
                    plan.Updates.Add((row, occurrence));
                }
            }
            else
            {
                plan.RowRemovals.Add(row);
                if (row.IsOverride)
                {
                    plan.Counts.DroppedOverrides++;
                }
                else
                {
                    plan.Counts.RemovedRows++;
                }
            }
        }

        var excludedKeys = new HashSet<DateTime>();
        foreach (var exclusion in state.Exclusions)
        {
            excludedKeys.Add(exclusion.OccurrenceKey);
            if (exclusion.OccurrenceKey >= nowKey && !byKey.ContainsKey(exclusion.OccurrenceKey))
            {
                plan.ExclusionRemovals.Add(exclusion);
                plan.Counts.DroppedExclusions++;
            }
        }

        foreach (var occurrence in occurrences)
        {
            if (occurrence.Key >= nowKey && !rowKeys.Contains(occurrence.Key) && !excludedKeys.Contains(occurrence.Key))
            {
                plan.Inserts.Add(occurrence);
                if (occurrence.Adjusted)
                {
                    plan.Counts.Adjusted++;
                }
            }
        }

        foreach (var (_, occurrence) in plan.Updates)
        {
            if (occurrence.Adjusted)
            {
                plan.Counts.Adjusted++;
            }
        }

        return plan;
    }

    /// <summary>
    /// Applies a plan to a tracked series: copies the target's rule and template onto the stored pattern, rotates its
    /// <see cref="RecurrencePattern.Version"/>, and stages the row and exclusion changes on <paramref name="context"/>.
    /// </summary>
    public static void Apply(
        OdysseyContext context, RecurrenceSeriesState state, RecurrenceSeriesPlan plan, string userId, DateTime nowUtc)
    {
        var pattern = state.Pattern;
        if (!ReferenceEquals(pattern, plan.Target))
        {
            CopyRule(plan.Target, pattern);
        }

        pattern.TimeZoneId = pattern.IsAllDay ? null : pattern.TimeZoneId;
        pattern.UpdatedByUserId = userId;
        pattern.UpdatedAt = nowUtc;
        pattern.Version = Guid.NewGuid();

        foreach (var (row, start, end) in plan.Reinterpretations)
        {
            row.StartDateTime = start;
            row.EndDateTime = end;
            row.TimeZoneId = pattern.TimeZoneId;
        }

        foreach (var (row, occurrence) in plan.Updates)
        {
            CopyTemplate(pattern, row);
            row.StartDateTime = occurrence.Start;
            row.EndDateTime = occurrence.End;
            row.UpdatedByUserId = userId;
            row.UpdatedAt = nowUtc;
        }

        foreach (var row in plan.RowRemovals)
        {
            context.CalendarEvents.Remove(row);
            state.Rows.Remove(row);
        }

        foreach (var exclusion in plan.ExclusionRemovals)
        {
            context.RecurrenceExclusions.Remove(exclusion);
            state.Exclusions.Remove(exclusion);
        }

        foreach (var occurrence in plan.Inserts)
        {
            var row = NewOccurrence(pattern, occurrence, userId, nowUtc);
            context.CalendarEvents.Add(row);
            state.Rows.Add(row);
        }
    }

    /// <summary>A new generated row of <paramref name="pattern"/> for <paramref name="occurrence"/>.</summary>
    public static ContextCalendarEvent NewOccurrence(
        RecurrencePattern pattern, GeneratedOccurrence occurrence, string? userId, DateTime nowUtc)
    {
        var row = new ContextCalendarEvent
        {
            CalendarId = pattern.CalendarId,
            Title = pattern.Title,
            StartDateTime = occurrence.Start,
            EndDateTime = occurrence.End,
            RecurrencePatternId = pattern.RecurrencePatternId,
            OccurrenceKey = occurrence.Key,
            CreatedByUserId = userId,
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc,
        };
        CopyTemplate(pattern, row);
        return row;
    }

    /// <summary>The template fields every non-override row carries from its series.</summary>
    public static void CopyTemplate(RecurrencePattern pattern, ContextCalendarEvent row)
    {
        row.CalendarId = pattern.CalendarId;
        row.Title = pattern.Title;
        row.Description = pattern.Description;
        row.Location = pattern.Location;
        row.LocationId = pattern.LocationId;
        row.IsAllDay = pattern.IsAllDay;
        row.TimeZoneId = pattern.IsAllDay ? null : pattern.TimeZoneId;
    }

    /// <summary>The rule and template fields of a pattern, onto another — never its ids, UIDs, keys or version.</summary>
    public static void CopyRule(RecurrencePattern source, RecurrencePattern target)
    {
        target.CalendarId = source.CalendarId;
        target.Title = source.Title;
        target.Description = source.Description;
        target.Location = source.Location;
        target.LocationId = source.LocationId;
        target.StartDateTime = source.StartDateTime;
        target.EndDateTime = source.EndDateTime;
        target.IsAllDay = source.IsAllDay;
        target.TimeZoneId = source.IsAllDay ? null : source.TimeZoneId;
        target.Frequency = source.Frequency;
        target.Interval = source.Interval;
        target.DaysOfWeek = source.DaysOfWeek;
        target.DayOfMonth = source.DayOfMonth;
        target.MonthOfYear = source.MonthOfYear;
        target.RecurrenceEndDate = source.RecurrenceEndDate;
        target.OccurrenceCount = source.OccurrenceCount;
    }

    /// <summary>A detached copy of a pattern's rule, for planning against without touching the stored row.</summary>
    public static RecurrencePattern CloneRule(RecurrencePattern source)
    {
        var copy = new RecurrencePattern
        {
            RecurrencePatternId = source.RecurrencePatternId,
            CalendarId = source.CalendarId,
            Title = source.Title,
        };
        CopyRule(source, copy);
        copy.KeysBackfilledAt = source.KeysBackfilledAt;
        copy.Version = source.Version;
        return copy;
    }
}

/// <summary>The changes one reconcile makes (issue #519 §3 component 2), computed before any is applied.</summary>
internal sealed class RecurrenceSeriesPlan
{
    public required RecurrencePattern Target { get; init; }

    public required List<GeneratedOccurrence> Occurrences { get; init; }

    public RecurrenceSeriesCounts Counts { get; } = new();

    public List<(ContextCalendarEvent Row, GeneratedOccurrence Occurrence)> Updates { get; } = [];

    public List<ContextCalendarEvent> RowRemovals { get; } = [];

    public List<RecurrenceExclusion> ExclusionRemovals { get; } = [];

    public List<GeneratedOccurrence> Inserts { get; } = [];

    public List<(ContextCalendarEvent Row, DateTime Start, DateTime End)> Reinterpretations { get; } = [];

    /// <summary>The target's key set, for matching a file's <c>EXDATE</c> and <c>RECURRENCE-ID</c> values.</summary>
    public HashSet<DateTime> KeySet => keySet ??= Occurrences.Select(o => o.Key).ToHashSet();

    private HashSet<DateTime>? keySet;
}
