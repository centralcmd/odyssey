using System.Net;
using Microsoft.Extensions.Logging;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Weather;

/// <summary>One batch answer: its items, and MET's attribution exactly as the response carried it.</summary>
internal sealed record WeatherBatchPage<TItem>(IReadOnlyList<TItem> Items, WeatherAttribution? Attribution);

/// <summary>
/// The weather a list page shows beside its records (issue #363 §3.6, §3.7, §7): which ids are on screen, which
/// of them are already answered, and one debounced, cancellable batch request for the rest.
/// <para>
/// Five rules live here so neither page restates them. An id already answered, or one the server omitted (deleted
/// or unknown), is never requested again. A newer window cancels the request in flight, so a slow old answer cannot
/// land last. A <c>429</c> retries once after <see cref="WeatherRetry.CoolDown"/>. A <c>403</c> clears everything and
/// stops the page asking. And no failure is shown: a list is not where one record's missing weather is explained, so
/// a failure is one <see cref="ILogger"/> warning naming the surface, the failure class and the count — never an
/// id, a name or a response body. Nothing is stored outside this object.
/// </para>
/// </summary>
internal sealed class WeatherBatchLoader<TItem> : IDisposable where TItem : class
{
    private readonly Func<IReadOnlyCollection<Guid>, CancellationToken, Task<ApiResult<WeatherBatchPage<TItem>>>> fetch;
    private readonly Func<TItem, Guid> idOf;
    private readonly Func<Task> changed;
    private readonly ILogger log;
    private readonly string surface;
    private readonly TimeSpan debounce;
    private readonly Dictionary<Guid, TItem> items = [];
    private readonly HashSet<Guid> omitted = [];
    private readonly HashSet<Guid> refreshing = [];
    private List<Guid> wanted = [];
    private CancellationTokenSource? pending;
    private bool disposed;

    public WeatherBatchLoader(
        Func<IReadOnlyCollection<Guid>, CancellationToken, Task<ApiResult<WeatherBatchPage<TItem>>>> fetch,
        Func<TItem, Guid> idOf,
        Func<Task> changed,
        ILogger log,
        string surface,
        TimeSpan debounce)
    {
        this.fetch = fetch;
        this.idOf = idOf;
        this.changed = changed;
        this.log = log;
        this.surface = surface;
        this.debounce = debounce;
    }

    /// <summary>The caller holds <c>weather.read</c>. Without it nothing is ever requested.</summary>
    public bool Enabled { get; private set; }

    /// <summary>A <c>403</c> arrived: everything was cleared and nothing more is requested until <see cref="Reset"/>.</summary>
    public bool Stopped { get; private set; }

    /// <summary>MET's attribution from the latest answer.</summary>
    public WeatherAttribution? Attribution { get; private set; }

    public TItem? Get(Guid id) => Enabled && !Stopped ? items.GetValueOrDefault(id) : null;

    /// <summary>Starts (or restarts, after an authentication change) from nothing.</summary>
    public void Reset(bool canRead)
    {
        Cancel();
        items.Clear();
        omitted.Clear();
        refreshing.Clear();
        wanted = [];
        Attribution = null;
        Stopped = false;
        Enabled = canRead;
    }

    /// <summary>The ids now on screen. After the debounce, the ones not yet answered are requested.</summary>
    public Task ShowAsync(IEnumerable<Guid> ids)
    {
        wanted = [.. ids.Distinct()];
        return ScheduleAsync();
    }

    /// <summary>
    /// A record was saved: ask for it again (with whatever else is missing). Its old answer stays on screen until
    /// the new one lands, so the row does not blink out and back.
    /// </summary>
    public Task RefreshAsync(Guid id)
    {
        omitted.Remove(id);
        refreshing.Add(id);
        if (!wanted.Contains(id))
            wanted.Add(id);
        return ScheduleAsync();
    }

    /// <summary>A record was deleted, or is about to be re-read: drop what is known about it.</summary>
    public void Forget(Guid id)
    {
        items.Remove(id);
        omitted.Remove(id);
        refreshing.Remove(id);
    }

