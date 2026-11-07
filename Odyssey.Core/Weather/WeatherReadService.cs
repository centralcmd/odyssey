using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.Preferences;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Weather;

/// <summary>
/// The weather reads (issue #348 §5 endpoints 1–5, 11–13; keyed on weather points by issue #387): point
/// resolution, site-aware local dates, the per-instant composition and the on-demand gate. Every response names
/// the point used and carries
/// MET's attribution; none ever reaches Frost (§7.14).
/// </summary>
public sealed class WeatherReadService
{
    /// <summary>How far ahead Locationforecast reaches; a window wholly beyond it is <c>OutOfRange</c>.</summary>
    public static readonly TimeSpan Horizon = TimeSpan.FromDays(10);

    internal const int AllDayMaxDays = WeatherRules.AllDayMaxDays;

    private readonly OdysseyContext context;
    private readonly WeatherFetchService fetch;
    private readonly IWeatherSettingsLookup settings;
    private readonly TimeProvider time;
    private readonly WeatherPointResolver resolver;

    public WeatherReadService(OdysseyContext context, WeatherFetchService fetch, IWeatherSettingsLookup settings, TimeProvider? time = null)
    {
        this.context = context;
        this.fetch = fetch;
        this.settings = settings;
        this.time = time ?? TimeProvider.System;
        resolver = new WeatherPointResolver(context, settings, this.time);
    }

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    // ── Resolution (§3; issue #387 §8) ───────────────────────────────────────────────────────────

    /// <summary>The resolution order lives in <see cref="WeatherPointResolver"/>, shared with the batch reads (issue #430 §3.2).</summary>
    public Task<WeatherResolution> ResolveAsync(
        Guid? explicitId, string userId, CancellationToken cancellationToken, Guid? linkedLocationId = null) =>
        resolver.ResolveAsync(explicitId, userId, cancellationToken, linkedLocationId);

    /// <summary>Selectable: the point exists, its forecast is on and it is not being purged.</summary>
    public Task<bool> IsSelectableAsync(Guid pointId, CancellationToken cancellationToken) =>
        resolver.IsSelectableAsync(pointId, cancellationToken);

    // ── Reads ─────────────────────────────────────────────────────────────────────────────────────

    public async Task<WeatherCurrentResponse> CurrentAsync(Guid? pointId, string userId, CancellationToken cancellationToken)
    {
        var resolution = await ResolveAsync(pointId, userId, cancellationToken);
        if (resolution.Point is not { } point)
        {
            return new WeatherCurrentResponse { Status = WeatherStatus.NoPlace };
        }

        var now = Now;
        await OnDemandAsync(resolution, now, cancellationToken);
        var timeline = await TimelineOrNullAsync(point.WeatherPointId, cancellationToken);

        var steps = await EffectiveStepsAsync(point.WeatherPointId, now.AddHours(-12), now.AddHours(1), cancellationToken);
        var step = WeatherComposer.Covering(steps, now) ?? steps.LastOrDefault(s => s.TimeUtc <= now);
        WeatherDaySummary? today = null;
        if (timeline is not null)
        {
            var date = timeline.DateForInstant(now);
            today = (await DaysAsync(point.WeatherPointId, date, date, cancellationToken)).FirstOrDefault();
        }

        var hasData = step is not null || today is not null;
        return new WeatherCurrentResponse
        {
            Point = point,
            ResolvedFrom = resolution.From,
            Status = await StatusAsync(resolution, hasData, cancellationToken),
            Stale = await StaleAsync(resolution, point.WeatherPointId, cancellationToken),
            Step = step is null ? null : ToStep(step),
            Today = today,
        };
    }

