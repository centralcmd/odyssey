using Odyssey.Dtos;
using Odyssey.Dtos.Journal;

namespace Odyssey.ApiClient.Resources;

/// <summary>
/// Typed client for weather (issue #348; weather points, issue #387): the reads, the point picker and the
/// caller's own point, and the Admin's weather points. Every read carries MET Norway's attribution, which the
/// caller must show. Linking a place to a point is <see cref="ILocationsApiClient.SetWeatherPointAsync"/>.
/// </summary>
/// <remarks>
/// Failures callers branch on: <c>409</c> with a <c>code</c> (<c>stale</c>, <c>duplicate-name</c>,
/// <c>location-changed</c>, <c>fetch-in-progress</c>, <c>purge-in-progress</c>, <c>still-enabled</c>,
/// <c>household-default</c>), <c>422</c> keyed <c>isTracked</c> at the tracked cap, <c>429</c> on refresh, purge
/// and delete, and <c>503</c> <c>weather-not-configured</c> on refresh. A <c>504</c> from a proxy on refresh,
/// purge or delete means the work may still be running — re-read rather than report a failure.
/// </remarks>
public interface IWeatherApiClient
{
    Task<ApiResult<WeatherCurrentResponse>> CurrentAsync(Guid? weatherPointId = null, CancellationToken ct = default);

    Task<ApiResult<WeatherDailyResponse>> DailyAsync(DateOnly from, DateOnly to, Guid? weatherPointId = null, CancellationToken ct = default);

    Task<ApiResult<WeatherHourlyResponse>> HourlyAsync(DateTime fromUtc, DateTime toUtc, Guid? weatherPointId = null, CancellationToken ct = default);

    /// <summary>
    /// The hourly read's day mode (issue #414): the rows of one POINT-LOCAL <paramref name="date"/>, with each row's
    /// start and end in the point's clock. The server owns the day's bounds, so the caller never needs the point's zone.
    /// </summary>
    Task<ApiResult<WeatherHourlyResponse>> HourlyForDayAsync(DateOnly date, Guid? weatherPointId = null, CancellationToken ct = default);

    /// <summary>
    /// The picker. Without <paramref name="nearLocationId"/> by name; with it nearest first, each entry carrying
    /// <c>distanceKm</c> (needs <c>locations.read</c>). Never cache the near variant: a move changes its distances.
    /// </summary>
    Task<ApiResult<WeatherPointOptions>> OptionsAsync(Guid? nearLocationId = null, CancellationToken ct = default);

    Task<ApiResult<WeatherPreferences>> PreferencesAsync(CancellationToken ct = default);

    Task<ApiResult> SetPreferenceAsync(Guid? defaultPointId, CancellationToken ct = default);

    /// <summary>One page of the Admin list; <paramref name="sortBy"/> is a <see cref="WeatherPointSortBy"/> name.</summary>
    Task<ApiResult<PagedResult<ExistingWeatherPoint>>> PointsAsync(
        int page,
        int pageSize,
        string? search = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default);

    Task<ApiResult<ExistingWeatherPoint>> PointAsync(Guid weatherPointId, CancellationToken ct = default);

    /// <summary>The places that use a point (id, name, whole-km distance); needs <c>locations.read</c> too.</summary>
    Task<ApiResult<List<WeatherPointLinkedLocation>>> LinkedLocationsAsync(Guid weatherPointId, CancellationToken ct = default);

    Task<ApiResult<ExistingWeatherPoint>> CreatePointAsync(NewWeatherPoint request, CancellationToken ct = default);

    Task<ApiResult<WeatherPointUpdateResult>> UpdatePointAsync(Guid weatherPointId, UpdateWeatherPoint request, CancellationToken ct = default);

    Task<ApiResult<WeatherRefreshResult>> RefreshPointAsync(Guid weatherPointId, CancellationToken ct = default);

    /// <summary>Deletes the point's measured data only; the point and its forecast stay.</summary>
    Task<ApiResult> PurgePointHistoryAsync(Guid weatherPointId, CancellationToken ct = default);

    /// <summary>Deletes the point and all its data; its places become unlinked.</summary>
    Task<ApiResult> DeletePointAsync(Guid weatherPointId, CancellationToken ct = default);

