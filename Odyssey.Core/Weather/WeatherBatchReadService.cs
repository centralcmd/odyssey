using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Weather;

/// <summary>
/// The event and entry weather batch reads (issue #430 §3.3, §5.2, §5.3): one request answers up to
/// <see cref="WeatherRules.BatchMaxIds"/> journal entries or calendar events, so a list costs one request rather
/// than one per row.
/// <para>
/// Three things are load-bearing. This service takes no <see cref="WeatherFetchService"/>, so a batch can never
/// call MET or Frost — loading a page cannot fan out into outbound fetches. Resolution and status come from
/// <see cref="WeatherPointResolver"/>, the same rules the single reads use. And every stored-data read is per
/// CLUSTER of nearby dates, never one read across the span of the whole batch, so a request mixing a very old and
/// a far-future record makes two small reads instead of one across years of retained forecast runs.
/// </para>
/// </summary>
public sealed class WeatherBatchReadService
{
    /// <summary>Members closer than this join one cluster (issue #430 §3.3 step 5.4).</summary>
    internal static readonly TimeSpan ClusterGap = TimeSpan.FromDays(2);

    /// <summary>A cluster never spans more than this, unless one member alone is longer (a long all-day event).</summary>
    internal static readonly TimeSpan ClusterSpan = TimeSpan.FromDays(7);

    private readonly OdysseyContext context;
    private readonly WeatherPointResolver resolver;
    private readonly TimeProvider time;

    public WeatherBatchReadService(OdysseyContext context, IWeatherSettingsLookup settings, TimeProvider? time = null)
    {
        this.context = context;
        this.time = time ?? TimeProvider.System;
        resolver = new WeatherPointResolver(context, settings, this.time);
    }

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    // ── Journal ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>GET /api/journal-entries/weather</c>: one item per known id, in request order; unknown ids are omitted.
    /// The journal never reads <c>OutOfRange</c>, as its single read never does.
    /// </summary>
    public async Task<JournalEntryWeatherBatch> JournalEntriesAsync(
        IReadOnlyList<Guid> ids, string userId, CancellationToken cancellationToken)
    {
        var entries = await context.JournalEntries.AsNoTracking()
            .Where(e => ids.Contains(e.JournalEntryId))
            .Select(e => new { e.JournalEntryId, e.EntryDate, e.LocationId })
            .ToDictionaryAsync(e => e.JournalEntryId, cancellationToken);
        var ordered = ids.Distinct().Where(entries.ContainsKey).Select(id => entries[id]).ToList();
        if (ordered.Count == 0)
        {
            return new JournalEntryWeatherBatch();
        }

        var set = await resolver.ResolveManyAsync(userId, LocationIds(ordered.Select(e => e.LocationId)), cancellationToken);
        var resolved = ordered.Select(e => (Entry: e, Resolution: set.For(e.LocationId))).ToList();
        var pointIds = resolved.Where(r => r.Resolution.Point is not null).Select(r => r.Resolution.Point!.WeatherPointId).Distinct().ToList();
        var timelines = await TimelinesAsync(pointIds, cancellationToken);
        var isConfigured = await resolver.IsConfiguredAsync(cancellationToken);
        var stale = await resolver.StalePointsAsync(SelectablePoints(resolved.Select(r => r.Resolution)), cancellationToken);

        var dates = resolved
            .Where(r => r.Resolution.Point is not null && timelines.ContainsKey(r.Resolution.Point.WeatherPointId))
            .ToDictionary(r => r.Entry.JournalEntryId,
                r => timelines[r.Resolution.Point!.WeatherPointId].DateForInstant(WeatherReadService.AsUtc(r.Entry.EntryDate)));
        var days = await DaysAsync(
            resolved.Where(r => dates.ContainsKey(r.Entry.JournalEntryId))
                .Select(r => (r.Resolution.Point!.WeatherPointId, From: dates[r.Entry.JournalEntryId], To: dates[r.Entry.JournalEntryId])),
            cancellationToken);

        var items = resolved.Select(r =>
        {
            var (entry, resolution) = r;
            if (resolution.Point is not { } point)
            {
                return new JournalEntryWeatherItem { JournalEntryId = entry.JournalEntryId, Status = WeatherStatus.NoPlace };
            }

            var item = new JournalEntryWeatherItem
            {
                JournalEntryId = entry.JournalEntryId,
                Point = point,
                ResolvedFrom = resolution.From,
                LinkedLocation = resolution.LinkedLocation,
                DistanceKm = resolution.DistanceKm,
            };
            if (!dates.TryGetValue(entry.JournalEntryId, out var date))
            {
                return item with { Status = WeatherPointResolver.Status(resolution, false, isConfigured) };
            }

            days.TryGetValue((point.WeatherPointId, date), out var day);
            return item with
            {
                Status = WeatherPointResolver.Status(resolution, day is not null, isConfigured),
                Stale = resolution.Selectable && stale.Contains(point.WeatherPointId),
                Date = date,
                Day = day,
            };
        }).ToList();
        return new JournalEntryWeatherBatch { Items = items };
    }

