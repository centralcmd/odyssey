using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.Preferences;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Weather;

/// <summary>
/// What a weather read resolved to (issue #387 §8). <see cref="Point"/> is null when nothing resolved
/// (<c>NoPlace</c>), and then <see cref="From"/> is null too. <see cref="LinkedLocation"/> and
/// <see cref="DistanceKm"/> are filled only for <c>Linked</c>.
/// </summary>
public sealed record WeatherResolution(
    WeatherPointRef? Point,
    bool Selectable,
    bool Tracked,
    WeatherResolvedFrom? From = null,
    WeatherLinkedLocationRef? LinkedLocation = null,
    int? DistanceKm = null);

/// <summary>
/// The one rule set for "which weather point answers this read, and with what status" (issue #430 §3.2), shared
/// by the single reads (<see cref="WeatherReadService"/>) and the event/entry batch reads
/// (<see cref="WeatherBatchReadService"/>) so the two can never disagree.
/// <para>
/// Order: an explicit point id, then — for an event or entry — the point its place links to, then the caller's
/// own preference if selectable, then the household default if selectable. An explicit id with no point is a 404.
/// An explicit or linked point that is not selectable (forecast off, or being purged) still returns stored
/// history, never fetches and never falls back. A place with no link is skipped, so the preference and the default
/// apply.
/// </para>
/// </summary>
public sealed class WeatherPointResolver
{
    /// <summary>The least age at which a forecast reads as stale, however short the refresh interval.</summary>
    private static readonly TimeSpan StaleFloor = TimeSpan.FromHours(3);

    private static readonly JsonSerializerOptions PreferenceJson = new(JsonSerializerDefaults.Web);

    private readonly OdysseyContext context;
    private readonly IWeatherSettingsLookup settings;
    private readonly TimeProvider time;

