using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <see cref="OdsInfiniteList{TItem}"/>'s two opt-in modes for the news reader (issue #473 §4.2, §4.4):
/// the cursor-driven <b>remote</b> mode of the article list (AC 33, 45, 46, 48, 50) and the
/// <b>footer-only</b> mode that follows the News tags table (AC 64). The in-memory card mode is pinned
/// by the existing page tests, which run unchanged.
/// </summary>
public class OdsInfiniteListRemoteTests
{
    private const string ScrollModule = "./js/infinite-scroll.js";

    private static BunitContext NewContext(out BunitJSModuleInterop module)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        module = ctx.JSInterop.SetupModule(ScrollModule);
        module.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    private static IReadOnlyList<string> ItemsOf(int count, int from = 1) =>
        [.. Enumerable.Range(from, count).Select(i => $"Article {i}")];

    private static RenderFragment<string> Card => item => b =>
    {
        b.OpenElement(0, "div");
        b.AddAttribute(1, "class", "card");
        b.AddContent(2, item);
        b.CloseElement();
    };

    private static IRenderedComponent<OdsInfiniteList<string>> RenderRemote(
        BunitContext ctx, IReadOnlyList<string> items, List<bool> calls,
        int? total = 1234, bool capped = false, bool hasMore = true, bool loadingMore = false, bool failed = false,
        object? resetKey = null) =>
        ctx.Render<OdsInfiniteList<string>>(p => p
            .Add(c => c.Remote, true)
            .Add(c => c.Items, items)
            .Add(c => c.RenderItem, Card)
            .Add(c => c.Noun, "articles")
            .Add(c => c.RemoteTotal, total)
            .Add(c => c.RemoteTotalCapped, capped)
            .Add(c => c.HasMoreRemote, hasMore)
            .Add(c => c.LoadingMore, loadingMore)
            .Add(c => c.LoadMoreFailed, failed)
            .Add(c => c.ResetKey, resetKey ?? 1)
            .Add(c => c.LoadMore, (bool fromPill) => calls.Add(fromPill)));

    private static void Update(IRenderedComponent<OdsInfiniteList<string>> cut, IReadOnlyList<string> items,
        bool hasMore = true, bool loadingMore = false, bool failed = false, object? resetKey = null) =>
        cut.Render(p => p
            .Add(c => c.Items, items)
            .Add(c => c.HasMoreRemote, hasMore)
            .Add(c => c.LoadingMore, loadingMore)
            .Add(c => c.LoadMoreFailed, failed)
            .Add(c => c.ResetKey, resetKey ?? 1));

    /// <summary>The element's text as a screen reader names it: every <c>aria-hidden</c> subtree left out.</summary>
    internal static string SpokenText(AngleSharp.Dom.IElement element)
    {
        var clone = (AngleSharp.Dom.IElement)element.Clone(deep: true);
        foreach (var hidden in clone.QuerySelectorAll("[aria-hidden='true']").ToList())
            hidden.Remove();
        return System.Text.RegularExpressions.Regex.Replace(clone.TextContent, @"\s+", " ").Trim();
    }

    private static string Announcement(IRenderedComponent<OdsInfiniteList<string>> cut) =>
        cut.Find(".odc-infinite-announce").TextContent.Trim();

    // ── Remote: no windowing ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Remote_mode_renders_every_item_with_no_window()
    {
        using var ctx = NewContext(out _);
        var cut = RenderRemote(ctx, ItemsOf(120), []);

        Assert.Equal(120, cut.FindAll(".card").Count);
    }

    // ── AC 50: the pill's name is its visible text, in every state ───────────────────────────

    [Fact]
    public void The_idle_pill_reads_loaded_of_total_and_carries_no_aria_label()
    {
        using var ctx = NewContext(out _);
        var cut = RenderRemote(ctx, ItemsOf(50), []);

        var pill = cut.Find("button.odc-infinite-pill");
        Assert.Equal("50 of 1,234 articles loaded · Load more", SpokenText(pill));
        Assert.False(pill.HasAttribute("aria-label"));
        Assert.False(pill.HasAttribute("aria-disabled"));
    }