    /// <summary>One calendar event's weather — hourly steps or the days of an all-day event (issue #430 §5.1).</summary>
    Task<ApiResult<CalendarEventWeatherResponse>> CalendarEventAsync(Guid calendarEventId, Guid? weatherPointId = null, CancellationToken ct = default);

    /// <summary>One journal entry's day of weather (issue #430 §5.1).</summary>
    Task<ApiResult<JournalEntryWeatherResponse>> JournalEntryAsync(Guid journalEntryId, Guid? weatherPointId = null, CancellationToken ct = default);

    /// <summary>
    /// Weather summaries for calendar events (issue #430 §5.3). De-duplicates, then sends chunks of
    /// <see cref="WeatherRules.BatchMaxIds"/> sequentially; all or nothing — the first failing chunk's result is
    /// returned and no later chunk is sent. An empty input returns an empty success without a request.
    /// </summary>
    Task<ApiResult<CalendarEventWeatherBatch>> CalendarEventsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    /// <summary>The day's weather for journal entries (issue #430 §5.2), chunked as <see cref="CalendarEventsAsync"/>.</summary>
    Task<ApiResult<JournalEntryWeatherBatch>> JournalEntriesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
}

/// <inheritdoc cref="IWeatherApiClient" />
public sealed class WeatherApiClient(IOdysseyApi api) : IWeatherApiClient
{
    private const string Base = "api/weather";
    private const string Points = "api/weather/points";

    public Task<ApiResult<WeatherCurrentResponse>> CurrentAsync(Guid? weatherPointId = null, CancellationToken ct = default) =>
        api.GetAsync<WeatherCurrentResponse>(PagedQuery.For($"{Base}/current").Add("weatherPointId", weatherPointId?.ToString()).Build(), ct);

