using System.Net;
using Bunit;
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
/// Mark all read (issue #473 §3.2.9, K9; AC 5, 29, 31, 37, 43): the count rule, the scope in words, the repeats with
/// the list's own filters and echoed <c>asOfUtc</c>, the throttled progress line, Cancel after the call in flight, the
/// mid-run busy rule, the partial failure, the out-of-date 422, and focus to the visible target afterwards.
/// </summary>
public class NewsMarkAllReadTests
{
    private static readonly Guid TagId = Guid.NewGuid();

    private static readonly DateTime AsOf = Now.UtcDateTime.AddMinutes(-1);

    private static NewsPageTestContext Ctx(List<NewsFeedCatalogueEntry>? catalogue = null, NewsUnreadCounts? counts = null,
        NewsArticlePage? page = null) =>
        Context([Article("A")], catalogue: catalogue, tags: [new() { NewsTagId = TagId, Name = "Climate" }, new() { NewsTagId = Guid.NewGuid(), Name = "Energy" }],
            counts: counts ?? new NewsUnreadCounts
            {
                Total = 42,
                ByFeed = [new() { FeedId = FeedId, UnreadCount = 7 }],
                ByTag = [new() { TagId = TagId, UnreadCount = 3 }],
            },
            page: page ?? (Page([Article("A")]) with { AsOfUtc = AsOf }));

    private static async Task<IRenderedComponent<Bunit.Rendering.ContainerFragment>> Open(NewsPageTestContext context,
        Func<IRenderedComponent<Bunit.Rendering.ContainerFragment>, Task>? filter = null)
    {
        var cut = context.Render<NewsPage>();
        cut.WaitForAssertion(() => Assert.Single(CardNames(cut)));
        if (filter is not null)
        {
            await filter(cut);
            cut.WaitForAssertion(() => Assert.Single(CardNames(cut)));
        }

        OpenMarkAll(cut);
        cut.WaitForElement("#nw-markall-desc");
        return cut;
    }

