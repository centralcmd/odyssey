using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Weather;

/// <summary>
/// One page's forecast reads, de-duplicated per <c>weatherPointId</c> (issue #388 §5, §10): two open records linked
/// to one point share one <c>current</c> and one <c>daily</c> request, and an open record reuses its answer while it
/// stays open. A failure is never kept, so <b>Try again</b> asks again; <see cref="Forget"/> drops a point's answers
/// when no open record needs them any more.
/// </summary>
public sealed class WeatherPointReads(IWeatherApiClient weather, TimeProvider? time = null)
{
    private readonly Dictionary<Guid, Task<ApiResult<WeatherCurrentResponse>>> current = [];
    private readonly Dictionary<Guid, Task<ApiResult<WeatherDailyResponse>>> daily = [];
    private readonly Dictionary<(Guid Point, DateOnly Date), HourlySlot> hourly = [];
    private readonly TimeProvider clock = time ?? TimeProvider.System;

    /// <summary>How long today's hours are held: "now" moves on, so the first row a cached answer shows goes stale.</summary>
    internal static readonly TimeSpan TodayLifetime = TimeSpan.FromMinutes(10);

    /// <summary>The longest any day's hours are held, so a new MET run is picked up.</summary>
    internal static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(60);

    private sealed class HourlySlot(Task<ApiResult<WeatherHourlyResponse>> task)
    {
        public Task<ApiResult<WeatherHourlyResponse>> Task { get; } = task;

        /// <summary>Set when the answer lands and is worth keeping; until then the slot only shares the request in flight.</summary>
        public DateTimeOffset? ExpiresAt { get; set; }
    }

    /// <summary>The viewer's today, as a seam for bUnit.</summary>
    internal static Func<DateOnly> Today = static () => DateOnly.FromDateTime(DateTime.Now);

    public Task<ApiResult<WeatherCurrentResponse>> CurrentAsync(Guid weatherPointId) =>
        Shared(current, weatherPointId, () => weather.CurrentAsync(weatherPointId));

    /// <summary>
    /// Yesterday for the measured line, then the days shown — the dashboard card's window. One day of slack each side,
    /// because the point's date may differ from the viewer's by one.
    /// </summary>
    public Task<ApiResult<WeatherDailyResponse>> DailyAsync(Guid weatherPointId)
    {
        var today = Today();
        return Shared(daily, weatherPointId,
            () => weather.DailyAsync(today.AddDays(-2), today.AddDays(WeatherForecastBody.DaysShown), weatherPointId));
    }

    /// <summary>
    /// The hours of one point-local <paramref name="date"/> (issue #415 §4). One request per (point, date) is shared
    /// while in flight. Only an <c>Ok</c>, not-stale answer that names the requested point and date is kept, and only
    /// until the earliest of: ten minutes for today (and no later than the end of its "Now" row), the start of the day for a future day (so "tomorrow" fetched
    /// before midnight never becomes today's panel after it), and an hour for anything.
    /// </summary>
    public async Task<ApiResult<WeatherHourlyResponse>> HourlyForDayAsync(Guid weatherPointId, DateOnly date)
    {
        var key = (weatherPointId, date);
        var now = clock.GetUtcNow();
        if (hourly.TryGetValue(key, out var held) && held.ExpiresAt is { } expires && expires <= now)
        {
            hourly.Remove(key);
            held = null;
        }

        if (held is null)
        {
            held = new HourlySlot(weather.HourlyForDayAsync(date, weatherPointId));
            hourly[key] = held;
        }

        var slot = held;
        var result = await slot.Task;
        // An Ok answer with no rows ("no hourly forecast yet") is not held: the panel offers no Try again there, so a held
        // one would hide the data for as long as it lived once MET answered.
        var keep = result.IsSuccess && result.Value is { Status: WeatherStatus.Ok, Stale: false, Day: { } day, Steps.Count: > 0 } value
            && value.Point?.WeatherPointId == weatherPointId && day.Date == date;
        if (!keep)
        {
            if (hourly.TryGetValue(key, out var current) && ReferenceEquals(current, slot))
                hourly.Remove(key);
            return result;
        }

        if (slot.ExpiresAt is null)
        {
            var arrived = clock.GetUtcNow();
            var expiry = arrived + MaxLifetime;
            if (result.Value!.Day!.IsToday)
            {
                // Today's first row is labelled "Now": once it ends, the held answer would name a past hour as now.
                var first = result.Value.Steps[0];
                var firstEnds = new DateTimeOffset(DateTime.SpecifyKind(first.TimeUtc, DateTimeKind.Utc).AddHours(first.PeriodHours));
                expiry = Min(expiry, Min(arrived + TodayLifetime, firstEnds));
            }
            else if (result.Value.Day.FromUtc is { } dayStart)
                expiry = Min(expiry, new DateTimeOffset(DateTime.SpecifyKind(dayStart, DateTimeKind.Utc)));
            slot.ExpiresAt = expiry;
        }

        return result;
    }

    /// <summary>Drops one day's hours, for Try again; the point's other days and its current/daily reads stay.</summary>
    public void ForgetDay(Guid weatherPointId, DateOnly date) => hourly.Remove((weatherPointId, date));

    public void Forget(Guid weatherPointId)
    {
        current.Remove(weatherPointId);
        daily.Remove(weatherPointId);
        foreach (var key in hourly.Keys.Where(k => k.Point == weatherPointId).ToList())
            hourly.Remove(key);
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;

    private static async Task<ApiResult<T>> Shared<T>(
        Dictionary<Guid, Task<ApiResult<T>>> slots, Guid id, Func<Task<ApiResult<T>>> fetch)
    {
        if (!slots.TryGetValue(id, out var task))
        {
            task = fetch();
            slots[id] = task;
        }

        var result = await task;
        if (!result.IsSuccess && slots.TryGetValue(id, out var held) && ReferenceEquals(held, task))
            slots.Remove(id);
        return result;
    }
}