    public Task<ApiResult<WeatherDailyResponse>> DailyAsync(DateOnly from, DateOnly to, Guid? weatherPointId = null, CancellationToken ct = default) =>
        api.GetAsync<WeatherDailyResponse>(PagedQuery.For($"{Base}/daily")
            .Add("from", from.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))
            .Add("to", to.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))
            .Add("weatherPointId", weatherPointId?.ToString())
            .Build(), ct);

    public Task<ApiResult<WeatherHourlyResponse>> HourlyAsync(DateTime fromUtc, DateTime toUtc, Guid? weatherPointId = null, CancellationToken ct = default) =>
        api.GetAsync<WeatherHourlyResponse>(PagedQuery.For($"{Base}/hourly")
            .Add("fromUtc", fromUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture))
            .Add("toUtc", toUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture))
            .Add("weatherPointId", weatherPointId?.ToString())
            .Build(), ct);

    public Task<ApiResult<WeatherHourlyResponse>> HourlyForDayAsync(DateOnly date, Guid? weatherPointId = null, CancellationToken ct = default) =>
        api.GetAsync<WeatherHourlyResponse>(PagedQuery.For($"{Base}/hourly")
            .Add("date", date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))
            .Add("weatherPointId", weatherPointId?.ToString())
            .Build(), ct);

    public Task<ApiResult<CalendarEventWeatherResponse>> CalendarEventAsync(Guid calendarEventId, Guid? weatherPointId = null, CancellationToken ct = default) =>
        api.GetAsync<CalendarEventWeatherResponse>(PagedQuery.For($"api/calendar-events/{calendarEventId}/weather")
            .Add("weatherPointId", weatherPointId?.ToString()).Build(), ct);

    public Task<ApiResult<JournalEntryWeatherResponse>> JournalEntryAsync(Guid journalEntryId, Guid? weatherPointId = null, CancellationToken ct = default) =>
        api.GetAsync<JournalEntryWeatherResponse>(PagedQuery.For($"api/journal-entries/{journalEntryId}/weather")
            .Add("weatherPointId", weatherPointId?.ToString()).Build(), ct);

    public async Task<ApiResult<CalendarEventWeatherBatch>> CalendarEventsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        var items = new List<CalendarEventWeatherItem>();
        CalendarEventWeatherBatch? last = null;
        foreach (var chunk in Chunks(ids))
        {
            var result = await api.GetAsync<CalendarEventWeatherBatch>(BatchUrl("api/calendar-events/weather", chunk), ct);
            if (!result.IsSuccess || result.Value is null)
            {
                return result;
            }

            items.AddRange(result.Value.Items);
            last = result.Value;
        }

        return ApiResult<CalendarEventWeatherBatch>.Success(
            (last ?? new CalendarEventWeatherBatch()) with { Items = items }, System.Net.HttpStatusCode.OK);
    }

    public async Task<ApiResult<JournalEntryWeatherBatch>> JournalEntriesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        var items = new List<JournalEntryWeatherItem>();
        JournalEntryWeatherBatch? last = null;
        foreach (var chunk in Chunks(ids))
        {
            var result = await api.GetAsync<JournalEntryWeatherBatch>(BatchUrl("api/journal-entries/weather", chunk), ct);
            if (!result.IsSuccess || result.Value is null)
            {
                return result;
            }

            items.AddRange(result.Value.Items);
            last = result.Value;
        }

        return ApiResult<JournalEntryWeatherBatch>.Success(
            (last ?? new JournalEntryWeatherBatch()) with { Items = items }, System.Net.HttpStatusCode.OK);
    }

    /// <summary>De-duplicated (first seen wins), then split into requests of at most <see cref="WeatherRules.BatchMaxIds"/>.</summary>
    internal static IEnumerable<IReadOnlyList<Guid>> Chunks(IReadOnlyCollection<Guid> ids) =>
        ids.Distinct().Chunk(WeatherRules.BatchMaxIds);

    /// <summary>Repeated <c>ids</c> keys in the <c>D</c> format, the only spelling the server accepts.</summary>
    private static string BatchUrl(string path, IReadOnlyList<Guid> ids) =>
        $"{path}?{string.Join("&", ids.Select(id => $"ids={id:D}"))}";

    public Task<ApiResult<WeatherPointOptions>> OptionsAsync(Guid? nearLocationId = null, CancellationToken ct = default) =>
        api.GetAsync<WeatherPointOptions>(
            nearLocationId is { } near ? $"{Points}/options?nearLocationId={near}" : $"{Points}/options", ct);

    public Task<ApiResult<WeatherPreferences>> PreferencesAsync(CancellationToken ct = default) =>
        api.GetAsync<WeatherPreferences>($"{Base}/preferences", ct);

    public Task<ApiResult> SetPreferenceAsync(Guid? defaultPointId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Base}/preferences", new UpdateWeatherPreferences { DefaultPointId = defaultPointId }, ct);

    public Task<ApiResult<PagedResult<ExistingWeatherPoint>>> PointsAsync(
        int page,
        int pageSize,
        string? search = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default) =>
        api.GetPagedAsync<ExistingWeatherPoint>(PagedQuery.For(Points)
            .Add("search", search)
            .Add("sortBy", sortBy)
            .Add("sortDir", sortDir)
            .Window(page, pageSize)
            .Build(), ct);

    public Task<ApiResult<ExistingWeatherPoint>> PointAsync(Guid weatherPointId, CancellationToken ct = default) =>
        api.GetAsync<ExistingWeatherPoint>($"{Points}/{weatherPointId}", ct);

    public Task<ApiResult<List<WeatherPointLinkedLocation>>> LinkedLocationsAsync(Guid weatherPointId, CancellationToken ct = default) =>
        api.GetAsync<List<WeatherPointLinkedLocation>>($"{Points}/{weatherPointId}/linked-locations", ct);

    public Task<ApiResult<ExistingWeatherPoint>> CreatePointAsync(NewWeatherPoint request, CancellationToken ct = default) =>
        api.SendAsync<ExistingWeatherPoint>(HttpMethod.Post, Points, request, ct);

    public Task<ApiResult<WeatherPointUpdateResult>> UpdatePointAsync(Guid weatherPointId, UpdateWeatherPoint request, CancellationToken ct = default) =>
        api.SendAsync<WeatherPointUpdateResult>(HttpMethod.Put, $"{Points}/{weatherPointId}", request, ct);

    public Task<ApiResult<WeatherRefreshResult>> RefreshPointAsync(Guid weatherPointId, CancellationToken ct = default) =>
        api.SendAsync<WeatherRefreshResult>(HttpMethod.Post, $"{Points}/{weatherPointId}/refresh", null, ct);

    public Task<ApiResult> PurgePointHistoryAsync(Guid weatherPointId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Points}/{weatherPointId}/history", null, ct);

    public Task<ApiResult> DeletePointAsync(Guid weatherPointId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Points}/{weatherPointId}", null, ct);
}