    private static string Sentence(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut) =>
        string.Join(' ', cut.Find("#nw-markall-desc").TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static Func<IRenderedComponent<Bunit.Rendering.ContainerFragment>, Task> Choose(string label, params string[] values) =>
        cut => cut.InvokeAsync(() => cut.FindComponents<OdsMultiSelect>().Single(m => m.Instance.Label == label || m.Instance.Label == $"{label}: All")
            .Instance.ValuesChanged.InvokeAsync(values));

    private static Func<IRenderedComponent<Bunit.Rendering.ContainerFragment>, Task> Then(params Func<IRenderedComponent<Bunit.Rendering.ContainerFragment>, Task>[] steps) =>
        async cut =>
        {
            foreach (var step in steps)
            {
                await step(cut);
            }
        };

    private static string Snackbars(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut) =>
        string.Join(" | ", cut.FindAll(".mud-snackbar").Select(s => s.TextContent.Trim()));

    // ── The number (AC 31) ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task With_no_feed_or_tag_filter_it_shows_the_overall_unread_total()
    {
        await using var context = Ctx();
        var cut = await Open(context);

        Assert.Equal("Mark 42 unread articles in all feeds as read?", Sentence(cut));
    }

    [Fact]
    public async Task With_exactly_one_feed_alone_it_shows_that_feeds_count()
    {
        await using var context = Ctx();
        var cut = await Open(context, Choose("Feed", FeedId.ToString()));

        Assert.Equal("Mark 7 unread articles in Example News as read?", Sentence(cut));
        Assert.Equal("Example News", cut.Find("#nw-markall-desc em").TextContent);
    }

    [Fact]
    public async Task With_exactly_one_tag_alone_it_shows_that_tags_count()
    {
        await using var context = Ctx();
        var cut = await Open(context, Choose("Tag", TagId.ToString()));

        Assert.Equal("Mark 3 unread articles tagged Climate as read?", Sentence(cut));
    }

    [Fact]
    public async Task One_archived_feed_that_the_counts_omit_shows_no_number_rather_than_zero()
    {
        var archived = CatalogueFeed("Old", f => f.State = NewsFeedState.Archived);
        await using var context = Ctx([CatalogueFeed("Example News", id: FeedId), archived]);
        var cut = await Open(context, Choose("Feed", archived.Id.ToString()));

        Assert.Equal("Mark all unread articles in Old as read?", Sentence(cut));
    }

    [Fact]
    public async Task A_feed_and_a_tag_together_show_no_number_and_name_both()
    {
        await using var context = Ctx();
        var cut = await Open(context, Then(Choose("Feed", FeedId.ToString()), Choose("Tag", TagId.ToString())));

        Assert.Equal("Mark all unread articles in Example News tagged Climate as read?", Sentence(cut));
    }

    [Fact]
    public async Task Several_feeds_and_several_tags_are_named_in_words()
    {
        var other = CatalogueFeed("Other");
        var third = CatalogueFeed("Third");
        await using var context = Ctx([CatalogueFeed("Example News", id: FeedId), other, third]);
        var cut = await Open(context, Choose("Feed", FeedId.ToString(), other.Id.ToString(), third.Id.ToString()));

        Assert.Equal("Mark all unread articles in Example News and 2 other feeds as read?", Sentence(cut));
    }

    [Fact]
    public async Task A_search_or_a_capped_total_or_any_status_other_than_unread_shows_no_number()
    {
        await using (var context = Ctx())
        {
            var cut = await Open(context, cut => cut.InvokeAsync(() => cut.FindComponent<OdsSearchField>().Instance.ValueChanged.InvokeAsync("election")));
            Assert.Equal("Mark all unread articles in all feeds matching ‘election’ as read?", Sentence(cut));
        }

        await using (var context = Ctx(page: Page([Article("A")], total: NewsLimits.MaxCountedArticles, capped: true)))
        {
            var cut = await Open(context);
            Assert.Equal("Mark all unread articles in all feeds as read?", Sentence(cut));
        }

        await using (var context = Ctx())
        {
            var cut = await Open(context, Choose("Status"));
            Assert.Equal("Mark all unread articles in all feeds as read?", Sentence(cut));
        }
    }

    // ── The run (AC 5, 43) ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task It_repeats_with_the_lists_filters_and_as_of_while_has_more_then_reports_the_total_once()
    {
        await using var context = Ctx();
        var calls = new List<(NewsArticleListFilter Filter, DateTime AsOf)>();
        context.News.Setup(n => n.MarkReadAsync(It.IsAny<NewsArticleListFilter>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((NewsArticleListFilter f, DateTime asOf, CancellationToken _) =>
            {
                calls.Add((f, asOf));
                return Ok(new MarkNewsReadResult { MarkedCount = calls.Count < 3 ? NewsLimits.MaxMarkReadPerRequest : 234, HasMore = calls.Count < 3 });
            });
        var cut = await Open(context, Then(Choose("Feed", FeedId.ToString()), Choose("Tag", TagId.ToString())));
        context.News.Invocations.Clear();

        cut.Find("#nw-markall-confirm").Click();

        cut.WaitForAssertion(() => Assert.Contains("10,234 articles marked as read.", Snackbars(cut), StringComparison.Ordinal));
        Assert.Equal(3, calls.Count);
        Assert.All(calls, c => Assert.Equal(AsOf, c.AsOf));
        Assert.All(calls, c => Assert.Equal([FeedId], c.Filter.FeedIds));
        Assert.All(calls, c => Assert.Equal([TagId], c.Filter.TagIds));
        Assert.All(calls, c => Assert.Equal([NewsArticleReadStatus.Unread], c.Filter.Statuses));
        // Said once — by the snackbar, which is a live region in MudBlazor 9 — never twice (AC 29).
        Assert.DoesNotContain("marked as read", Polite(cut), StringComparison.Ordinal);
        // Then a fresh first page, focus to the visible target, and the counts and catalogue re-read.
        cut.WaitForAssertion(() => context.News.Verify(n => n.ListArticlesAsync(It.IsAny<NewsArticleListFilter>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()), Times.Once));
        Assert.Contains(context.Ctx.JSInterop.Invocations, i => i.Identifier == "odsNewsFocusListStart");
        context.News.Verify(n => n.GetUnreadCountsAsync(It.IsAny<CancellationToken>()), Times.Once);
        context.News.Verify(n => n.GetCatalogueAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.Empty(cut.FindAll("#nw-markall-desc"));
    }

    [Fact]
    public async Task While_marking_the_button_reads_marking_the_list_is_busy_and_progress_moves_at_most_every_five_seconds()
    {
        await using var context = Ctx();
        var steps = new Queue<TaskCompletionSource<ApiResult<MarkNewsReadResult>>>();
        for (var i = 0; i < 4; i++)
        {
            steps.Enqueue(new TaskCompletionSource<ApiResult<MarkNewsReadResult>>());
        }

        var pending = steps.ToList();
        context.News.Setup(n => n.MarkReadAsync(It.IsAny<NewsArticleListFilter>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(() => steps.Dequeue().Task);
        var cut = await Open(context);
        var step = new MarkNewsReadResult { MarkedCount = NewsLimits.MaxMarkReadPerRequest, HasMore = true };

        cut.Find("#nw-markall-confirm").Click();

        cut.WaitForAssertion(() => Assert.EndsWith("Marking…", cut.Find("#nw-markall-confirm").TextContent.Trim(), StringComparison.Ordinal));
        Assert.Equal("true", cut.Find("#nw-markall-confirm").GetAttribute("aria-disabled"));
        Assert.Equal("true", cut.Find(".nw-list").GetAttribute("aria-busy"));
        await cut.InvokeAsync(() => pending[0].SetResult(Ok(step)));
        cut.WaitForAssertion(() => Assert.Equal("Marking… 5,000 so far", cut.Find(".nw-run-line").TextContent.Trim()));
        Assert.Equal("status", cut.Find(".nw-run-line").GetAttribute("role"));
        await cut.InvokeAsync(() => pending[1].SetResult(Ok(step)));
        Assert.Equal("Marking… 5,000 so far", cut.Find(".nw-run-line").TextContent.Trim());
        await cut.InvokeAsync(() => context.Time.Advance(NewsMarkAllRun.ProgressInterval));
        await cut.InvokeAsync(() => pending[2].SetResult(Ok(step)));
        cut.WaitForAssertion(() => Assert.Equal("Marking… 15,000 so far", cut.Find(".nw-run-line").TextContent.Trim()));
        await cut.InvokeAsync(() => pending[3].SetResult(Ok(new MarkNewsReadResult { MarkedCount = 1, HasMore = false })));
        cut.WaitForAssertion(() => Assert.Contains("15,001 articles marked as read.", Snackbars(cut), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancel_stops_after_the_call_in_flight_and_reports_the_partial_total()
    {
        await using var context = Ctx();
        var first = new TaskCompletionSource<ApiResult<MarkNewsReadResult>>();
        var calls = 0;
        context.News.Setup(n => n.MarkReadAsync(It.IsAny<NewsArticleListFilter>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(() => ++calls == 1 ? first.Task : Task.FromResult(Ok(new MarkNewsReadResult { MarkedCount = 1, HasMore = true })));
        var cut = await Open(context);
        cut.Find("#nw-markall-confirm").Click();
        cut.WaitForAssertion(() => Assert.Equal(1, calls));

        cut.Find("[data-dialog-cancel]").Click();
        await cut.InvokeAsync(() => first.SetResult(Ok(new MarkNewsReadResult { MarkedCount = NewsLimits.MaxMarkReadPerRequest, HasMore = true })));

        cut.WaitForAssertion(() => Assert.Contains("5,000 articles marked as read. Stopped before finishing.", Snackbars(cut), StringComparison.Ordinal));
        Assert.Equal(1, calls);
        Assert.DoesNotContain("Stopped before finishing", Polite(cut), StringComparison.Ordinal);
        Assert.Contains(context.Ctx.JSInterop.Invocations, i => i.Identifier == "odsNewsFocusListStart");
    }

    [Fact]
    public async Task A_busy_call_partway_waits_out_retry_after_and_repeats_once_and_a_second_stops()
    {
        await using var context = Ctx();
        var calls = 0;
        context.News.Setup(n => n.MarkReadAsync(It.IsAny<NewsArticleListFilter>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++calls == 1
                ? Ok(new MarkNewsReadResult { MarkedCount = NewsLimits.MaxMarkReadPerRequest, HasMore = true })
                : Busy<MarkNewsReadResult>());
        var cut = await Open(context);

        cut.Find("#nw-markall-confirm").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, calls));
        await cut.InvokeAsync(() => context.Time.Advance(TimeSpan.FromSeconds(2)));
        cut.WaitForAssertion(() => Assert.Contains("5,000 articles marked as read. Stopped before finishing.", Snackbars(cut), StringComparison.Ordinal));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task A_later_failure_says_some_were_marked_and_stays_until_dismissed()
    {
        await using var context = Ctx();
        var calls = 0;
        context.News.Setup(n => n.MarkReadAsync(It.IsAny<NewsArticleListFilter>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++calls == 1
                ? Ok(new MarkNewsReadResult { MarkedCount = 10, HasMore = true })
                : Fail<MarkNewsReadResult>(HttpStatusCode.InternalServerError));
        var cut = await Open(context);

        cut.Find("#nw-markall-confirm").Click();

        cut.WaitForAssertion(() => Assert.Contains("Some articles were marked as read; try again to finish.", Snackbars(cut), StringComparison.Ordinal));
        Assert.Contains(context.Ctx.JSInterop.Invocations, i => i.Identifier == "odsNewsFocusListStart");
    }

    [Fact]
    public async Task A_422_says_the_list_is_out_of_date_and_refresh_takes_a_fresh_first_page()
    {
        await using var context = Ctx();
        context.News.Setup(n => n.MarkReadAsync(It.IsAny<NewsArticleListFilter>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<MarkNewsReadResult>(HttpStatusCode.UnprocessableEntity, NewsErrorCodes.MarkReadScopeInvalid));
        var cut = await Open(context);
        context.News.Invocations.Clear();

        cut.Find("#nw-markall-confirm").Click();

        cut.WaitForAssertion(() => Assert.Contains("The list is out of date. Refresh and try again.", cut.Markup, StringComparison.Ordinal));
        Assert.Equal("true", cut.Find("#nw-markall-confirm").GetAttribute("aria-disabled"));
        cut.FindAll(".mud-alert button").Single(b => b.TextContent.Contains("Refresh", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => context.News.Verify(n => n.ListArticlesAsync(It.IsAny<NewsArticleListFilter>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()), Times.Once));
    }

    [Fact]
    public async Task A_busy_first_call_is_the_busy_snackbar()
    {
        await using var context = Ctx();
        context.News.Setup(n => n.MarkReadAsync(It.IsAny<NewsArticleListFilter>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Busy<MarkNewsReadResult>());
        var cut = await Open(context);

        cut.Find("#nw-markall-confirm").Click();

        cut.WaitForAssertion(() => Assert.Contains(NewsFeedback.BusyText, Snackbars(cut), StringComparison.Ordinal));
        context.News.Verify(n => n.MarkReadAsync(It.IsAny<NewsArticleListFilter>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task The_confirm_opens_on_cancel()
    {
        await using var context = Ctx();
        var cut = await Open(context);

        Assert.Contains(context.Ctx.JSInterop.Invocations, i => i.Identifier == "odsFocusDialogCancel");
        Assert.Contains("Articles that arrived after this list was loaded stay unread.", cut.Markup, StringComparison.Ordinal);
    }
}
