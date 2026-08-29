using System.Text.Json;
using Odyssey.Dtos.Application;
using Odyssey.ApiClient.Resources;

namespace Odyssey.Client.Services;

/// <summary>
/// Persists a page's UI state (open sections, filters, search) under the user's
/// preferences, one JSON blob per page key. Loads are forgiving: a missing,
/// forbidden, or unparseable preference resolves to <c>null</c> so the page falls
/// back to its defaults — a stale blob from an older header layout never breaks
/// the page (System.Text.Json ignores unknown members and defaults missing ones;
/// anything it still can't read is discarded). Saves are debounced and best-effort.
/// </summary>
public interface IPageStateService
{
    /// <summary>Loads the persisted state for <paramref name="pageKey"/>, or <c>null</c>
    /// when none exists or it can't be read/parsed (the caller should use its defaults).</summary>
    Task<T?> LoadAsync<T>(string pageKey) where T : class;

    /// <summary>Queues a debounced save of the page state, coalescing rapid changes
    /// (e.g. search-as-you-type). Fire-and-forget; failures are swallowed.</summary>
    void QueueSave<T>(string pageKey, T state) where T : class;
}

public sealed class PageStateService : IPageStateService
{
    /// <summary>The coalescing window for <see cref="QueueSave"/> — long enough that
    /// search-as-you-type produces one write rather than one per keystroke.</summary>
    internal static readonly TimeSpan DefaultDebounceDelay = TimeSpan.FromMilliseconds(500);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The in-flight debounce timer per page key, and only those: an entry is added when a save is
    /// queued and removed once that save settles, so the dictionary tracks pending work rather than
    /// accumulating one permanent entry (and one undisposed <see cref="CancellationTokenSource"/>)
    /// per page the user has visited for the lifetime of the scope.
    /// </summary>
    private readonly Dictionary<string, CancellationTokenSource> debounce = new();
    private readonly object gate = new();
    private readonly IUserPreferencesApiClient preferences;
    private readonly bool enabled;
    private readonly TimeSpan debounceDelay;

    public PageStateService(IUserPreferencesApiClient preferences)
        : this(preferences, OperatingSystem.IsBrowser(), DefaultDebounceDelay)
    {
    }

    /// <summary>
    /// Test seam. <paramref name="enabled"/> stands in for the browser check (page state is a
    /// browser-session concern; prerendering must not write it), and <paramref name="debounceDelay"/>
    /// lets a test exercise the coalescing without waiting half a second per save.
    /// </summary>
    internal PageStateService(IUserPreferencesApiClient preferences, bool enabled, TimeSpan debounceDelay)
    {
        this.preferences = preferences;
        this.enabled = enabled;
        this.debounceDelay = debounceDelay;
    }

    /// <summary>
    /// Test seam: how many debounce timers are currently registered. Should fall back to zero once
    /// queued saves settle — a number that only ever grows means the dictionary is leaking.
    /// </summary>
    internal int PendingSaveCount
    {
        get { lock (gate) return debounce.Count; }
    }

    public async Task<T?> LoadAsync<T>(string pageKey) where T : class
    {
        if (!enabled || string.IsNullOrWhiteSpace(pageKey))
            return null;

        try
        {
            // 404 (no preference yet), 403 (no permission), anything non-2xx → use defaults.
            var result = await preferences.GetAsync<UserPreferenceResponse>(pageKey);
            if (!result.IsSuccess)
                return null;

            var payload = result.Value;
            if (string.IsNullOrWhiteSpace(payload?.PreferencesJson))
                return null;

            return JsonSerializer.Deserialize<T>(payload.PreferencesJson, JsonOptions);
        }
        catch
        {
            // Never let a bad/incompatible preference break the page — discard it and
            // let the caller render (and re-save) its defaults.
            return null;
        }
    }

    public void QueueSave<T>(string pageKey, T state) where T : class
    {
        if (!enabled || string.IsNullOrWhiteSpace(pageKey))
            return;

        string json;
        try { json = JsonSerializer.Serialize(state, JsonOptions); }
        catch { return; }
        if (string.IsNullOrWhiteSpace(json))
            return;

        CancellationTokenSource cts;
        lock (gate)
        {
            if (debounce.TryGetValue(pageKey, out var pending))
            {
                pending.Cancel();
                pending.Dispose();
            }
            cts = new CancellationTokenSource();
            debounce[pageKey] = cts;
        }

        _ = PutAfterDelayAsync(pageKey, json, cts);
    }

    private async Task PutAfterDelayAsync(string pageKey, string json, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(debounceDelay, cts.Token);

            // Best-effort: a non-success here just means this page's layout isn't
            // remembered this time; never surface it to the user.
            await preferences.PutAsync(pageKey, new UserPreferenceRequest(json), cts.Token);
        }
        catch
        {
            // Superseded by a newer change (cancelled) or a transient failure — ignore.
        }
        finally
        {
            Retire(pageKey, cts);
        }
    }

    /// <summary>
    /// Drops a settled timer, but only while it is still the registered one for its page: if a newer
    /// QueueSave has already replaced it, that call cancelled and disposed this source and owns the
    /// entry, so touching either here would double-dispose. Disposing under the lock is what keeps
    /// <see cref="QueueSave"/>'s <c>Cancel</c> from ever reaching a disposed source — an entry
    /// present in the dictionary has not been disposed.
    /// </summary>
    private void Retire(string pageKey, CancellationTokenSource cts)
    {
        lock (gate)
        {
            if (!debounce.TryGetValue(pageKey, out var current) || !ReferenceEquals(current, cts))
                return;

            debounce.Remove(pageKey);
            cts.Dispose();
        }
    }
}