    [Fact]
    public void A_capped_total_reads_10000_plus_with_an_sr_only_more_than()
    {
        using var ctx = NewContext(out _);
        var cut = RenderRemote(ctx, ItemsOf(50), [], total: 10000, capped: true);

        var pill = cut.Find("button.odc-infinite-pill");
        Assert.Equal("10,000+", pill.QuerySelector("[aria-hidden='true']:not(.material-icons)")!.TextContent);
        Assert.Equal("more than 10,000", pill.QuerySelector(".odc-sr-only")!.TextContent);
        Assert.Equal("50 of more than 10,000 articles loaded · Load more", SpokenText(pill));
    }

    [Fact]
    public void While_loading_the_pill_is_aria_disabled_not_disabled_and_says_loading()
    {
        using var ctx = NewContext(out _);
        var calls = new List<bool>();
        var cut = RenderRemote(ctx, ItemsOf(50), calls, loadingMore: true);

        var pill = cut.Find("button.odc-infinite-pill");
        Assert.Equal("Loading more articles…", SpokenText(pill));
        Assert.Equal("true", pill.GetAttribute("aria-disabled"));
        Assert.False(pill.HasAttribute("disabled"));

        pill.Click();
        Assert.Empty(calls);
    }

    [Fact]
    public void After_a_failure_the_pill_offers_retry_and_pressing_it_asks_again()
    {
        using var ctx = NewContext(out _);
        var calls = new List<bool>();
        var cut = RenderRemote(ctx, ItemsOf(50), calls, failed: true);

        var pill = cut.Find("button.odc-infinite-pill");
        Assert.Equal("Couldn’t load more. Retry", SpokenText(pill));
        Assert.False(pill.HasAttribute("aria-disabled"));

        pill.Click();
        Assert.Equal([true], calls);
        Assert.Equal(50, cut.FindAll(".card").Count);   // AC 45: the loaded rows stay
    }

    [Fact]
    public void The_pill_sits_outside_every_live_region()
    {
        using var ctx = NewContext(out _);
        var cut = RenderRemote(ctx, ItemsOf(50), []);

        var pill = cut.Find("button.odc-infinite-pill");
        Assert.Null(pill.Closest("[role='status']"));
        Assert.Null(pill.Closest("[aria-live]"));
    }

    [Fact]
    public void Pressing_the_pill_asks_with_fromPill_true()
    {
        using var ctx = NewContext(out _);
        var calls = new List<bool>();
        var cut = RenderRemote(ctx, ItemsOf(50), calls);

        cut.Find("button.odc-infinite-pill").Click();

        Assert.Equal([true], calls);
    }

    // ── AC 33: the pill disappears at nextCursor: null ───────────────────────────────────────

    [Fact]
    public void No_next_page_removes_the_pill_and_the_sentinel_but_keeps_the_status_region()
    {
        using var ctx = NewContext(out _);
        var cut = RenderRemote(ctx, ItemsOf(50), [], hasMore: false);

        Assert.Empty(cut.FindAll("button.odc-infinite-pill"));
        Assert.Empty(cut.FindAll("div[aria-hidden='true'][style='height:1px']"));
        var status = cut.Find(".odc-infinite-announce");
        Assert.Equal("status", status.GetAttribute("role"));
        Assert.Equal("polite", status.GetAttribute("aria-live"));
    }

    // ── §4.4 announcements: appends only ─────────────────────────────────────────────────────

    [Fact]
    public void An_append_is_announced_with_the_new_and_loaded_counts()
    {
        using var ctx = NewContext(out _);
        var cut = RenderRemote(ctx, ItemsOf(50), []);
        Assert.Equal(string.Empty, Announcement(cut));   // a first page is the page's to announce

        Update(cut, ItemsOf(50), loadingMore: true);
        Update(cut, ItemsOf(100));

        Assert.Equal("50 more articles loaded, 100 of 1,234.", Announcement(cut));
    }

