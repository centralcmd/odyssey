using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.Preferences;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Weather;

/// <summary>
/// The weather reads (issue #348 §5 endpoints 1–5, 11–13): location resolution, site-aware local dates,
/// the per-instant composition and the on-demand gate. Every response names the location used and carries
/// MET's attribution; none ever reaches Frost (§7.14).
/// </summary>
public sealed class WeatherReadService
{
    /// <summary>How far ahead Locationforecast reaches; a window wholly beyond it is <c>OutOfRange</c>.</summary>
    public static readonly TimeSpan Horizon = TimeSpan.FromDays(10);

    /// <summary>The least age at which a forecast reads as stale, however short the refresh interval.</summary>
    private static readonly TimeSpan StaleFloor = TimeSpan.FromHours(3);

    private const int AllDayMaxDays = 31;

    private readonly OdysseyContext context;
    private readonly WeatherFetchService fetch;
    private readonly IWeatherSettingsLookup settings;
    private readonly TimeProvider time;

    public WeatherReadService(OdysseyContext context, WeatherFetchService fetch, IWeatherSettingsLookup settings, TimeProvider? time = null)
    {
        this.context = context;
        this.fetch = fetch;
        this.settings = settings;
        this.time = time ?? TimeProvider.System;
    }

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    // ── Resolution (§3) ───────────────────────────────────────────────────────────────────────────

    /// <summary>What a read resolved to. <see cref="Location"/> is null when nothing resolved (<c>NoPlace</c>).</summary>
    public sealed record Resolution(WeatherLocationRef? Location, bool Selectable, bool Tracked);

    /// <summary>
    /// Explicit id, then the caller's own preference if selectable, then the household default if selectable.
    /// An explicit id with no location row is a 404; one that exists but is not selectable still returns stored
    /// history and never fetches.
    /// </summary>
    public async Task<Resolution> ResolveAsync(Guid? explicitId, string userId, CancellationToken cancellationToken)
    {
        if (explicitId is { } id)
        {
            var name = await context.Locations.AsNoTracking().Where(l => l.LocationId == id)
                .Select(l => l.Name).FirstOrDefaultAsync(cancellationToken)
                ?? throw new DomainNotFoundException($"Location ID {id} not found.");
            var state = await SelectableStateAsync(id, cancellationToken);
            return new Resolution(new WeatherLocationRef { LocationId = id, Name = name }, state.Selectable, state.Tracked);
        }

        var preferred = await PreferredLocationIdAsync(userId, cancellationToken);
        var household = (await settings.GetAsync(cancellationToken)).HouseholdDefaultLocationId;

        foreach (var candidate in new[] { preferred, household })
        {
            if (candidate is { } candidateId && await SelectableStateAsync(candidateId, cancellationToken) is { Selectable: true } state)
            {
                var name = await context.Locations.AsNoTracking().Where(l => l.LocationId == candidateId)
                    .Select(l => l.Name).FirstAsync(cancellationToken);
                return new Resolution(new WeatherLocationRef { LocationId = candidateId, Name = name }, true, state.Tracked);
            }
        }

        return new Resolution(null, false, false);
    }

    /// <summary>Selectable: the location exists, is not archived, has weather enabled and is not being purged.</summary>
    private async Task<(bool Selectable, bool Tracked)> SelectableStateAsync(Guid locationId, CancellationToken cancellationToken)
    {
        var row = await (
                from weather in context.WeatherLocations.AsNoTracking()
                join location in context.Locations.AsNoTracking() on weather.LocationId equals location.LocationId
                where weather.LocationId == locationId
                select new { weather.ForecastEnabled, weather.PurgeStartedAt, weather.IsTracked, location.Archived })
            .FirstOrDefaultAsync(cancellationToken);
        var selectable = row is not null && row.ForecastEnabled && row.PurgeStartedAt is null && row.Archived is null;
        return (selectable, selectable && row!.IsTracked);
    }

    public async Task<bool> IsSelectableAsync(Guid locationId, CancellationToken cancellationToken) =>
        (await SelectableStateAsync(locationId, cancellationToken)).Selectable;