    // ── Calendar ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>GET /api/calendar-events/weather</c>: a compact summary per known id, in request order; unknown ids are
    /// omitted. Status follows the single read: a timed event is <c>Ok</c> when its window has rows, an all-day
    /// one when any day of its span (≤ 31) is stored. The summary describes the event's FIRST day, so an all-day
    /// event whose first day is not stored reads <c>Ok</c> with no summary, and the agenda shows nothing for it.
    /// </summary>
    public async Task<CalendarEventWeatherBatch> CalendarEventsAsync(
        IReadOnlyList<Guid> ids, string userId, CancellationToken cancellationToken)
    {
        var events = await context.CalendarEvents.AsNoTracking()
            .Where(e => ids.Contains(e.CalendarEventId))
            .Select(e => new { e.CalendarEventId, e.StartDateTime, e.EndDateTime, e.IsAllDay, e.LocationId })
            .ToDictionaryAsync(e => e.CalendarEventId, cancellationToken);
        var ordered = ids.Distinct().Where(events.ContainsKey).Select(id => events[id]).ToList();
        if (ordered.Count == 0)
        {
            return new CalendarEventWeatherBatch();
        }

        var set = await resolver.ResolveManyAsync(userId, LocationIds(ordered.Select(e => e.LocationId)), cancellationToken);
        var resolved = ordered.Select(e => (Event: e, Resolution: set.For(e.LocationId))).ToList();
        var isConfigured = await resolver.IsConfiguredAsync(cancellationToken);
        var stale = await resolver.StalePointsAsync(SelectablePoints(resolved.Select(r => r.Resolution)), cancellationToken);

        // OutOfRange: beyond the horizon on a point with nothing stored — one existence query for every point that
        // has such an event, never one per item.
        var now = Now;
        var beyond = resolved
            .Where(r => r.Resolution.Point is not null && WeatherReadService.AsUtc(r.Event.StartDateTime) > now + WeatherReadService.Horizon)
            .Select(r => r.Resolution.Point!.WeatherPointId).Distinct().ToList();
        var withData = beyond.Count == 0
            ? new HashSet<Guid>()
            : (await context.WeatherDays.AsNoTracking()
                .Where(d => beyond.Contains(d.WeatherPointId))
                .Select(d => d.WeatherPointId).Distinct().ToListAsync(cancellationToken)).ToHashSet();
        bool OutOfRange(Guid pointId, DateTime startUtc) => startUtc > now + WeatherReadService.Horizon && !withData.Contains(pointId);

        var answerable = resolved
            .Where(r => r.Resolution.Point is not null
                && !OutOfRange(r.Resolution.Point.WeatherPointId, WeatherReadService.AsUtc(r.Event.StartDateTime)))
            .ToList();

        var allDaySpans = answerable.Where(r => r.Event.IsAllDay)
            .ToDictionary(r => r.Event.CalendarEventId, r => AllDaySpan(r.Event.StartDateTime, r.Event.EndDateTime));
        var days = await DaysAsync(
            answerable.Where(r => r.Event.IsAllDay)
                .Select(r => (r.Resolution.Point!.WeatherPointId, allDaySpans[r.Event.CalendarEventId].First, allDaySpans[r.Event.CalendarEventId].Last)),
            cancellationToken);

        var windows = answerable.Where(r => !r.Event.IsAllDay).ToDictionary(r => r.Event.CalendarEventId,
            r => WeatherEventWeather.Window(WeatherReadService.AsUtc(r.Event.StartDateTime), WeatherReadService.AsUtc(r.Event.EndDateTime)));
        var effective = await EffectiveStepsAsync(
            answerable.Where(r => !r.Event.IsAllDay)
                .Select(r => (r.Resolution.Point!.WeatherPointId, windows[r.Event.CalendarEventId].FromUtc, windows[r.Event.CalendarEventId].ToUtc)),
            cancellationToken);

        var items = resolved.Select(r =>
        {
            var (ev, resolution) = r;
            if (resolution.Point is not { } point)
            {
                return new CalendarEventWeatherItem { CalendarEventId = ev.CalendarEventId, Status = WeatherStatus.NoPlace, IsAllDay = ev.IsAllDay };
            }

            var item = new CalendarEventWeatherItem
            {
                CalendarEventId = ev.CalendarEventId,
                Point = point,
                ResolvedFrom = resolution.From,
                IsAllDay = ev.IsAllDay,
            };
            var startUtc = WeatherReadService.AsUtc(ev.StartDateTime);
            if (OutOfRange(point.WeatherPointId, startUtc))
            {
                return item with { Status = WeatherStatus.OutOfRange };
            }

            WeatherEventSummary? summary;
            bool hasData;
            if (ev.IsAllDay)
            {
                var (first, last) = allDaySpans[ev.CalendarEventId];
                hasData = false;
                for (var d = first; d <= last; d = d.AddDays(1))
                {
                    hasData |= days.ContainsKey((point.WeatherPointId, d));
                }

                days.TryGetValue((point.WeatherPointId, first), out var firstDay);
                summary = WeatherEventWeather.Daily(firstDay);
            }
            else
            {
                var (fromUtc, toUtc) = windows[ev.CalendarEventId];
                summary = effective.TryGetValue(point.WeatherPointId, out var steps)
                    ? WeatherEventWeather.Timed(steps, startUtc, fromUtc, toUtc)
                    : null;
                hasData = summary is not null;
            }

            var status = WeatherPointResolver.Status(resolution, hasData, isConfigured);
            return item with
            {
                Status = status,
                Stale = resolution.Selectable && stale.Contains(point.WeatherPointId),
                Summary = status == WeatherStatus.Ok ? summary : null,
            };
        }).ToList();
        return new CalendarEventWeatherBatch { Items = items };
    }