    public WeatherPointResolver(OdysseyContext context, IWeatherSettingsLookup settings, TimeProvider? time = null)
    {
        this.context = context;
        this.settings = settings;
        this.time = time ?? TimeProvider.System;
    }

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>The stored shape, <c>{"defaultPointId": "…"}</c> (issue #387 renamed it from <c>defaultLocationId</c>).</summary>
    internal sealed record StoredPreference(Guid? DefaultPointId);

    internal static string SerializePreference(Guid? defaultPointId) =>
        JsonSerializer.Serialize(new StoredPreference(defaultPointId), PreferenceJson);

    // ── One item ─────────────────────────────────────────────────────────────────────────────────

    public async Task<WeatherResolution> ResolveAsync(
        Guid? explicitId, string userId, CancellationToken cancellationToken, Guid? linkedLocationId = null)
    {
        if (explicitId is { } id)
        {
            return await ResolveFixedAsync(id, WeatherResolvedFrom.Explicit, cancellationToken)
                ?? throw new DomainNotFoundException($"Weather point ID {id} not found.");
        }

        var many = await ResolveManyAsync(userId, linkedLocationId is { } l ? [l] : [], cancellationToken);
        return many.For(linkedLocationId);
    }

    /// <summary>The point <paramref name="id"/> as a resolution, or null when no such point exists.</summary>
    public async Task<WeatherResolution?> ResolveFixedAsync(Guid id, WeatherResolvedFrom from, CancellationToken cancellationToken)
    {
        var row = await context.WeatherPoints.AsNoTracking()
            .Where(p => p.WeatherPointId == id)
            .Select(p => new PointRow(p.WeatherPointId, p.Name, p.ForecastEnabled, p.PurgeStartedAt, p.IsTracked))
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : Fixed(row, from);
    }

    /// <summary>Selectable: the point exists, its forecast is on and it is not being purged.</summary>
    public Task<bool> IsSelectableAsync(Guid pointId, CancellationToken cancellationToken) =>
        context.WeatherPoints.AsNoTracking()
            .AnyAsync(p => p.WeatherPointId == pointId && p.ForecastEnabled && p.PurgeStartedAt == null, cancellationToken);

    // ── Many items ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Prefetches everything resolution needs for a set of linked places — the caller's preference, the household
    /// default, every linked place, every candidate point and the linked points' current sites — in a fixed number
    /// of queries, then answers each item from memory (issue #430 §3.2). No item is ever explicit here.
    /// </summary>
    public async Task<ResolutionSet> ResolveManyAsync(
        string userId, IReadOnlyCollection<Guid> linkedLocationIds, CancellationToken cancellationToken)
    {
        var places = linkedLocationIds.Count == 0
            ? []
            : await context.Locations.AsNoTracking()
                .Where(l => linkedLocationIds.Contains(l.LocationId) && l.WeatherPointId != null)
                .Select(l => new PlaceRow(l.LocationId, l.Name, l.Latitude, l.Longitude, l.WeatherPointId!.Value))
                .ToListAsync(cancellationToken);

        var preferred = await PreferredPointIdAsync(userId, cancellationToken);
        var household = (await settings.GetAsync(cancellationToken)).HouseholdDefaultPointId;

        var candidateIds = places.Select(p => p.WeatherPointId)
            .Concat(preferred is { } p1 ? [p1] : [])
            .Concat(household is { } p2 ? [p2] : [])
            .Distinct().ToList();
        var points = candidateIds.Count == 0
            ? new Dictionary<Guid, PointRow>()
            : await context.WeatherPoints.AsNoTracking()
                .Where(p => candidateIds.Contains(p.WeatherPointId))
                .Select(p => new PointRow(p.WeatherPointId, p.Name, p.ForecastEnabled, p.PurgeStartedAt, p.IsTracked))
                .ToDictionaryAsync(p => p.Id, cancellationToken);

        var linkedPointIds = places.Select(p => p.WeatherPointId).Distinct().ToList();
        var sites = linkedPointIds.Count == 0
            ? new Dictionary<Guid, WeatherPointSite>()
            : await (from point in context.WeatherPoints.AsNoTracking()
                     join site in context.WeatherPointSites.AsNoTracking()
                         on new { point.WeatherPointId, Version = point.CurrentSiteVersion } equals new { site.WeatherPointId, Version = site.SiteVersion }
                     where linkedPointIds.Contains(point.WeatherPointId)
                     select site).ToDictionaryAsync(s => s.WeatherPointId, cancellationToken);

        WeatherResolution? fallback = null;
        foreach (var (candidate, from) in new[] { (preferred, WeatherResolvedFrom.Preferred), (household, WeatherResolvedFrom.Household) })
        {
            if (candidate is { } id && points.TryGetValue(id, out var row) && Fixed(row, from) is { Selectable: true } resolved)
            {
                fallback = resolved;
                break;
            }
        }

        var linked = new Dictionary<Guid, WeatherResolution>();
        foreach (var place in places)
        {
            if (points.TryGetValue(place.WeatherPointId, out var row))
            {
                sites.TryGetValue(place.WeatherPointId, out var site);
                linked[place.LocationId] = Fixed(row, WeatherResolvedFrom.Linked) with
                {
                    LinkedLocation = new WeatherLinkedLocationRef { LocationId = place.LocationId, Name = place.Name },
                    DistanceKm = WeatherDistance.KmOrNull(place.Latitude, place.Longitude, site?.Latitude, site?.Longitude),
                };
            }
        }

        return new ResolutionSet(linked, fallback ?? new WeatherResolution(null, false, false));
    }

    /// <summary>The prefetched answers of <see cref="ResolveManyAsync"/>.</summary>
    public sealed class ResolutionSet
    {
        private readonly IReadOnlyDictionary<Guid, WeatherResolution> linked;
        private readonly WeatherResolution fallback;

        internal ResolutionSet(IReadOnlyDictionary<Guid, WeatherResolution> linked, WeatherResolution fallback)
        {
            this.linked = linked;
            this.fallback = fallback;
        }

        /// <summary>The resolution for an item whose place is <paramref name="linkedLocationId"/> (or none).</summary>
        public WeatherResolution For(Guid? linkedLocationId) =>
            linkedLocationId is { } id && linked.TryGetValue(id, out var resolution) ? resolution : fallback;
    }

    // ── Status and staleness ─────────────────────────────────────────────────────────────────────

    /// <summary>The status rule, pure: data wins; else a non-selectable point; else configuration decides.</summary>
    public static WeatherStatus Status(WeatherResolution resolution, bool hasData, bool isConfigured)
    {
        if (hasData)
        {
            return WeatherStatus.Ok;
        }

        if (!resolution.Selectable)
        {
            // A linked point is used as if named explicitly, with no fallback (issue #387 §8): with its forecast
            // off it simply has no forecast data.
            return resolution.From == WeatherResolvedFrom.Linked ? WeatherStatus.NoData : WeatherStatus.WeatherDisabled;
        }

        return isConfigured ? WeatherStatus.NoData : WeatherStatus.NotConfigured;
    }

    public async Task<WeatherStatus> StatusAsync(WeatherResolution resolution, bool hasData, CancellationToken cancellationToken) =>
        hasData
            ? WeatherStatus.Ok
            : Status(resolution, hasData, (await settings.GetAsync(cancellationToken)).IsConfigured);

    /// <summary>Whether the weather settings say MET Norway can be reached at all (a contact is set).</summary>
    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken) =>
        (await settings.GetAsync(cancellationToken)).IsConfigured;

