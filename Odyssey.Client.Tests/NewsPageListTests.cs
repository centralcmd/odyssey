using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Internet;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;
using Xunit;
using static Odyssey.Client.Tests.NewsPageTestContext;
using static Odyssey.Client.Tests.NewsPageTests;

namespace Odyssey.Client.Tests;

/// <summary>
/// The /news article list in OdsInfiniteList's remote mode and its filter bar (issue #473 §3.2.3, §3.2.4, §4.4; AC 33,
/// 45, 46, 47, 49, 50, 52, 53, 56): appends by cursor with de-duplication, the pill and its focus rule, stale responses
/// dropped, the cursor restart, the Status mapping, the 50-id cap and the deep link.
/// </summary>
public class NewsPageListTests
{
    private sealed record Call(NewsArticleListFilter Filter, int Limit, string? Cursor, CancellationToken Token);

    /// <summary>Records every list call; first pages answer <paramref name="first"/>, appends answer by cursor.</summary>
    private static (NewsPageTestContext Context, List<Call> Calls) Recorded(Func<Call, ApiResult<NewsArticlePage>> answer,
        IReadOnlyList<string>? claims = null, List<NewsFeedCatalogueEntry>? catalogue = null, List<ExistingNewsTag>? tags = null)
    {
        var context = Context([], claims, catalogue, tags);
        var calls = new List<Call>();
        context.News.Setup(n => n.ListArticlesAsync(It.IsAny<NewsArticleListFilter>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((NewsArticleListFilter f, int l, string? c, CancellationToken t) =>
            {
                var call = new Call(f, l, c, t);
                calls.Add(call);
                return answer(call);
            });
        return (context, calls);
    }

    private static IRenderedComponent<OdsInfiniteList<NewsArticleSummary>> List(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut) =>
        cut.FindComponent<OdsInfiniteList<NewsArticleSummary>>();

    private static IRenderedComponent<OdsMultiSelect> Select(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string label) =>
        cut.FindComponents<OdsMultiSelect>().Single(m => m.Instance.Label == label || m.Instance.Label == $"{label}: All");

    private static IEnumerable<string?> FocusedCards(NewsPageTestContext context) =>
        context.Ctx.JSInterop.Invocations.Where(i => i.Identifier == "odsNewsFocusCard").Select(i => i.Arguments[0] as string);

    [Fact]
    public async Task Appends_the_next_page_by_cursor_drops_duplicates_and_stops_at_a_null_cursor()
    {
        var first = Enumerable.Range(0, 3).Select(i => Article($"A{i}")).ToList();
        var second = new List<NewsArticleSummary> { first[2], Article("B0"), Article("B1") };
        var (context, calls) = Recorded(c => Ok(c.Cursor is null ? Page(first, total: 5, next: "c1") : Page(second, total: null, next: null)));
        await using var _ = context;
        var cut = context.Render<NewsPage>();
        cut.WaitForAssertion(() => Assert.Equal(3, CardNames(cut).Count));
        Assert.Contains("3 of 5 articles loaded · Load more", cut.Find(".odc-infinite-pill").TextContent, StringComparison.Ordinal);

        await cut.InvokeAsync(() => List(cut).Instance.LoadMore.InvokeAsync(true));

        cut.WaitForAssertion(() => Assert.Equal(["A0", "A1", "A2", "B0", "B1"], CardNames(cut)));
        Assert.Equal("c1", calls[1].Cursor);
        Assert.Same(calls[0].Filter, calls[1].Filter);
        Assert.Empty(cut.FindAll(".odc-infinite-pill"));
        // A pill press moves focus to the first NEW card, also on the last page (AC 47).
        Assert.Contains(NewsArticleCard.CardId(second[1].Id), FocusedCards(context));
        cut.WaitForAssertion(() => Assert.Contains("2 more articles loaded, 5 of 5.", cut.Find(".odc-infinite-announce").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_scroll_driven_append_moves_no_focus()
    {
        var (context, _) = Recorded(c => Ok(c.Cursor is null ? Page([Article("A")], total: 2, next: "c1") : Page([Article("B")])));
        await using var _ = context;
        var cut = context.Render<NewsPage>();
        cut.WaitForAssertion(() => Assert.Single(CardNames(cut)));

        await cut.InvokeAsync(() => List(cut).Instance.LoadMore.InvokeAsync(false));

        cut.WaitForAssertion(() => Assert.Equal(2, CardNames(cut).Count));
        Assert.Empty(FocusedCards(context));
    }

    [Fact]
    public async Task The_pill_reads_ten_thousand_plus_while_capped_and_the_first_page_is_announced()
    {
        var (context, _) = Recorded(_ => Ok(Page([Article("A")], total: NewsLimits.MaxCountedArticles, next: "c1", capped: true)));
        await using var _ = context;
        var cut = context.Render<NewsPage>();

        cut.WaitForAssertion(() => Assert.Contains($"{NewsLimits.MaxCountedArticles:N0}+", cut.Find(".odc-infinite-pill").TextContent, StringComparison.Ordinal));
        Assert.Contains($"more than {NewsLimits.MaxCountedArticles:N0}", cut.Find(".odc-infinite-pill").TextContent, StringComparison.Ordinal);
        Assert.Null(cut.Find(".odc-infinite-pill").GetAttribute("aria-label"));
        cut.WaitForAssertion(() => Assert.Contains($"More than {NewsLimits.MaxCountedArticles:N0} articles", Polite(cut), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_append_keeps_the_rows_and_retry_reuses_the_cursor()
    {
        var fail = true;
        var (context, calls) = Recorded(c => c.Cursor is null
            ? Ok(Page([Article("A")], total: 2, next: "c1"))
            : fail ? Fail<NewsArticlePage>(HttpStatusCode.InternalServerError) : Ok(Page([Article("B")])));
        await using var _ = context;
        var cut = context.Render<NewsPage>();
        cut.WaitForAssertion(() => Assert.Single(CardNames(cut)));

        await cut.InvokeAsync(() => List(cut).Instance.LoadMore.InvokeAsync(true));

        cut.WaitForAssertion(() => Assert.Contains("Couldn’t load more. Retry", cut.Find(".odc-infinite-pill").TextContent, StringComparison.Ordinal));
        Assert.Single(CardNames(cut));
        fail = false;
        cut.Find(".odc-infinite-pill").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, CardNames(cut).Count));
        Assert.Equal(["c1", "c1"], calls.Where(c => c.Cursor is not null).Select(c => c.Cursor));
    }

    [Fact]
    public async Task A_400_keyed_cursor_restarts_from_a_fresh_first_page_without_an_error()
    {
        var (context, calls) = Recorded(c => c.Cursor is null
            ? Ok(Page([Article("A")], total: 2, next: "stale"))
            : Fail<NewsArticlePage>(HttpStatusCode.BadRequest, NewsErrorCodes.CursorInvalid));
        await using var _ = context;
        var cut = context.Render<NewsPage>();
        cut.WaitForAssertion(() => Assert.Single(CardNames(cut)));

        await cut.InvokeAsync(() => List(cut).Instance.LoadMore.InvokeAsync(false));

        cut.WaitForAssertion(() => Assert.Equal(2, calls.Count(c => c.Cursor is null)));
        Assert.DoesNotContain("Couldn’t load", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_response_from_before_a_filter_change_is_discarded_and_its_request_cancelled()
    {
        var slow = new TaskCompletionSource<ApiResult<NewsArticlePage>>();
        await using var context = Context([]);
        var calls = new List<Call>();
        var count = 0;
        context.News.Setup(n => n.ListArticlesAsync(It.IsAny<NewsArticleListFilter>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((NewsArticleListFilter f, int l, string? c, CancellationToken t) =>
            {
                calls.Add(new Call(f, l, c, t));
                return ++count switch
                {
                    1 => Task.FromResult(Ok(Page([Article("Initial")]))),
                    2 => slow.Task,
                    _ => Task.FromResult(Ok(Page([Article("Newest")]))),
                };
            });
        var cut = context.Render<NewsPage>();
        cut.WaitForAssertion(() => Assert.Equal(["Initial"], CardNames(cut)));

        _ = cut.InvokeAsync(() => Select(cut, "Status").Instance.ValuesChanged.InvokeAsync([NewsPage.ReadKey]));
        cut.WaitForAssertion(() => Assert.Equal(2, calls.Count));
        // While the reset loads, the old rows stay under the refetch bar (§3.2.4).
        Assert.Equal(["Initial"], CardNames(cut));
        Assert.NotEmpty(cut.FindAll(".mud-progress-linear"));
        Assert.Equal("true", cut.Find(".nw-list").GetAttribute("aria-busy"));

        await cut.InvokeAsync(() => Select(cut, "Status").Instance.ValuesChanged.InvokeAsync([]));
        cut.WaitForAssertion(() => Assert.Equal(["Newest"], CardNames(cut)));
        Assert.True(calls[1].Token.IsCancellationRequested);

        await cut.InvokeAsync(() => slow.SetResult(Ok(Page([Article("Stale")]))));
        Assert.Equal(["Newest"], CardNames(cut));
    }

    [Fact]
    public async Task Status_unread_alone_sends_is_read_false_and_both_or_neither_send_none()
    {
        var (context, calls) = Recorded(_ => Ok(Page([Article("A")])));
        await using var _ = context;
        var cut = context.Render<NewsPage>();
        cut.WaitForAssertion(() => Assert.Single(calls));
        Assert.Equal(false, calls[0].Filter.IsRead);
        Assert.Equal("Status", Select(cut, "Status").Instance.Label);

        await cut.InvokeAsync(() => Select(cut, "Status").Instance.ValuesChanged.InvokeAsync([NewsPage.UnreadKey, NewsPage.ReadKey]));
        cut.WaitForAssertion(() => Assert.Equal(2, calls.Count));
        Assert.Null(calls[1].Filter.IsRead);
        Assert.Equal("Status: All", Select(cut, "Status").Instance.Label);

        await cut.InvokeAsync(() => Select(cut, "Status").Instance.ValuesChanged.InvokeAsync([]));
        cut.WaitForAssertion(() => Assert.Equal(3, calls.Count));
        Assert.Null(calls[2].Filter.IsRead);

        await cut.InvokeAsync(() => Select(cut, "Status").Instance.ValuesChanged.InvokeAsync([NewsPage.ReadKey]));
        cut.WaitForAssertion(() => Assert.Equal(4, calls.Count));
        Assert.Equal(true, calls[3].Filter.IsRead);
        // Nothing unread to mark while Status is Read only (§3.2.1).
        Assert.Empty(cut.FindAll("#nw-mark-all"));
    }

    [Fact]
    public async Task Changing_load_n_at_a_time_applies_from_the_next_append_without_a_reset()
    {
        var (context, calls) = Recorded(c => Ok(c.Cursor is null ? Page([Article("A")], total: 2, next: "c1") : Page([Article("B")])));
        await using var _ = context;
        var cut = context.Render<NewsPage>();
        cut.WaitForAssertion(() => Assert.Single(calls));

        await cut.InvokeAsync(() => cut.FindComponent<OdsPageSizeSelect>().Instance.ValueChanged.InvokeAsync(OdsPageSizes.Batch[2]));
        Assert.Single(calls);
        await cut.InvokeAsync(() => List(cut).Instance.LoadMore.InvokeAsync(false));

        cut.WaitForAssertion(() => Assert.Equal(2, calls.Count));
        Assert.Equal(OdsPageSizes.Batch[2], calls[1].Limit);
        Assert.Equal(OdsPageSizes.Batch, cut.FindComponent<OdsPageSizeSelect>().Instance.Options);
    }

    [Fact]
    public async Task The_filter_bar_renders_its_controls_in_order_and_nothing_else()
    {
        await using var context = Context([Article("A")]);
        var cut = context.Render<NewsPage>();
        cut.WaitForAssertion(() => Assert.Single(CardNames(cut)));

        var bar = cut.Find(".ph-filter-bar");
        var order = bar.Children.Select(c =>
            c.ClassList.Contains("nw-search") ? "search"
            : c.QuerySelector(".odc-sortsel") is not null || c.ClassList.Contains("odc-sortsel") ? "sort"
            : c.TextContent.Contains("at a time", StringComparison.Ordinal) ? "size"
            : c.TextContent.Trim()).ToList();
        Assert.Equal(["Feed", "Tag", "Status"], cut.FindComponents<OdsMultiSelect>().Select(m => m.Instance.Label));
        Assert.Equal("search", order[0]);
        Assert.Equal("size", order[^1]);
        Assert.Single(cut.FindComponents<OdsSortSelect<NewsArticleSummary>>());
        Assert.Empty(cut.FindComponents<OdsSegmentedControl>());
        Assert.Empty(cut.FindComponents<OdsSwitch>());
        Assert.Empty(cut.FindComponents<OdsDateRangePicker>());
        Assert.Empty(cut.FindComponents<OdsPager>());
    }

    [Fact]
    public async Task Relevance_is_offered_only_with_a_search_and_clearing_the_search_falls_back_and_says_so()
    {
        var (context, calls) = Recorded(_ => Ok(Page([Article("A")])));
        await using var _ = context;
        var cut = context.Render<NewsPage>();
        cut.WaitForAssertion(() => Assert.Single(calls));
        Assert.Equal(["Published"], cut.FindComponent<OdsSortSelect<NewsArticleSummary>>().Instance.Fields.Select(f => f.Label));

        await cut.InvokeAsync(() => cut.FindComponent<OdsSearchField>().Instance.ValueChanged.InvokeAsync("election"));
        Assert.Equal(["Published", "Relevance"], cut.FindComponent<OdsSortSelect<NewsArticleSummary>>().Instance.Fields.Select(f => f.Label));
        await cut.InvokeAsync(() => cut.FindComponent<OdsSortSelect<NewsArticleSummary>>().Instance.SortChanged.InvokeAsync(new OdsTableSort(NewsPage.RelevanceKey, OdsSortDirection.Desc)));
        cut.WaitForAssertion(() => Assert.Equal(NewsArticleSortBy.Relevance, calls[^1].Filter.SortBy));
        Assert.Equal("election", calls[^1].Filter.Search);

        await cut.InvokeAsync(() => cut.FindComponent<OdsSearchField>().Instance.ValueChanged.InvokeAsync(string.Empty));

        cut.WaitForAssertion(() => Assert.Equal(NewsArticleSortBy.Newest, calls[^1].Filter.SortBy));
        Assert.Null(calls[^1].Filter.Search);
        cut.WaitForAssertion(() => Assert.Contains("Sorted by newest, because the search was cleared", Polite(cut), StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_feed_and_tag_selects_are_capped_and_a_request_never_carries_more_ids()
    {
        var feeds = Enumerable.Range(0, NewsLimits.MaxFilterIds + 1).Select(i => CatalogueFeed($"Feed {i:00}")).ToList();
        var (context, calls) = Recorded(_ => Ok(Page([Article("A")])), catalogue: feeds);
        await using var _ = context;
        var cut = context.Render<NewsPage>();
        cut.WaitForAssertion(() => Assert.Single(calls));
        var feed = Select(cut, "Feed");
        Assert.Equal(NewsLimits.MaxFilterIds, feed.Instance.MaxSelected);
        Assert.Equal($"Up to {NewsLimits.MaxFilterIds} feeds", feed.Instance.MaxSelectedText);
        Assert.Equal(NewsLimits.MaxFilterIds, Select(cut, "Tag").Instance.MaxSelected);
        Assert.Equal($"Up to {NewsLimits.MaxFilterIds} tags", Select(cut, "Tag").Instance.MaxSelectedText);

        await cut.InvokeAsync(() => feed.Instance.ValuesChanged.InvokeAsync([.. feeds.Select(f => f.Id.ToString())]));

        cut.WaitForAssertion(() => Assert.Equal(2, calls.Count));
        Assert.Equal(NewsLimits.MaxFilterIds, calls[1].Filter.FeedIds!.Count);
    }

    [Fact]
    public async Task Feed_options_name_their_state_as_text_and_list_archived_feeds_last()
    {
        await using var context = Context([Article("A")], catalogue:
        [
            CatalogueFeed("Old", f => { f.State = NewsFeedState.Archived; f.Health = NewsFeedHealth.Archived; }),
            CatalogueFeed("Broken", f => f.Health = NewsFeedHealth.Failing),
            CatalogueFeed("Resting", f => { f.State = NewsFeedState.Paused; f.Health = NewsFeedHealth.Paused; }),
            CatalogueFeed("Fine", f => f.Language = "nb"),
        ]);
        var cut = context.Render<NewsPage>();
        cut.WaitForAssertion(() => Assert.Single(CardNames(cut)));

        var options = Select(cut, "Feed").Instance.Options;
        Assert.Equal(["Broken (needs attention)", "Fine", "Resting (paused)", "Old (archived)"], options.Select(o => o.Label));
        Assert.Equal("archive", options[^1].Icon);
    }

    [Fact]
    public async Task A_feed_link_preselects_exactly_that_feed_clears_search_and_keeps_status()
    {
        var other = CatalogueFeed("Other");
        var (context, calls) = Recorded(_ => Ok(Page([Article("A")])), catalogue: [CatalogueFeed("Example News", id: FeedId), other]);
        await using var _ = context;
        context.State.Setup(s => s.LoadAsync<NewsPage.NewsPageState>(NewsPage.PageStateKey))
            .ReturnsAsync(new NewsPage.NewsPageState { Search = "saved", FeedIds = [FeedId.ToString()], Statuses = [NewsPage.ReadKey] });

        context.Ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo($"news?feedId={other.Id}");
        var cut = context.Render<NewsPage>();

        cut.WaitForAssertion(() => Assert.Single(calls));
        Assert.Equal([other.Id], calls[0].Filter.FeedIds);
        Assert.Null(calls[0].Filter.Search);
        Assert.Equal(true, calls[0].Filter.IsRead);
    }

    [Fact]
    public async Task A_stale_feed_or_tag_id_is_dropped_before_the_first_load()
    {
        var (context, calls) = Recorded(_ => Ok(Page([Article("A")])));
        await using var _ = context;
        context.State.Setup(s => s.LoadAsync<NewsPage.NewsPageState>(NewsPage.PageStateKey))
            .ReturnsAsync(new NewsPage.NewsPageState { FeedIds = [Guid.NewGuid().ToString(), FeedId.ToString()], TagIds = [Guid.NewGuid().ToString()] });

        var cut = context.Render<NewsPage>();

        cut.WaitForAssertion(() => Assert.Single(calls));
        Assert.Equal([FeedId], calls[0].Filter.FeedIds);
        Assert.Null(calls[0].Filter.TagIds);
    }

    [Fact]
    public async Task Show_articles_from_the_feed_in_the_menu_filters_to_that_feed()
    {
        var article = Article("A");
        var (context, calls) = Recorded(_ => Ok(Page([article])));
        await using var _ = context;
        var cut = context.Render<NewsPage>();
        cut.WaitForAssertion(() => Assert.Single(calls));

        ClickMenu(cut, article, "Show articles from Example News");

        cut.WaitForAssertion(() => Assert.Equal(2, calls.Count));
        Assert.Equal([FeedId], calls[1].Filter.FeedIds);
        Assert.EndsWith($"news?feedId={FeedId}", context.Ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri, StringComparison.Ordinal);
    }
}
