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
public sealed class WeatherPointReads(IWeatherApiClient weather)
{
    private readonly Dictionary<Guid, Task<ApiResult<WeatherCurrentResponse>>> current = [];
    private readonly Dictionary<Guid, Task<ApiResult<WeatherDailyResponse>>> daily = [];

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

    public void Forget(Guid weatherPointId)
    {
        current.Remove(weatherPointId);
        daily.Remove(weatherPointId);
    }

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