    public async Task<WeatherHourlyResponse> HourlyAsync(WeatherHourlyQuery query, string userId, CancellationToken cancellationToken)
    {
        // Model validation already ran on the HTTP path; this re-check is for a caller that skipped it, so the mode
        // rules hold for every caller and neither branch below can meet a missing value.
        if (query.ModeErrors().FirstOrDefault() is { } invalid)
        {
            var member = invalid.MemberNames.FirstOrDefault() ?? nameof(WeatherHourlyQuery.FromUtc);
            throw new DomainValidationException(
                invalid.ErrorMessage ?? "The hourly query is malformed.", null, char.ToLowerInvariant(member[0]) + member[1..]);
        }

        if (query.Date is not null && query.TryGetDate(out var date))
        {
            return await HourlyDayAsync(date, query.WeatherPointId, userId, cancellationToken);
        }

        var from = AsUtc(query.FromUtc!.Value);
        var to = AsUtc(query.ToUtc!.Value);
        var resolution = await ResolveAsync(query.WeatherPointId, userId, cancellationToken);
        if (resolution.Point is not { } point)
        {
            return new WeatherHourlyResponse { Status = WeatherStatus.NoPlace };
        }

        var now = Now;
        if (from > now + Horizon)
        {
            return new WeatherHourlyResponse { Point = point, ResolvedFrom = resolution.From, Status = WeatherStatus.OutOfRange };
        }

        if (to > now)
        {
            await OnDemandAsync(resolution, now, cancellationToken);
        }

        var steps = await EffectiveStepsAsync(point.WeatherPointId, from, to, cancellationToken);
        return new WeatherHourlyResponse
        {
            Point = point,
            ResolvedFrom = resolution.From,
            Status = await StatusAsync(resolution, steps.Count > 0, cancellationToken),
            Stale = await StaleAsync(resolution, point.WeatherPointId, cancellationToken),
            Steps = steps.Select(ToStep).ToList(),
        };
    }

    /// <summary>
    /// The hourly read's day mode (issue #414): the rows of one point-local date. The date is range-checked against
    /// the point's own today BEFORE any date arithmetic, so an extreme date can never overflow the bounds, and before
    /// the on-demand fetch, so at most the ten dates of the window can trigger one. Today starts at the step covering
    /// now. Every row carries its start and end in the point's clock, each with its own instant's offset.
    /// </summary>
    private async Task<WeatherHourlyResponse> HourlyDayAsync(
        DateOnly date, Guid? pointId, string userId, CancellationToken cancellationToken)
    {
        var resolution = await ResolveAsync(pointId, userId, cancellationToken);
        if (resolution.Point is not { } point)
        {
            return new WeatherHourlyResponse { Status = WeatherStatus.NoPlace };
        }

        var unbounded = new WeatherHourlyDay { Date = date };

        // A point always has a site, but nothing in the schema forces it (CurrentSiteVersion carries no key): with no
        // timeline there is no clock to place the day in, and every date answers the same way.
        var timeline = await TimelineOrNullAsync(point.WeatherPointId, cancellationToken);
        if (timeline is null)
        {
            return new WeatherHourlyResponse { Point = point, ResolvedFrom = resolution.From, Status = WeatherStatus.NoData, Day = unbounded };
        }

        var now = Now;
        var localToday = timeline.DateForInstant(now);
        if (date < localToday || date > localToday.AddDays(WeatherRules.DayModeMaxDaysAhead))
        {
            return new WeatherHourlyResponse { Point = point, ResolvedFrom = resolution.From, Status = WeatherStatus.OutOfRange, Day = unbounded };
        }

        var (fromUtc, toUtc) = timeline.DayBoundsUtc(date);
        var isToday = date == localToday;
        var day = new WeatherHourlyDay
        {
            Date = date,
            IsToday = isToday,
            FromUtc = DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc),
            ToUtc = DateTime.SpecifyKind(toUtc, DateTimeKind.Utc),
            LengthHours = (decimal)(toUtc - fromUtc).TotalHours,
        };

        // localToday follows the instant rule and the bounds the date-ownership rule; after a site moves zone the two
        // can disagree, so today's window starts at the later of now and its own midnight, and may be empty.
        var windowStart = isToday && now > fromUtc ? now : fromUtc;
        if (windowStart >= toUtc)
        {
            return new WeatherHourlyResponse { Point = point, ResolvedFrom = resolution.From, Status = WeatherStatus.Ok, Day = day };
        }