    [Fact]
    public void The_last_page_still_announces_its_append_after_the_pill_is_gone()
    {
        using var ctx = NewContext(out _);
        var cut = RenderRemote(ctx, ItemsOf(50), [], total: 60);

        Update(cut, ItemsOf(50), loadingMore: true);
        Update(cut, ItemsOf(60), hasMore: false);

        Assert.Empty(cut.FindAll("button.odc-infinite-pill"));
        Assert.Equal("10 more articles loaded, 60 of 60.", Announcement(cut));
    }

    [Fact]
    public void A_capped_append_says_more_than()
    {
        using var ctx = NewContext(out _);
        var cut = RenderRemote(ctx, ItemsOf(50), [], total: 10000, capped: true);

        Update(cut, ItemsOf(50), loadingMore: true);
        Update(cut, ItemsOf(100));

        Assert.Equal("50 more articles loaded, 100 of more than 10,000.", Announcement(cut));
    }

    [Fact]
    public void A_failed_append_is_announced()
    {
        using var ctx = NewContext(out _);
        var cut = RenderRemote(ctx, ItemsOf(50), []);

        Update(cut, ItemsOf(50), loadingMore: true);
        Update(cut, ItemsOf(50), failed: true);

        Assert.Equal("Couldn’t load more articles.", Announcement(cut));
    }

    [Fact]
    public void A_fresh_first_page_is_not_announced_as_an_append()
    {
        using var ctx = NewContext(out _);
        var cut = RenderRemote(ctx, ItemsOf(20), []);

        // A filter change: new ResetKey, a bigger first page.
        Update(cut, ItemsOf(50), resetKey: 2);

        Assert.Equal(string.Empty, Announcement(cut));
    }

    [Fact]
    public void Rows_that_grow_without_a_load_more_are_not_announced()
    {
        using var ctx = NewContext(out _);
        var cut = RenderRemote(ctx, ItemsOf(20), []);

        Update(cut, ItemsOf(21));

        Assert.Equal(string.Empty, Announcement(cut));
    }

    // ── AC 48: one load at a time; none on scroll after a failure; no stall ──────────────────

    [Fact]
    public async Task The_sentinel_asks_with_fromPill_false()
    {
        using var ctx = NewContext(out _);
        var calls = new List<bool>();
        var cut = RenderRemote(ctx, ItemsOf(50), calls);

        await cut.InvokeAsync(() => cut.Instance.OnSentinelVisible());

        Assert.Equal([false], calls);
    }

    [Fact]
    public async Task The_sentinel_never_asks_while_a_page_is_in_flight()
    {
        using var ctx = NewContext(out _);
        var calls = new List<bool>();
        var cut = RenderRemote(ctx, ItemsOf(50), calls, loadingMore: true);

        await cut.InvokeAsync(() => cut.Instance.OnSentinelVisible());

        Assert.Empty(calls);
    }

    [Fact]
    public async Task The_sentinel_never_asks_after_a_failure_until_retry()
    {
        using var ctx = NewContext(out _);
        var calls = new List<bool>();
        var cut = RenderRemote(ctx, ItemsOf(50), calls, failed: true);

        await cut.InvokeAsync(() => cut.Instance.OnSentinelVisible());

        Assert.Empty(calls);
    }

    [Fact]
    public async Task LoadMore_is_never_called_concurrently()
    {
        using var ctx = NewContext(out _);
        var calls = 0;
        var gate = new TaskCompletionSource();
        var cut = ctx.Render<OdsInfiniteList<string>>(p => p
            .Add(c => c.Remote, true)
            .Add(c => c.Items, ItemsOf(50))
            .Add(c => c.RenderItem, Card)
            .Add(c => c.HasMoreRemote, true)
            .Add(c => c.LoadMore, async (bool _) =>
            {
                calls++;
                await gate.Task;
            }));

        var first = cut.InvokeAsync(() => cut.Instance.OnSentinelVisible());
        await cut.InvokeAsync(() => cut.Instance.OnSentinelVisible());
        await cut.InvokeAsync(() => cut.Find("button.odc-infinite-pill").Click());
        Assert.Equal(1, calls);

        gate.SetResult();
        await first;
        await cut.InvokeAsync(() => cut.Instance.OnSentinelVisible());
        Assert.Equal(2, calls);
    }