    private async Task ScheduleAsync()
    {
        if (!Enabled || Stopped || disposed)
            return;

        Cancel();
        var source = pending = new CancellationTokenSource();
        try
        {
            await Task.Delay(debounce, source.Token);
            await LoadAsync(source.Token, retried: false);
        }
        catch (OperationCanceledException)
        {
            // A newer window, a reset or the page leaving: nothing to report.
        }
        catch (Exception ex)
        {
            log.LogWarning("Weather for {Surface} failed: {FailureClass}.", surface, ex.GetType().Name);
        }
    }

    private async Task LoadAsync(CancellationToken token, bool retried)
    {
        var missing = wanted.Where(id => (!items.ContainsKey(id) || refreshing.Contains(id)) && !omitted.Contains(id)).ToList();
        if (missing.Count == 0)
            return;

        var result = await fetch(missing, token);
        token.ThrowIfCancellationRequested();

        if (result.IsSuccess && result.Value is { } page)
        {
            var answered = page.Items.Select(idOf).ToHashSet();
            foreach (var item in page.Items)
                items[idOf(item)] = item;
            foreach (var id in missing.Where(id => !answered.Contains(id)))
            {
                items.Remove(id);
                omitted.Add(id);
            }

            refreshing.ExceptWith(missing);
            Attribution = page.Attribution ?? Attribution;
            await changed();
            return;
        }

        if (result.Status == HttpStatusCode.Forbidden)
        {
            Warn("forbidden", missing.Count);
            Stopped = true;
            items.Clear();
            omitted.Clear();
            refreshing.Clear();
            await changed();
            return;
        }

        if (result.Status == HttpStatusCode.TooManyRequests && !retried)
        {
            Warn("rate-limited", missing.Count);
            await Task.Delay(WeatherRetry.CoolDown, token);
            await LoadAsync(token, retried: true);
            return;
        }

        Warn(result.Status switch
        {
            HttpStatusCode.TooManyRequests => "rate-limited",
            HttpStatusCode.BadRequest => "invalid",
            0 => "network",
            _ => "error",
        }, missing.Count);
    }

    private void Warn(string failureClass, int count) =>
        log.LogWarning("Weather for {Surface} failed: {FailureClass}, {Count} items.", surface, failureClass, count);

    private void Cancel()
    {
        pending?.Cancel();
        pending?.Dispose();
        pending = null;
    }

    public void Dispose()
    {
        disposed = true;
        Cancel();
    }
}

/// <summary>The two list reads, shaped for <see cref="WeatherBatchLoader{TItem}"/>.</summary>
internal static class WeatherBatchReads
{
    public static Func<IReadOnlyCollection<Guid>, CancellationToken, Task<ApiResult<WeatherBatchPage<JournalEntryWeatherItem>>>> JournalEntries(
        IWeatherApiClient weather) =>
        async (ids, ct) => Page(await weather.JournalEntriesAsync(ids, ct), b => new WeatherBatchPage<JournalEntryWeatherItem>(b.Items, b.Attribution));

    public static Func<IReadOnlyCollection<Guid>, CancellationToken, Task<ApiResult<WeatherBatchPage<CalendarEventWeatherItem>>>> CalendarEvents(
        IWeatherApiClient weather) =>
        async (ids, ct) => Page(await weather.CalendarEventsAsync(ids, ct), b => new WeatherBatchPage<CalendarEventWeatherItem>(b.Items, b.Attribution));

    private static ApiResult<WeatherBatchPage<TItem>> Page<TBatch, TItem>(
        ApiResult<TBatch> result, Func<TBatch, WeatherBatchPage<TItem>> map) where TBatch : class =>
        result is { IsSuccess: true, Value: { } batch }
            ? ApiResult<WeatherBatchPage<TItem>>.Success(map(batch), result.Status)
            : result.CastFailure<WeatherBatchPage<TItem>>();
}