    // ── Reads ─────────────────────────────────────────────────────────────────────────────────────

    public async Task<WeatherCurrentResponse> CurrentAsync(Guid? locationId, string userId, CancellationToken cancellationToken)
    {
        var resolution = await ResolveAsync(locationId, userId, cancellationToken);
        if (resolution.Location is not { } location)
        {
            return new WeatherCurrentResponse { Status = WeatherStatus.NoPlace };
        }

        var now = Now;
        await OnDemandAsync(resolution, now, cancellationToken);
        var timeline = await TimelineOrNullAsync(location.LocationId, cancellationToken);

        var steps = await EffectiveStepsAsync(location.LocationId, now.AddHours(-12), now.AddHours(1), cancellationToken);
        var step = WeatherComposer.Covering(steps, now) ?? steps.LastOrDefault(s => s.TimeUtc <= now);
        WeatherDaySummary? today = null;
        if (timeline is not null)
        {
            var date = timeline.DateForInstant(now);
            today = (await DaysAsync(location.LocationId, date, date, cancellationToken)).FirstOrDefault();
        }

        var hasData = step is not null || today is not null;
        return new WeatherCurrentResponse
        {
            Location = location,
            Status = await StatusAsync(resolution, hasData, cancellationToken),
            Stale = await StaleAsync(resolution, location.LocationId, cancellationToken),
            Step = step is null ? null : ToStep(step),
            Today = today,
        };
    }

    public async Task<WeatherHourlyResponse> HourlyAsync(WeatherHourlyQuery query, string userId, CancellationToken cancellationToken)
    {
        var from = AsUtc(query.FromUtc!.Value);
        var to = AsUtc(query.ToUtc!.Value);
        var resolution = await ResolveAsync(query.LocationId, userId, cancellationToken);
        if (resolution.Location is not { } location)
        {
            return new WeatherHourlyResponse { Status = WeatherStatus.NoPlace };
        }

        var now = Now;
        if (from > now + Horizon)
        {
            return new WeatherHourlyResponse { Location = location, Status = WeatherStatus.OutOfRange };
        }

        if (to > now)
        {
            await OnDemandAsync(resolution, now, cancellationToken);
        }

        var steps = await EffectiveStepsAsync(location.LocationId, from, to, cancellationToken);
        return new WeatherHourlyResponse
        {
            Location = location,
            Status = await StatusAsync(resolution, steps.Count > 0, cancellationToken),
            Stale = await StaleAsync(resolution, location.LocationId, cancellationToken),
            Steps = steps.Select(ToStep).ToList(),
        };
    }

    public async Task<WeatherDailyResponse> DailyAsync(WeatherDailyQuery query, string userId, CancellationToken cancellationToken)
    {
        var from = query.From!.Value;
        var to = query.To!.Value;
        var resolution = await ResolveAsync(query.LocationId, userId, cancellationToken);
        if (resolution.Location is not { } location)
        {
            return new WeatherDailyResponse { Status = WeatherStatus.NoPlace };
        }

        var now = Now;
        if (from > DateOnly.FromDateTime(now + Horizon))
        {
            return new WeatherDailyResponse { Location = location, Status = WeatherStatus.OutOfRange };
        }

        // Only a range that reaches the location's own today asks MET for a forecast, as hourly and journal
        // reads only fetch for a future instant. Comparing in the location's zone is what lets a history
        // window ending yesterday stay a pure read; the UTC fallback keeps the margin a west-of-UTC place needs.
        var timeline = await TimelineOrNullAsync(location.LocationId, cancellationToken);
        var localToday = timeline?.DateForInstant(now) ?? DateOnly.FromDateTime(now).AddDays(-1);
        if (to >= localToday)
        {
            await OnDemandAsync(resolution, now, cancellationToken);
        }

        var days = await DaysAsync(location.LocationId, from, to, cancellationToken);
        return new WeatherDailyResponse
        {
            Location = location,
            Status = await StatusAsync(resolution, days.Count > 0, cancellationToken),
            Stale = await StaleAsync(resolution, location.LocationId, cancellationToken),
            Days = days,
            History = await HistoryBoundsAsync(location.LocationId, cancellationToken),
        };
    }