    // ── Clusters ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Greedy, left to right over one point's members sorted by start: a member joins the open cluster when it
    /// starts at most <see cref="ClusterGap"/> after the cluster's end and the cluster would still span at most
    /// <see cref="ClusterSpan"/>; otherwise it opens a new one. A member longer than the span is a cluster alone.
    /// </summary>
    internal static List<(DateTime From, DateTime To)> Clusters(IEnumerable<(DateTime From, DateTime To)> members)
    {
        var clusters = new List<(DateTime From, DateTime To)>();
        foreach (var (from, to) in members.OrderBy(m => m.From).ThenBy(m => m.To))
        {
            if (clusters.Count > 0)
            {
                var (cFrom, cTo) = clusters[^1];
                var end = to > cTo ? to : cTo;
                if (from <= cTo + ClusterGap && end - cFrom <= ClusterSpan)
                {
                    clusters[^1] = (cFrom, end);
                    continue;
                }
            }

            clusters.Add((from, to));
        }

        return clusters;
    }

    private async Task<Dictionary<(Guid PointId, DateOnly Date), WeatherDaySummary>> DaysAsync(
        IEnumerable<(Guid PointId, DateOnly From, DateOnly To)> needs, CancellationToken cancellationToken)
    {
        var result = new Dictionary<(Guid, DateOnly), WeatherDaySummary>();
        foreach (var group in needs.GroupBy(n => n.PointId))
        {
            var pointId = group.Key;
            foreach (var (from, to) in Clusters(group.Select(n => (ToDateTime(n.From), ToDateTime(n.To)))))
            {
                var first = DateOnly.FromDateTime(from);
                var last = DateOnly.FromDateTime(to);
                var rows = await context.WeatherDays.AsNoTracking()
                    .Where(d => d.WeatherPointId == pointId && d.LocalDate >= first && d.LocalDate <= last)
                    .ToListAsync(cancellationToken);
                foreach (var row in rows)
                {
                    result[(pointId, row.LocalDate)] = WeatherReadService.ToSummary(row);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// The effective steps per point over the clusters of its timed events' windows, each read widened by the
    /// longest period so a step straddling a window's start is found.
    /// </summary>
    private async Task<Dictionary<Guid, List<WeatherForecastStep>>> EffectiveStepsAsync(
        IEnumerable<(Guid PointId, DateTime From, DateTime To)> needs, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, List<WeatherForecastStep>>();
        foreach (var group in needs.GroupBy(n => n.PointId))
        {
            var pointId = group.Key;
            var all = new List<WeatherForecastStep>();
            foreach (var (from, to) in Clusters(group.Select(n => (n.From, n.To))))
            {
                all.AddRange(await WeatherComposer
                    .EffectiveStepsQuery(context.WeatherForecastSteps.AsNoTracking(), pointId, WeatherEventWeather.ReadFrom(from), to)
                    .ToListAsync(cancellationToken));
            }

            result[pointId] = WeatherComposer.EffectiveSteps(all);
        }

        return result;
    }

    // ── Shared ───────────────────────────────────────────────────────────────────────────────────

    private async Task<Dictionary<Guid, WeatherSiteTimeline>> TimelinesAsync(IReadOnlyCollection<Guid> pointIds, CancellationToken cancellationToken)
    {
        if (pointIds.Count == 0)
        {
            return [];
        }

        var sites = await context.WeatherPointSites.AsNoTracking()
            .Where(s => pointIds.Contains(s.WeatherPointId)).ToListAsync(cancellationToken);
        return sites.GroupBy(s => s.WeatherPointId).ToDictionary(g => g.Key, g => new WeatherSiteTimeline(g));
    }

    /// <summary>An all-day event's dates: <c>Start … End − 1 day</c>, at least one day, at most 31 (the single read's rule).</summary>
    private static (DateOnly First, DateOnly Last) AllDaySpan(DateTime start, DateTime end)
    {
        var first = DateOnly.FromDateTime(start);
        var last = DateOnly.FromDateTime(end).AddDays(-1);
        if (last < first)
        {
            last = first;
        }

        if (last.DayNumber - first.DayNumber + 1 > WeatherReadService.AllDayMaxDays)
        {
            last = first.AddDays(WeatherReadService.AllDayMaxDays - 1);
        }

        return (first, last);
    }

    private static List<Guid> LocationIds(IEnumerable<Guid?> ids) =>
        ids.Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();

    private static List<Guid> SelectablePoints(IEnumerable<WeatherResolution> resolutions) =>
        resolutions.Where(r => r.Selectable && r.Point is not null).Select(r => r.Point!.WeatherPointId).Distinct().ToList();

    private static DateTime ToDateTime(DateOnly date) => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
}
