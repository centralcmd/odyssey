using System.Collections.Concurrent;
using System.Net;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Application;
using Odyssey.Client.Services;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Covers <see cref="PageStateService"/> — the per-page UI-state store behind every list page's
/// remembered filters, search and open sections.
/// </summary>
/// <remarks>
/// Two things here are timing-sensitive and unverifiable by reading the code. The debounce must
/// <b>coalesce</b> — search-as-you-type calls <c>QueueSave</c> on every keystroke, and a lost
/// cancellation would turn a ten-character search into ten PUTs — and it must coalesce
/// <b>per page key</b>, since a single scoped service serves every page the session visits. The
/// load path's forgiveness matters just as much: a stale blob written by an older header layout has
/// to resolve to <c>null</c> so the page falls back to its defaults, never throw out of
/// <c>OnInitializedAsync</c>.
/// <para>
/// The service is constructed through its internal test seam, which supplies the browser check and a
/// short debounce window in place of the real 500 ms.
/// </para>
/// </remarks>
public class PageStateServiceTests
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(60);

    private sealed record PageState
    {
        public string Search { get; init; } = "";
        public int PageSize { get; init; }
    }

    /// <summary>Records every write and serves a canned read.</summary>
    private sealed class FakePreferences : IUserPreferencesApiClient
    {
        private readonly ConcurrentQueue<(string Key, string Json)> writes = new();

        public HttpStatusCode GetStatus { get; set; } = HttpStatusCode.OK;
        public string? StoredJson { get; set; }

        public IReadOnlyList<(string Key, string Json)> Writes => [.. writes];

        public Task<ApiResult<TValue>> GetAsync<TValue>(string key, CancellationToken ct = default)
        {
            if (GetStatus != HttpStatusCode.OK)
                return Task.FromResult(ApiResult<TValue>.Failure(GetStatus, new ApiProblem { Status = (int)GetStatus }));

            var payload = (TValue?)(object?)new UserPreferenceResponse(key, StoredJson ?? "", DateTime.UtcNow);
            return Task.FromResult(ApiResult<TValue>.Success(payload, HttpStatusCode.OK));
        }

        public Task<ApiResult> PutAsync(string key, object value, CancellationToken ct = default)
        {
            writes.Enqueue((key, ((UserPreferenceRequest)value).PreferencesJson));
            return Task.FromResult(ApiResult.Success(HttpStatusCode.NoContent));
        }

        /// <summary>Waits until <paramref name="count"/> writes have landed, or gives up.</summary>
        public async Task<bool> WaitForWritesAsync(int count)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (writes.Count >= count)
                    return true;
                await Task.Delay(10);
            }

            return false;
        }
    }

    private static PageStateService Create(FakePreferences prefs, bool enabled = true) =>
        new(prefs, enabled, Debounce);

    /// <summary>Long enough for any straggling debounce timer to have fired.</summary>
    private static Task SettleAsync() => Task.Delay(Debounce * 6);

    // ── QueueSave: debounce and coalescing ───────────────────────────────────

    [Fact]
    public async Task A_queued_save_is_written_after_the_debounce_window()
    {
        var prefs = new FakePreferences();
        var service = Create(prefs);

        service.QueueSave("accounts-page", new PageState { Search = "oslo" });

        Assert.True(await prefs.WaitForWritesAsync(1), "the queued save never reached the store");
        var (key, json) = prefs.Writes[0];
        Assert.Equal("accounts-page", key);
        Assert.Contains("\"search\":\"oslo\"", json);
    }

    /// <summary>Nothing is written before the window elapses — otherwise the debounce buys nothing.</summary>
    [Fact]
    public async Task A_queued_save_is_not_written_immediately()
    {
        var prefs = new FakePreferences();
        var service = Create(prefs);

        service.QueueSave("accounts-page", new PageState { Search = "o" });

        Assert.Empty(prefs.Writes);
        Assert.True(await prefs.WaitForWritesAsync(1));
    }

    /// <summary>
    /// The defect this guards: a burst of keystrokes must produce one PUT carrying the final state,
    /// not one per keystroke.
    /// </summary>
    [Fact]
    public async Task Rapid_saves_to_one_page_coalesce_into_a_single_write_of_the_last_state()
    {
        var prefs = new FakePreferences();
        var service = Create(prefs);

        foreach (var term in new[] { "o", "os", "osl", "oslo" })
            service.QueueSave("accounts-page", new PageState { Search = term });

        Assert.True(await prefs.WaitForWritesAsync(1));
        await SettleAsync();

        Assert.Single(prefs.Writes);
        Assert.Contains("\"search\":\"oslo\"", prefs.Writes[0].Json);
    }

    /// <summary>
    /// One scoped service serves every page in the session, so the pending-save map is keyed by page.
    /// Coalescing across keys would drop one page's state whenever another page saved.
    /// </summary>
    [Fact]
    public async Task Saves_to_different_pages_do_not_cancel_each_other()
    {
        var prefs = new FakePreferences();
        var service = Create(prefs);

        service.QueueSave("accounts-page", new PageState { Search = "accounts" });
        service.QueueSave("transactions-page", new PageState { Search = "transactions" });

        Assert.True(await prefs.WaitForWritesAsync(2));
        await SettleAsync();

        Assert.Equal(
            ["accounts-page", "transactions-page"],
            prefs.Writes.Select(w => w.Key).Order());
    }

    /// <summary>Once a window has elapsed the next change starts a fresh one — the debounce
    /// coalesces a burst, it does not collapse a page's whole session into one write.</summary>
    [Fact]
    public async Task A_save_queued_after_the_window_elapsed_is_written_separately()
    {
        var prefs = new FakePreferences();
        var service = Create(prefs);

        service.QueueSave("accounts-page", new PageState { Search = "first" });
        Assert.True(await prefs.WaitForWritesAsync(1));

        service.QueueSave("accounts-page", new PageState { Search = "second" });
        Assert.True(await prefs.WaitForWritesAsync(2));

        Assert.Equal(["first", "second"], prefs.Writes.Select(w => w.Json.Contains("first") ? "first" : "second"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_page_key_is_never_written(string pageKey)
    {
        var prefs = new FakePreferences();
        var service = Create(prefs);

        service.QueueSave(pageKey, new PageState { Search = "x" });
        await SettleAsync();

        Assert.Empty(prefs.Writes);
    }

    /// <summary>Page state is a browser-session concern; outside the browser the service is inert
    /// so prerendering never writes a half-initialised layout over the user's saved one.</summary>
    [Fact]
    public async Task Nothing_is_persisted_outside_the_browser()
    {
        var prefs = new FakePreferences { StoredJson = """{"search":"oslo"}""" };
        var service = Create(prefs, enabled: false);

        service.QueueSave("accounts-page", new PageState { Search = "x" });
        await SettleAsync();

        Assert.Empty(prefs.Writes);
        Assert.Null(await service.LoadAsync<PageState>("accounts-page"));
    }

    // ── LoadAsync: every failure resolves to the page's defaults ─────────────

    [Fact]
    public async Task A_saved_state_round_trips()
    {
        var prefs = new FakePreferences { StoredJson = """{"search":"oslo","pageSize":50}""" };
        var service = Create(prefs);

        var state = await service.LoadAsync<PageState>("accounts-page");

        Assert.NotNull(state);
        Assert.Equal("oslo", state.Search);
        Assert.Equal(50, state.PageSize);
    }

    /// <summary>
    /// The single behaviour every caller depends on: a page whose state cannot be read renders its
    /// defaults. A 404 is a fresh user, a 403 is a missing permission, and a malformed blob is a
    /// layout that has since changed — none of them may surface as an exception out of
    /// <c>OnInitializedAsync</c>, which would blank the page.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound, null)]
    [InlineData(HttpStatusCode.Forbidden, null)]
    [InlineData(HttpStatusCode.InternalServerError, null)]
    [InlineData(HttpStatusCode.OK, "")]
    [InlineData(HttpStatusCode.OK, "   ")]
    [InlineData(HttpStatusCode.OK, "not json at all")]
    [InlineData(HttpStatusCode.OK, "{\"search\": ")]
    [InlineData(HttpStatusCode.OK, "null")]
    public async Task An_unreadable_preference_resolves_to_the_page_defaults(HttpStatusCode status, string? stored)
    {
        var prefs = new FakePreferences { GetStatus = status, StoredJson = stored };
        var service = Create(prefs);

        Assert.Null(await service.LoadAsync<PageState>("accounts-page"));
    }

    /// <summary>A blob from an older layout keeps whatever still binds and defaults the rest —
    /// unknown members are ignored rather than treated as corruption.</summary>
    [Fact]
    public async Task A_state_from_an_older_layout_keeps_what_still_binds()
    {
        var prefs = new FakePreferences { StoredJson = """{"search":"oslo","retiredSection":true}""" };
        var service = Create(prefs);

        var state = await service.LoadAsync<PageState>("accounts-page");

        Assert.NotNull(state);
        Assert.Equal("oslo", state.Search);
        Assert.Equal(0, state.PageSize);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_page_key_never_hits_the_store(string pageKey)
    {
        var prefs = new FakePreferences { GetStatus = HttpStatusCode.InternalServerError };
        var service = Create(prefs);

        Assert.Null(await service.LoadAsync<PageState>(pageKey));
    }

    /// <summary>
    /// <c>QueueSave</c> writes with web (camelCase) JSON options and <c>LoadAsync</c> reads with the
    /// same, so what one writes the other must read back. A mismatch binds nothing and silently
    /// resets every page to its defaults on the next visit — the failure mode
    /// <see cref="UserPreferenceServiceTests"/> documents for the theme preference.
    /// </summary>
    [Fact]
    public async Task What_QueueSave_writes_is_what_LoadAsync_reads()
    {
        var prefs = new FakePreferences();
        var service = Create(prefs);

        service.QueueSave("accounts-page", new PageState { Search = "oslo", PageSize = 50 });
        Assert.True(await prefs.WaitForWritesAsync(1));

        prefs.StoredJson = prefs.Writes[0].Json;
        var reloaded = await service.LoadAsync<PageState>("accounts-page");

        Assert.Equal(new PageState { Search = "oslo", PageSize = 50 }, reloaded);
    }

    /// <summary>
    /// The debounce map tracked pending work but never dropped it: an entry (and its
    /// <see cref="CancellationTokenSource"/>) was added per page key and kept for the lifetime of the
    /// scope, so it grew monotonically with the number of pages visited and disposed only the sources
    /// a later save superseded (issue #370). Small in absolute terms, but it is an unbounded map keyed
    /// by something the user drives, which is the shape of a leak rather than a fixed cost.
    /// </summary>
    [Fact]
    public async Task A_settled_save_stops_being_tracked()
    {
        var prefs = new FakePreferences();
        var service = Create(prefs);

        foreach (var key in new[] { "accounts-page", "budgets-page", "users-page" })
            service.QueueSave(key, new PageState { Search = key });

        Assert.True(await prefs.WaitForWritesAsync(3));

        // The PUT is awaited inside the debounce task, so the last one can still be retiring when
        // its write lands; give the continuations a moment rather than racing them.
        for (var attempt = 0; attempt < 50 && service.PendingSaveCount > 0; attempt++)
            await Task.Delay(20);

        Assert.Equal(0, service.PendingSaveCount);
    }

    /// <summary>
    /// The superseding path cancels and disposes the timer it replaces, and the settling path drops
    /// only a timer still registered to it — so a burst on one key must neither double-dispose (which
    /// would throw out of <c>Cancel</c>) nor leave the key registered once it settles.
    /// </summary>
    [Fact]
    public async Task A_burst_on_one_key_settles_to_a_single_write_and_no_tracked_timer()
    {
        var prefs = new FakePreferences();
        var service = Create(prefs);

        for (var i = 0; i < 12; i++)
            service.QueueSave("accounts-page", new PageState { Search = new string('o', i + 1) });

        Assert.True(await prefs.WaitForWritesAsync(1));

        for (var attempt = 0; attempt < 50 && service.PendingSaveCount > 0; attempt++)
            await Task.Delay(20);

        Assert.Equal(0, service.PendingSaveCount);
        Assert.Single(prefs.Writes);
    }
}