    [Fact]
    public void After_an_append_the_sentinel_is_observed_afresh_so_a_tall_screen_fills_itself()
    {
        using var ctx = NewContext(out var module);
        var cut = RenderRemote(ctx, ItemsOf(50), []);
        cut.WaitForAssertion(() => Assert.Single(module.Invocations["observe"]));

        Update(cut, ItemsOf(50), loadingMore: true);
        cut.WaitForAssertion(() => Assert.NotEmpty(module.Invocations["unobserve"]));
        Update(cut, ItemsOf(100));

        // A fresh IntersectionObserver reports the sentinel's current state, so one still in view asks again.
        cut.WaitForAssertion(() => Assert.Equal(2, module.Invocations["observe"].Count));
    }

    // ── Remote list states still come from OdsListStatus ─────────────────────────────────────

    [Fact]
    public void A_failed_first_page_shows_the_error_state_and_no_pill()
    {
        using var ctx = NewContext(out _);
        var cut = ctx.Render<OdsInfiniteList<string>>(p => p
            .Add(c => c.Remote, true)
            .Add(c => c.Items, Array.Empty<string>())
            .Add(c => c.RenderItem, Card)
            .Add(c => c.HasMoreRemote, true)
            .Add(c => c.Error, true));

        Assert.Empty(cut.FindAll("button.odc-infinite-pill"));
        Assert.Empty(cut.FindAll(".card"));
    }

    // ── Footer-only mode (AC 64) ─────────────────────────────────────────────────────────────

    private static IRenderedComponent<OdsInfiniteList<string>> RenderFooter(
        BunitContext ctx, int total, int window, List<int> grown) =>
        ctx.Render<OdsInfiniteList<string>>(p => p
            .Add(c => c.FooterOnly, true)
            .Add(c => c.Items, ItemsOf(total))
            .Add(c => c.Noun, "tags")
            .Add(c => c.BatchSize, 25)
            .Add(c => c.WindowSize, window)
            .Add(c => c.WindowSizeChanged, (int size) => grown.Add(size)));

    [Fact]
    public void Footer_only_renders_no_items_and_no_list_states()
    {
        using var ctx = NewContext(out _);
        var cut = ctx.Render<OdsInfiniteList<string>>(p => p
            .Add(c => c.FooterOnly, true)
            .Add(c => c.Items, Array.Empty<string>())
            .Add(c => c.WindowSize, 25)
            .Add(c => c.Loading, true)
            .Add(c => c.Error, true));

        Assert.Equal(string.Empty, cut.Markup.Trim());
    }

    [Fact]
    public void Footer_only_shows_the_pill_with_the_window_and_grows_it_by_a_batch()
    {
        using var ctx = NewContext(out _);
        var grown = new List<int>();
        var cut = RenderFooter(ctx, total: 60, window: 25, grown);

        var pill = cut.Find("button.odc-infinite-pill");
        Assert.Contains("25 of 60 tags", pill.TextContent);
        pill.Click();

        Assert.Equal([50], grown);
    }

    [Fact]
    public void Footer_only_growth_stops_at_the_total()
    {
        using var ctx = NewContext(out _);
        var grown = new List<int>();
        var cut = RenderFooter(ctx, total: 60, window: 50, grown);

        cut.Find("button.odc-infinite-pill").Click();

        Assert.Equal([60], grown);
    }

    [Fact]
    public async Task Footer_only_grows_from_the_sentinel_too()
    {
        using var ctx = NewContext(out _);
        var grown = new List<int>();
        var cut = RenderFooter(ctx, total: 60, window: 25, grown);

        await cut.InvokeAsync(() => cut.Instance.OnSentinelVisible());

        Assert.Equal([50], grown);
    }

    [Fact]
    public void Footer_only_renders_nothing_once_the_window_covers_the_set()
    {
        using var ctx = NewContext(out _);
        var cut = RenderFooter(ctx, total: 20, window: 25, []);

        Assert.Empty(cut.FindAll("button.odc-infinite-pill"));
        Assert.Empty(cut.FindAll("[role='status']"));
    }
}