        await OnDemandAsync(resolution, now, cancellationToken);

        var effective = await EffectiveStepsAsync(
            point.WeatherPointId, windowStart.AddHours(-WeatherComposer.LongestPeriodHours), toUtc, cancellationToken);
        var rows = WeatherComposer.DayRows(effective, windowStart, toUtc);
        var zone = WeatherSiteTimeline.Zone(timeline.SiteForDate(date));
        return new WeatherHourlyResponse
        {
            Point = point,
            ResolvedFrom = resolution.From,
            Status = await StatusAsync(resolution, rows.Count > 0, cancellationToken),
            Stale = await StaleAsync(resolution, point.WeatherPointId, cancellationToken),
            Day = day,
            Steps = rows.Select(row => ToStep(row) with
            {
                LocalStart = InZone(row.TimeUtc, zone),
                LocalEnd = InZone(row.TimeUtc.AddHours(row.PeriodHours), zone),
            }).ToList(),
        };
    }

    /// <summary>An instant in <paramref name="zone"/>'s clock, with that zone's offset AT the instant.</summary>
    internal static DateTimeOffset InZone(DateTime instantUtc, TimeZoneInfo zone)
    {
        var utc = new DateTimeOffset(DateTime.SpecifyKind(instantUtc, DateTimeKind.Utc));
        return utc.ToOffset(zone.GetUtcOffset(utc));
    }

    public async Task<WeatherDailyResponse> DailyAsync(WeatherDailyQuery query, string userId, CancellationToken cancellationToken)
    {
        var from = query.From!.Value;
        var to = query.To!.Value;
        var resolution = await ResolveAsync(query.WeatherPointId, userId, cancellationToken);
        if (resolution.Point is not { } point)
        {
            return new WeatherDailyResponse { Status = WeatherStatus.NoPlace };
        }

        var now = Now;
        if (from > DateOnly.FromDateTime(now + Horizon))
        {
            return new WeatherDailyResponse { Point = point, ResolvedFrom = resolution.From, Status = WeatherStatus.OutOfRange };
        }

        // Only a range that reaches the point's own today asks MET for a forecast, as hourly and journal
        // reads only fetch for a future instant. Comparing in the point's zone is what lets a history
        // window ending yesterday stay a pure read; the UTC fallback keeps the margin a west-of-UTC place needs.
        var timeline = await TimelineOrNullAsync(point.WeatherPointId, cancellationToken);
        var localToday = timeline?.DateForInstant(now) ?? DateOnly.FromDateTime(now).AddDays(-1);
        if (to >= localToday)
        {
            await OnDemandAsync(resolution, now, cancellationToken);
        }

        var days = await DaysAsync(point.WeatherPointId, from, to, cancellationToken);
        return new WeatherDailyResponse
        {
            Point = point,
            ResolvedFrom = resolution.From,
            Status = await StatusAsync(resolution, days.Count > 0, cancellationToken),
            Stale = await StaleAsync(resolution, point.WeatherPointId, cancellationToken),
            Days = days,
            History = await HistoryBoundsAsync(point.WeatherPointId, cancellationToken),
        };
    }

    /// <summary>
    /// A calendar event's weather (§3 Local dates): a timed event gets the non-overlapping rows that overlap
    /// <c>[Start, End)</c> clamped to 24 h (at least 1 h, issue #430 Goal 8); an all-day one the dates <c>Start … End − 1 day</c>, at most 31. Null when the event
    /// does not exist.
    /// </summary>
    public async Task<CalendarEventWeatherResponse?> CalendarEventAsync(
        Guid eventId, Guid? pointId, string userId, CancellationToken cancellationToken)
    {
        var calendarEvent = await context.CalendarEvents.AsNoTracking()
            .Where(e => e.CalendarEventId == eventId)
            .Select(e => new { e.StartDateTime, e.EndDateTime, e.IsAllDay, e.LocationId })
            .FirstOrDefaultAsync(cancellationToken);
        if (calendarEvent is null)
        {
            return null;
        }

        var resolution = await ResolveAsync(pointId, userId, cancellationToken, calendarEvent.LocationId);
        if (resolution.Point is not { } point)
        {
            return new CalendarEventWeatherResponse { Status = WeatherStatus.NoPlace, IsAllDay = calendarEvent.IsAllDay };
        }

        var now = Now;
        var start = AsUtc(calendarEvent.StartDateTime);
        var end = AsUtc(calendarEvent.EndDateTime);
        if (start > now + Horizon && !await HasAnyDataAsync(point.WeatherPointId, cancellationToken))
        {
            return new CalendarEventWeatherResponse
            {
                Point = point,
                ResolvedFrom = resolution.From,
                LinkedLocation = resolution.LinkedLocation,
                DistanceKm = resolution.DistanceKm,
                Status = WeatherStatus.OutOfRange,
                IsAllDay = calendarEvent.IsAllDay,
            };
        }

        if (end > now)
        {
            await OnDemandAsync(resolution, now, cancellationToken);
        }

        IReadOnlyList<WeatherStep> steps = [];
        IReadOnlyList<WeatherDaySummary> days = [];
        if (calendarEvent.IsAllDay)
        {
            var first = DateOnly.FromDateTime(calendarEvent.StartDateTime);
            var last = DateOnly.FromDateTime(calendarEvent.EndDateTime).AddDays(-1);
            if (last < first)
            {
                last = first;
            }

            if (last.DayNumber - first.DayNumber + 1 > AllDayMaxDays)
            {
                last = first.AddDays(AllDayMaxDays - 1);
            }

            days = await DaysAsync(point.WeatherPointId, first, last, cancellationToken);
        }
        else
        {
            // The same window and overlap rule as the calendar batch (issue #430 Goal 8): a step that straddles the
            // start is included, and a coarser block overlapped by finer steps is dropped.
            var (fromUtc, toUtc) = WeatherEventWeather.Window(start, end);
            var effective = await EffectiveStepsAsync(point.WeatherPointId, WeatherEventWeather.ReadFrom(fromUtc), toUtc, cancellationToken);
            steps = WeatherEventWeather.Rows(effective, fromUtc, toUtc).Select(ToStep).ToList();
        }

        return new CalendarEventWeatherResponse
        {
            Point = point,
            ResolvedFrom = resolution.From,
            LinkedLocation = resolution.LinkedLocation,
            DistanceKm = resolution.DistanceKm,
            Status = await StatusAsync(resolution, steps.Count > 0 || days.Count > 0, cancellationToken),
            Stale = await StaleAsync(resolution, point.WeatherPointId, cancellationToken),
            IsAllDay = calendarEvent.IsAllDay,
            Steps = steps,
            Days = days,
        };
    }

    /// <summary>
    /// A journal entry's day (§3): <c>EntryDate</c> is an instant; the site whose range contains it gives the
    /// zone, and that date's summary is returned. Null when the entry does not exist.
    /// </summary>
    public async Task<JournalEntryWeatherResponse?> JournalEntryAsync(
        Guid entryId, Guid? pointId, string userId, CancellationToken cancellationToken)
    {
        var entry = await context.JournalEntries.AsNoTracking()
            .Where(e => e.JournalEntryId == entryId)
            .Select(e => new { e.EntryDate, e.LocationId })
            .FirstOrDefaultAsync(cancellationToken);
        if (entry is null)
        {
            return null;
        }

        var entryDate = (DateTime?)entry.EntryDate;
        var resolution = await ResolveAsync(pointId, userId, cancellationToken, entry.LocationId);
        if (resolution.Point is not { } point)
        {
            return new JournalEntryWeatherResponse { Status = WeatherStatus.NoPlace };
        }

        var timeline = await TimelineOrNullAsync(point.WeatherPointId, cancellationToken);
        if (timeline is null)
        {
            return new JournalEntryWeatherResponse
            {
                Point = point,
                ResolvedFrom = resolution.From,
                LinkedLocation = resolution.LinkedLocation,
                DistanceKm = resolution.DistanceKm,
                Status = await StatusAsync(resolution, false, cancellationToken),
            };
        }

        var instant = AsUtc(entryDate.Value);
        var date = timeline.DateForInstant(instant);
        var now = Now;
        if (instant > now)
        {
            await OnDemandAsync(resolution, now, cancellationToken);
        }

        var day = (await DaysAsync(point.WeatherPointId, date, date, cancellationToken)).FirstOrDefault();
        return new JournalEntryWeatherResponse
        {
            Point = point,
            ResolvedFrom = resolution.From,
            LinkedLocation = resolution.LinkedLocation,
            DistanceKm = resolution.DistanceKm,
            Status = await StatusAsync(resolution, day is not null, cancellationToken),
            Stale = await StaleAsync(resolution, point.WeatherPointId, cancellationToken),
            Date = date,
            Day = day,
        };
    }

    // ── Picker and preference (endpoints 9, 14) ───────────────────────────────────────────────────

    /// <summary>
    /// The picker (issue #387 §5 endpoint 9): every point not being purged, with its two switches. With
    /// <paramref name="nearLocationId"/> the whole set is sorted by distance from that place FIRST and only then
    /// truncated to <see cref="WeatherRules.OptionsMax"/>, so the nearest point is never cut off; without it, by
    /// name. Null when <paramref name="nearLocationId"/> names no place. No coordinates leave this method.
    /// </summary>
    public async Task<WeatherPointOptions?> OptionsAsync(Guid? nearLocationId, CancellationToken cancellationToken)
    {
        (decimal Latitude, decimal Longitude)? near = null;
        if (nearLocationId is { } pointId)
        {
            var place = await context.Locations.AsNoTracking()
                .Where(l => l.LocationId == pointId)
                .Select(l => new { l.Latitude, l.Longitude })
                .FirstOrDefaultAsync(cancellationToken);
            if (place is null)
            {
                return null;
            }

            if (place is { Latitude: { } lat, Longitude: { } lon })
            {
                near = (lat, lon);
            }
        }

        var live = from point in context.WeatherPoints.AsNoTracking()
                   join site in context.WeatherPointSites.AsNoTracking()
                       on new { point.WeatherPointId, Version = point.CurrentSiteVersion } equals new { site.WeatherPointId, Version = site.SiteVersion }
                   where point.PurgeStartedAt == null
                   select new { point.WeatherPointId, point.Name, point.ForecastEnabled, point.HistoryEnabled, site.Latitude, site.Longitude };

        List<WeatherPointOption> points;
        if (near is { } origin)
        {
            // In memory: a household's points are few, and haversine does not translate.
            points = (await live.ToListAsync(cancellationToken))
                .Select(p => new WeatherPointOption
                {
                    WeatherPointId = p.WeatherPointId,
                    Name = p.Name,
                    ForecastEnabled = p.ForecastEnabled,
                    HistoryEnabled = p.HistoryEnabled,
                    DistanceKm = WeatherDistance.Km(origin.Latitude, origin.Longitude, p.Latitude, p.Longitude),
                })
                .OrderBy(p => p.DistanceKm).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.WeatherPointId)
                .Take(WeatherRules.OptionsMax)
                .ToList();
        }
        else
        {
            points = await live
                .OrderBy(p => p.Name).ThenBy(p => p.WeatherPointId)
                .Take(WeatherRules.OptionsMax)
                .Select(p => new WeatherPointOption
                {
                    WeatherPointId = p.WeatherPointId,
                    Name = p.Name,
                    ForecastEnabled = p.ForecastEnabled,
                    HistoryEnabled = p.HistoryEnabled,
                })
                .ToListAsync(cancellationToken);
        }

        var household = (await settings.GetAsync(cancellationToken)).HouseholdDefaultPointId;
        return new WeatherPointOptions
        {
            Points = points,
            HouseholdDefaultPointId = household is { } id && await IsSelectableAsync(id, cancellationToken) ? id : null,
        };
    }

    public async Task<WeatherPreferences> PreferencesAsync(string userId, CancellationToken cancellationToken)
    {
        var preferred = await resolver.PreferredPointIdAsync(userId, cancellationToken);
        var household = (await settings.GetAsync(cancellationToken)).HouseholdDefaultPointId;
        return new WeatherPreferences
        {
            DefaultPoint = await SelectableRefAsync(preferred, cancellationToken),
            HouseholdDefaultPoint = await SelectableRefAsync(household, cancellationToken),
        };
    }

    /// <summary>Saves the caller's own preference row. A point that is not selectable is a 400 keyed <c>defaultPointId</c>.</summary>
    public async Task SetPreferenceAsync(string userId, UpdateWeatherPreferences request, CancellationToken cancellationToken)
    {
        if (request.DefaultPointId is { } id && !await IsSelectableAsync(id, cancellationToken))
        {
            throw new DomainValidationException(
                "That weather point has no forecast. Choose one from the list.", code: null, field: "defaultPointId");
        }

        var json = WeatherPointResolver.SerializePreference(request.DefaultPointId);
        await new UserPreferencesService(context, time)
            .UpsertServerOwnedAsync(userId, UserPreferencesService.WeatherKey, json, cancellationToken);
    }

    private async Task<WeatherPointRef?> SelectableRefAsync(Guid? id, CancellationToken cancellationToken)
    {
        if (id is not { } pointId || !await IsSelectableAsync(pointId, cancellationToken))
        {
            return null;
        }

        return await context.WeatherPoints.AsNoTracking().Where(p => p.WeatherPointId == pointId)
            .Select(p => new WeatherPointRef { WeatherPointId = p.WeatherPointId, Name = p.Name })
            .FirstOrDefaultAsync(cancellationToken);
    }

    // ── Shared ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The on-demand gate (§3): an enabled, untracked, selectable point whose shared backoff has expired
    /// gets one synchronous fetch. <see cref="WeatherFetchService"/> applies the backoff and the claim; a
    /// tracked point is the scheduler's alone.
    /// </summary>
    private async Task OnDemandAsync(WeatherResolution resolution, DateTime now, CancellationToken cancellationToken)
    {
        if (resolution is { Selectable: true, Tracked: false, Point: { } point })
        {
            await fetch.FetchForecastAsync(point.WeatherPointId, WeatherFetchMode.OnDemand, cancellationToken);
        }
    }

    private Task<WeatherStatus> StatusAsync(WeatherResolution resolution, bool hasData, CancellationToken cancellationToken) =>
        resolver.StatusAsync(resolution, hasData, cancellationToken);

    private Task<bool> StaleAsync(WeatherResolution resolution, Guid pointId, CancellationToken cancellationToken) =>
        resolver.StaleAsync(resolution, pointId, cancellationToken);

    private async Task<List<WeatherForecastStep>> EffectiveStepsAsync(
        Guid pointId, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken) =>
        WeatherComposer.EffectiveSteps(await WeatherComposer
            .EffectiveStepsQuery(context.WeatherForecastSteps.AsNoTracking(), pointId, fromUtc, toUtc)
            .ToListAsync(cancellationToken));

    private async Task<List<WeatherDaySummary>> DaysAsync(
        Guid pointId, DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        (await context.WeatherDays.AsNoTracking()
            .Where(d => d.WeatherPointId == pointId && d.LocalDate >= from && d.LocalDate <= to)
            .OrderBy(d => d.LocalDate)
            .ToListAsync(cancellationToken))
        .Select(ToSummary)
        .ToList();

    private async Task<WeatherHistoryBounds> HistoryBoundsAsync(Guid pointId, CancellationToken cancellationToken)
    {
        var measured = await context.WeatherDays.AsNoTracking()
            .Where(d => d.WeatherPointId == pointId && d.ObservedFetchedAtUtc != null)
            .GroupBy(d => d.WeatherPointId)
            .Select(g => new { From = g.Min(d => d.LocalDate), Through = g.Max(d => d.LocalDate) })
            .FirstOrDefaultAsync(cancellationToken);
        // "No station" is a lookup's answer, never the absence of one: an unresolved position is not reported
        // as having no station just because nothing has asked Frost yet.
        var resolvedAt = await context.WeatherPoints.AsNoTracking()
            .Where(w => w.WeatherPointId == pointId).Select(w => w.StationResolvedAtUtc).FirstOrDefaultAsync(cancellationToken);
        var stations = await context.WeatherStations.AsNoTracking().Where(st => st.WeatherPointId == pointId).ToListAsync(cancellationToken);
        return new WeatherHistoryBounds
        {
            MeasuredFrom = measured?.From,
            MeasuredThrough = measured?.Through,
            HasObservationStation = WeatherStationGroups.HasAny(resolvedAt, stations),
            Stations = WeatherStationGroups.Refs(stations),
            ObservationsConfigured = await fetch.ObservationsConfiguredAsync(cancellationToken),
        };
    }

    private Task<bool> HasAnyDataAsync(Guid pointId, CancellationToken cancellationToken) =>
        context.WeatherDays.AsNoTracking().AnyAsync(d => d.WeatherPointId == pointId, cancellationToken);

    private async Task<WeatherSiteTimeline?> TimelineOrNullAsync(Guid pointId, CancellationToken cancellationToken)
    {
        var sites = await context.WeatherPointSites.AsNoTracking()
            .Where(s => s.WeatherPointId == pointId).ToListAsync(cancellationToken);
        return sites.Count == 0 ? null : new WeatherSiteTimeline(sites);
    }

    internal static WeatherStep ToStep(WeatherForecastStep step) => new()
    {
        TimeUtc = DateTime.SpecifyKind(step.TimeUtc, DateTimeKind.Utc),
        PeriodHours = step.PeriodHours,
        IssuedAtUtc = DateTime.SpecifyKind(step.RunProviderUpdatedAt, DateTimeKind.Utc),
        SymbolCode = step.SymbolCode,
        AirTemperatureC = step.AirTemperatureC,
        PrecipitationMm = step.PrecipitationMm,
        PrecipitationProbabilityPct = step.PrecipitationProbabilityPct,
        WindSpeedMps = step.WindSpeedMps,
        WindGustMps = step.WindGustMps,
        WindFromDirectionDeg = step.WindFromDirectionDeg,
        RelativeHumidityPct = step.RelativeHumidityPct,
        CloudAreaFractionPct = step.CloudAreaFractionPct,
        AirPressureHpa = step.AirPressureHpa,
        UvIndexClearSky = step.UvIndexClearSky,
        TempMinC = step.TempMinC,
        TempMaxC = step.TempMaxC,
    };

    internal static WeatherDaySummary ToSummary(WeatherDay day) => new()
    {
        Date = day.LocalDate,
        Forecast = day.ForecastCoverageHours is { } coverage
            ? new WeatherDayForecast
            {
                SymbolCode = day.ForecastSymbolCode,
                MinTempC = day.ForecastMinTempC,
                MaxTempC = day.ForecastMaxTempC,
                PrecipitationMm = day.ForecastPrecipitationMm,
                MeanWindSpeedMps = day.ForecastMeanWindSpeedMps,
                MaxWindGustMps = day.ForecastMaxWindGustMps,
                CoverageHours = coverage,
                IssuedAtUtc = day.ForecastIssuedAtUtc is { } issued ? DateTime.SpecifyKind(issued, DateTimeKind.Utc) : null,
            }
            : null,
        Observed = day.ObservedFetchedAtUtc is { } fetched
            ? new WeatherDayObserved
            {
                MinTempC = day.ObservedMinTempC,
                MaxTempC = day.ObservedMaxTempC,
                MeanTempC = day.ObservedMeanTempC,
                PrecipitationMm = day.ObservedPrecipitationMm,
                MeanWindSpeedMps = day.ObservedMeanWindSpeedMps,
                MaxWindGustMps = day.ObservedMaxWindGustMps,
                FetchedAtUtc = DateTime.SpecifyKind(fetched, DateTimeKind.Utc),
            }
            : null,
    };

    internal static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