    /// <summary>
    /// Stale: the last refresh failed, OR the newest forecast is older than three refresh intervals (never
    /// under 3 h). The age half covers what a recorded failure cannot: a scheduled place paused past a
    /// lowered cap, or a scheduler that is not running, would otherwise serve a frozen forecast as current.
    /// A point that is not selectable is never stale.
    /// </summary>
    public async Task<bool> StaleAsync(WeatherResolution resolution, Guid pointId, CancellationToken cancellationToken)
    {
        if (!resolution.Selectable)
        {
            return false;
        }

        var stale = await StalePointsAsync([pointId], cancellationToken);
        return stale.Contains(pointId);
    }

    /// <summary>The points of <paramref name="pointIds"/> that read as stale, in one query (issue #430 §3.3 step 6).</summary>
    public async Task<IReadOnlySet<Guid>> StalePointsAsync(IReadOnlyCollection<Guid> pointIds, CancellationToken cancellationToken)
    {
        if (pointIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var schedules = await context.WeatherPointSchedules.AsNoTracking()
            .Where(s => pointIds.Contains(s.WeatherPointId))
            .Select(s => new { s.WeatherPointId, s.ForecastFailureKind, s.LastForecastSuccessUtc })
            .ToListAsync(cancellationToken);
        if (schedules.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var interval = TimeSpan.FromMinutes((await settings.GetAsync(cancellationToken)).RefreshIntervalMinutes);
        var staleAfter = interval * 3 > StaleFloor ? interval * 3 : StaleFloor;
        var now = Now;
        return schedules
            .Where(s => s.ForecastFailureKind is not null || (s.LastForecastSuccessUtc is { } last && now - last > staleAfter))
            .Select(s => s.WeatherPointId)
            .ToHashSet();
    }

    // ── Preference ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The caller's stored point id, or null. A row that does not parse reads as no preference, never a failure;
    /// whatever it names is re-checked for selectability by every caller, so a deleted point reads as absent.
    /// </summary>
    public async Task<Guid?> PreferredPointIdAsync(string userId, CancellationToken cancellationToken)
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
            return JsonSerializer.Deserialize<StoredPreference>(json, PreferenceJson)?.DefaultPointId;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ── Shared ───────────────────────────────────────────────────────────────────────────────────

    private sealed record PointRow(Guid Id, string Name, bool ForecastEnabled, DateTime? PurgeStartedAt, bool IsTracked);

    private sealed record PlaceRow(Guid LocationId, string Name, decimal? Latitude, decimal? Longitude, Guid WeatherPointId);

    private static WeatherResolution Fixed(PointRow row, WeatherResolvedFrom from)
    {
        var selectable = row.ForecastEnabled && row.PurgeStartedAt is null;
        return new WeatherResolution(
            new WeatherPointRef { WeatherPointId = row.Id, Name = row.Name }, selectable, selectable && row.IsTracked, from);
    }
}