    /// <summary>
    /// A calendar event's weather (§3 Local dates): a timed event gets the hourly window <c>[Start, End)</c>
    /// clamped to 24 h; an all-day one the dates <c>Start … End − 1 day</c>, at most 31. Null when the event
    /// does not exist.
    /// </summary>
    public async Task<CalendarEventWeatherResponse?> CalendarEventAsync(
        Guid eventId, Guid? locationId, string userId, CancellationToken cancellationToken)
    {
        var calendarEvent = await context.CalendarEvents.AsNoTracking()
            .Where(e => e.CalendarEventId == eventId)
            .Select(e => new { e.StartDateTime, e.EndDateTime, e.IsAllDay })
            .FirstOrDefaultAsync(cancellationToken);
        if (calendarEvent is null)
        {
            return null;
        }

        var resolution = await ResolveAsync(locationId, userId, cancellationToken);
        if (resolution.Location is not { } location)
        {
            return new CalendarEventWeatherResponse { Status = WeatherStatus.NoPlace, IsAllDay = calendarEvent.IsAllDay };
        }

        var now = Now;
        var start = AsUtc(calendarEvent.StartDateTime);
        var end = AsUtc(calendarEvent.EndDateTime);
        if (start > now + Horizon && !await HasAnyDataAsync(location.LocationId, cancellationToken))
        {
            return new CalendarEventWeatherResponse { Location = location, Status = WeatherStatus.OutOfRange, IsAllDay = calendarEvent.IsAllDay };
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

            days = await DaysAsync(location.LocationId, first, last, cancellationToken);
        }
        else
        {
            var clampedEnd = end - start > TimeSpan.FromHours(24) ? start.AddHours(24) : end;
            steps = (await EffectiveStepsAsync(location.LocationId, start, clampedEnd > start ? clampedEnd : start.AddHours(1), cancellationToken))
                .Select(ToStep).ToList();
        }

        return new CalendarEventWeatherResponse
        {
            Location = location,
            Status = await StatusAsync(resolution, steps.Count > 0 || days.Count > 0, cancellationToken),
            Stale = await StaleAsync(resolution, location.LocationId, cancellationToken),
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
        Guid entryId, Guid? locationId, string userId, CancellationToken cancellationToken)
    {
        var entryDate = await context.JournalEntries.AsNoTracking()
            .Where(e => e.JournalEntryId == entryId)
            .Select(e => (DateTime?)e.EntryDate)
            .FirstOrDefaultAsync(cancellationToken);
        if (entryDate is null)
        {
            return null;
        }

        var resolution = await ResolveAsync(locationId, userId, cancellationToken);
        if (resolution.Location is not { } location)
        {
            return new JournalEntryWeatherResponse { Status = WeatherStatus.NoPlace };
        }

        var timeline = await TimelineOrNullAsync(location.LocationId, cancellationToken);
        if (timeline is null)
        {
            return new JournalEntryWeatherResponse
            {
                Location = location,
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

        var day = (await DaysAsync(location.LocationId, date, date, cancellationToken)).FirstOrDefault();
        return new JournalEntryWeatherResponse
        {
            Location = location,
            Status = await StatusAsync(resolution, day is not null, cancellationToken),
            Stale = await StaleAsync(resolution, location.LocationId, cancellationToken),
            Date = date,
            Day = day,
        };
    }

    // ── Picker and preference (endpoints 11–13) ───────────────────────────────────────────────────

    public async Task<WeatherLocationOptions> OptionsAsync(CancellationToken cancellationToken)
    {
        var live =
            from weather in context.WeatherLocations.AsNoTracking()
            join location in context.Locations.AsNoTracking() on weather.LocationId equals location.LocationId
            where weather.PurgeStartedAt == null && location.Archived == null
            select new { weather.ForecastEnabled, weather.HistoryEnabled, location.LocationId, location.Name };

        // The forecast places are the dashboard picker's list; the history places are where a location's
        // Weather history exists. A place may be in either, both or neither.
        var locations = await live.Where(w => w.ForecastEnabled)
            .OrderBy(w => w.Name).ThenBy(w => w.LocationId)
            .Select(w => new WeatherLocationRef { LocationId = w.LocationId, Name = w.Name })
            .Take(WeatherRules.OptionsMax)
            .ToListAsync(cancellationToken);
        var history = await live.Where(w => w.HistoryEnabled)
            .OrderBy(w => w.Name).ThenBy(w => w.LocationId)
            .Select(w => new WeatherLocationRef { LocationId = w.LocationId, Name = w.Name })
            .Take(WeatherRules.OptionsMax)
            .ToListAsync(cancellationToken);
        var household = (await settings.GetAsync(cancellationToken)).HouseholdDefaultLocationId;
        return new WeatherLocationOptions
        {
            Locations = locations,
            HouseholdDefaultLocationId = household is { } id && locations.Any(l => l.LocationId == id) ? id : null,
            HistoryLocations = history,
        };
    }

    public async Task<WeatherPreferences> PreferencesAsync(string userId, CancellationToken cancellationToken)
    {
        var preferred = await PreferredLocationIdAsync(userId, cancellationToken);
        var household = (await settings.GetAsync(cancellationToken)).HouseholdDefaultLocationId;
        return new WeatherPreferences
        {
            DefaultLocation = await SelectableRefAsync(preferred, cancellationToken),
            HouseholdDefaultLocation = await SelectableRefAsync(household, cancellationToken),
        };
    }

    /// <summary>Saves the caller's own preference row. A location that is not selectable is a 400 keyed <c>defaultLocationId</c>.</summary>
    public async Task SetPreferenceAsync(string userId, UpdateWeatherPreferences request, CancellationToken cancellationToken)
    {
        if (request.DefaultLocationId is { } id && !await IsSelectableAsync(id, cancellationToken))
        {
            throw new DomainValidationException(
                "That location has no weather. Choose one from the list.", code: null, field: "defaultLocationId");
        }

        var json = JsonSerializer.Serialize(new StoredPreference(request.DefaultLocationId), PreferenceJson);
        await new UserPreferencesService(context, time)
            .UpsertServerOwnedAsync(userId, UserPreferencesService.WeatherKey, json, cancellationToken);
    }

    private static readonly JsonSerializerOptions PreferenceJson = new(JsonSerializerDefaults.Web);

    private sealed record StoredPreference(Guid? DefaultLocationId);

    /// <summary>
    /// The caller's stored location id, or null. A row that does not parse reads as no preference, never a
    /// failure; whatever it names is re-checked for selectability by every caller.
    /// </summary>
    private async Task<Guid?> PreferredLocationIdAsync(string userId, CancellationToken cancellationToken)
    {
        var json = await context.UserPreferences.AsNoTracking()
            .Where(p => p.UserId == userId && p.Key == UserPreferencesService.WeatherKey)
            .Select(p => p.PreferencesJson)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StoredPreference>(json, PreferenceJson)?.DefaultLocationId;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<WeatherLocationRef?> SelectableRefAsync(Guid? id, CancellationToken cancellationToken)
    {
        if (id is not { } locationId || !await IsSelectableAsync(locationId, cancellationToken))
        {
            return null;
        }

        return await context.Locations.AsNoTracking().Where(l => l.LocationId == locationId)
            .Select(l => new WeatherLocationRef { LocationId = l.LocationId, Name = l.Name })
            .FirstOrDefaultAsync(cancellationToken);
    }

    // ── Shared ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The on-demand gate (§3): an enabled, untracked, selectable location whose shared backoff has expired
    /// gets one synchronous fetch. <see cref="WeatherFetchService"/> applies the backoff and the claim; a
    /// tracked location is the scheduler's alone.
    /// </summary>
    private async Task OnDemandAsync(Resolution resolution, DateTime now, CancellationToken cancellationToken)
    {
        if (resolution is { Selectable: true, Tracked: false, Location: { } location })
        {
            await fetch.FetchForecastAsync(location.LocationId, WeatherFetchMode.OnDemand, cancellationToken);
        }
    }

    private async Task<WeatherStatus> StatusAsync(Resolution resolution, bool hasData, CancellationToken cancellationToken)
    {
        if (hasData)
        {
            return WeatherStatus.Ok;
        }

        if (!resolution.Selectable)
        {
            return WeatherStatus.WeatherDisabled;
        }

        return (await settings.GetAsync(cancellationToken)).IsConfigured ? WeatherStatus.NoData : WeatherStatus.NotConfigured;
    }

    /// <summary>
    /// Stale: the last refresh failed, OR the newest forecast is older than three refresh intervals (never
    /// under 3 h). The age half covers what a recorded failure cannot: a scheduled place paused past a
    /// lowered cap, or a scheduler that is not running, would otherwise serve a frozen forecast as current.
    /// </summary>
    private async Task<bool> StaleAsync(Resolution resolution, Guid locationId, CancellationToken cancellationToken)
    {
        if (!resolution.Selectable)
        {
            return false;
        }

        var schedule = await context.WeatherLocationSchedules.AsNoTracking()
            .Where(s => s.LocationId == locationId)
            .Select(s => new { s.ForecastFailureKind, s.LastForecastSuccessUtc })
            .FirstOrDefaultAsync(cancellationToken);
        if (schedule is null)
        {
            return false;
        }

        if (schedule.ForecastFailureKind is not null)
        {
            return true;
        }

        var interval = TimeSpan.FromMinutes((await settings.GetAsync(cancellationToken)).RefreshIntervalMinutes);
        var staleAfter = interval * 3 > StaleFloor ? interval * 3 : StaleFloor;
        return schedule.LastForecastSuccessUtc is { } last && Now - last > staleAfter;
    }


    private async Task<List<WeatherForecastStep>> EffectiveStepsAsync(
        Guid locationId, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken) =>
        WeatherComposer.EffectiveSteps(await WeatherComposer
            .EffectiveStepsQuery(context.WeatherForecastSteps.AsNoTracking(), locationId, fromUtc, toUtc)
            .ToListAsync(cancellationToken));

    private async Task<List<WeatherDaySummary>> DaysAsync(
        Guid locationId, DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        (await context.WeatherDays.AsNoTracking()
            .Where(d => d.LocationId == locationId && d.LocalDate >= from && d.LocalDate <= to)
            .OrderBy(d => d.LocalDate)
            .ToListAsync(cancellationToken))
        .Select(ToSummary)
        .ToList();

    private async Task<WeatherHistoryBounds> HistoryBoundsAsync(Guid locationId, CancellationToken cancellationToken)
    {
        var measured = await context.WeatherDays.AsNoTracking()
            .Where(d => d.LocationId == locationId && d.ObservedFetchedAtUtc != null)
            .GroupBy(d => d.LocationId)
            .Select(g => new { From = g.Min(d => d.LocalDate), Through = g.Max(d => d.LocalDate) })
            .FirstOrDefaultAsync(cancellationToken);
        // "No station" is a lookup's answer, never the absence of one: an unresolved position is not reported
        // as having no station just because nothing has asked Frost yet.
        var knownToHaveNone = await context.WeatherLocations.AsNoTracking()
            .AnyAsync(w => w.LocationId == locationId && w.FrostStationId == null && w.StationResolvedAtUtc != null, cancellationToken);
        return new WeatherHistoryBounds
        {
            MeasuredFrom = measured?.From,
            MeasuredThrough = measured?.Through,
            HasObservationStation = !knownToHaveNone,
            ObservationsConfigured = await fetch.ObservationsConfiguredAsync(cancellationToken),
        };
    }

    private Task<bool> HasAnyDataAsync(Guid locationId, CancellationToken cancellationToken) =>
        context.WeatherDays.AsNoTracking().AnyAsync(d => d.LocationId == locationId, cancellationToken);

    private async Task<WeatherSiteTimeline?> TimelineOrNullAsync(Guid locationId, CancellationToken cancellationToken)
    {
        var sites = await context.WeatherLocationSites.AsNoTracking()
            .Where(s => s.LocationId == locationId).ToListAsync(cancellationToken);
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

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
