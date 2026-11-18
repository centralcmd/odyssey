using System.Net;
using Bunit;
using Moq;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Internet;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Internet;
using Xunit;
using static Odyssey.Client.Tests.NewsPageTestContext;
using static Odyssey.Client.Tests.NewsPageTests;

namespace Odyssey.Client.Tests;

/// <summary>
/// The /news list's states and the search field's messages (issue #473 §3.7, §7.1, §7.4; AC 17, 18, 34, 35, 40, 54):
/// Loading, the four empty states, Error.Load and Busy.FirstLoad, and the 400 / 429 / 503 / gave-up search messages.
/// </summary>
public class NewsPageStatesTests
{
    private static void SetList(NewsPageTestContext context, Func<NewsArticleListFilter, ApiResult<NewsArticlePage>> answer) =>
        context.News.Setup(n => n.ListArticlesAsync(It.IsAny<NewsArticleListFilter>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((NewsArticleListFilter f, int _, string? _, CancellationToken _) => answer(f));

    private static AngleSharp.Dom.IElement SearchInput(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut) => cut.Find("input#news-search");

    private static Task Search(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string text) =>
        cut.InvokeAsync(() => cut.FindComponent<OdsSearchField>().Instance.ValueChanged.InvokeAsync(text));

    // ── States (§3.7) ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Loading_shows_skeleton_cards_announced_and_busy()
    {
        var catalogue = new TaskCompletionSource<ApiResult<List<NewsFeedCatalogueEntry>>>();
        await using var context = Context([Article("A")]);
        context.News.Setup(n => n.GetCatalogueAsync(It.IsAny<CancellationToken>())).Returns(catalogue.Task);

        var cut = context.Render<NewsPage>();

        Assert.Equal("true", cut.Find(".nw-loading").GetAttribute("aria-busy"));
        Assert.Contains("Loading articles…", cut.Find(".nw-loading [role=status]").TextContent, StringComparison.Ordinal);
        await cut.InvokeAsync(() => catalogue.SetResult(Ok(new List<NewsFeedCatalogueEntry> { CatalogueFeed("Example News", id: FeedId) })));
        cut.WaitForAssertion(() => Assert.Single(CardNames(cut)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task No_feeds_offers_new_feed_only_with_the_claim(bool canCreate)
    {
        await using var context = Context([], canCreate ? null : ReaderOnly, catalogue: []);

        var cut = context.Render<NewsPage>();

        cut.WaitForAssertion(() => Assert.Equal("No feeds yet", cut.Find("#nw-empty-h").TextContent));
        Assert.Equal(canCreate, cut.FindAll("a, button").Any(b => b.TextContent.Contains("New feed", StringComparison.Ordinal)));
        Assert.Equal(!canCreate, cut.Markup.Contains("Ask an administrator to add feeds.", StringComparison.Ordinal));
        Assert.Equal("-1", cut.Find("#nw-empty-h").GetAttribute("tabindex"));
        context.News.Verify(n => n.ListArticlesAsync(It.IsAny<NewsArticleListFilter>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task All_read_says_caught_up_and_show_all_articles_drops_the_status_filter()
    {
        await using var context = Context([]);
        var sent = new List<NewsArticleListFilter>();
        SetList(context, f => { sent.Add(f); return Ok(Page(f.IsRead is null ? [Article("Old", a => a.IsRead = true)] : [])); });

        var cut = context.Render<NewsPage>();

        cut.WaitForAssertion(() => Assert.Equal("You’re all caught up", cut.Find("#nw-empty-h").TextContent));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Show all articles", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.Equal(["Old"], CardNames(cut)));
        Assert.Null(sent[^1].IsRead);
        Assert.Contains(context.Ctx.JSInterop.Invocations, i => i.Identifier == "odsNewsFocusListStart");
    }

    [Fact]
    public async Task Filtered_empty_echoes_the_search_and_clear_filters_keeps_status_at_unread()
    {
        await using var context = Context([]);
        var sent = new List<NewsArticleListFilter>();
        SetList(context, f => { sent.Add(f); return Ok(Page(f.Search is null && f.FeedIds is null ? [Article("A")] : [])); });
        var cut = context.Render<NewsPage>();
        cut.WaitForAssertion(() => Assert.Single(CardNames(cut)));

        await Search(cut, "<b>quiet</b>");

        cut.WaitForAssertion(() => Assert.Equal("No articles match these filters", cut.Find("#nw-empty-h").TextContent));
        Assert.Contains("Nothing matches “<b>quiet</b>”.", cut.Find(".odc-empty").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".odc-empty b"));
        cut.WaitForAssertion(() => Assert.Contains("No articles match these filters.", Polite(cut), StringComparison.Ordinal));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Clear filters", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Single(CardNames(cut)));
        Assert.Null(sent[^1].Search);
        Assert.Equal(false, sent[^1].IsRead);
    }

    [Fact]
    public async Task Pending_feeds_say_they_have_not_been_fetched_yet()
    {
        await using var context = Context([], catalogue: [CatalogueFeed("New", f => { f.Health = NewsFeedHealth.Pending; f.ArticleCount = 0; f.UnreadCount = 0; })]);

        var cut = context.Render<NewsPage>();

        cut.WaitForAssertion(() => Assert.Equal("Your feeds haven’t been fetched yet.", cut.Find("#nw-empty-h").TextContent));
        Assert.Contains("This usually takes a minute.", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_first_load_shows_couldnt_load_articles_with_retry()
    {
        await using var context = Context([]);
        var fail = true;
        SetList(context, _ => fail ? Fail<NewsArticlePage>(HttpStatusCode.InternalServerError) : Ok(Page([Article("A")])));

        var cut = context.Render<NewsPage>();

        cut.WaitForAssertion(() => Assert.Contains("Couldn’t load articles.", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".mud-snackbar"));
        fail = false;
        cut.Find("#nw-load-retry").Click();
        cut.WaitForAssertion(() => Assert.Single(CardNames(cut)));
    }

    [Fact]
    public async Task A_busy_first_load_retries_once_by_itself_with_no_snackbar()
    {
        await using var context = Context([]);
        var calls = 0;
        SetList(context, _ => ++calls == 1 ? Busy<NewsArticlePage>() : Ok(Page([Article("A")])));

        var cut = context.Render<NewsPage>();

        cut.WaitForAssertion(() => Assert.Contains("News is busy. Retrying in a moment.", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".mud-snackbar"));
        await cut.InvokeAsync(() => context.Time.Advance(TimeSpan.FromSeconds(2)));
        cut.WaitForAssertion(() => Assert.Single(CardNames(cut)));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_busy_catalogue_is_the_lists_first_load_state()
    {
        await using var context = Context([]);
        context.News.Setup(n => n.GetCatalogueAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Busy<List<NewsFeedCatalogueEntry>>());

        var cut = context.Render<NewsPage>();

        cut.WaitForAssertion(() => Assert.Contains("News is busy. Retrying in a moment.", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".mud-snackbar"));
    }

    // ── Search messages (§7.4) ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task With_no_message_the_helper_is_sr_only_and_describes_the_field()
    {
        await using var context = Context([Article("A")]);
        var cut = context.Render<NewsPage>();
        cut.WaitForAssertion(() => Assert.Single(CardNames(cut)));

        var help = cut.Find($"#{NewsPage.SearchHelpId}");
        Assert.Contains("odc-sr-only", help.ClassList);
        Assert.Equal("Searches titles, authors and summaries, not the full article.", help.TextContent.Trim());
        Assert.Equal(NewsPage.SearchHelpId, SearchInput(cut).GetAttribute("aria-describedby"));
        Assert.Null(SearchInput(cut).GetAttribute("aria-invalid"));
        Assert.Equal("Search titles, authors and summaries", SearchInput(cut).GetAttribute("placeholder"));
    }

    [Fact]
    public async Task A_400_shows_the_message_visibly_sets_aria_invalid_keeps_the_rows_and_resends_nothing()
    {
        await using var context = Context([]);
        var calls = 0;
        SetList(context, f => { calls++; return f.Search is null ? Ok(Page([Article("A")])) : Fail<NewsArticlePage>(HttpStatusCode.BadRequest, NewsErrorCodes.SearchUnusable); });
        var cut = context.Render<NewsPage>();
        cut.WaitForAssertion(() => Assert.Single(CardNames(cut)));

        await Search(cut, "an");

        var expected = $"Use words of at least {Odyssey.Dtos.NewsLimits.MinSearchTermLength} letters.";
        cut.WaitForAssertion(() => Assert.Equal(expected, cut.Find($"#{NewsPage.SearchHelpId}").TextContent.Trim()));
        Assert.DoesNotContain("odc-sr-only", cut.Find($"#{NewsPage.SearchHelpId}").ClassList);
        Assert.Equal("true", SearchInput(cut).GetAttribute("aria-invalid"));
        Assert.Single(CardNames(cut));
        Assert.Contains(expected, cut.Find(".nw-search [role=status]").TextContent, StringComparison.Ordinal);
        await cut.InvokeAsync(() => context.Time.Advance(TimeSpan.FromMinutes(1)));
        Assert.Equal(2, calls);
        Assert.Empty(cut.FindAll(".mud-snackbar"));
    }

    [Fact]
    public async Task A_429_resends_once_after_retry_after_then_shows_the_enter_hint_and_enter_resends()
    {
        await using var context = Context([]);
        var searches = 0;
        SetList(context, f =>
        {
            if (f.Search is null)
            {
                return Ok(Page([Article("A")]));
            }

            searches++;
            return searches <= 2
                ? Fail<NewsArticlePage>(HttpStatusCode.TooManyRequests, NewsErrorCodes.SearchLimited, TimeSpan.FromSeconds(5))
                : Ok(Page([Article("Found")]));
        });
        var cut = context.Render<NewsPage>();
        cut.WaitForAssertion(() => Assert.Single(CardNames(cut)));

        await Search(cut, "election");

        cut.WaitForAssertion(() => Assert.Equal("Too many searches. Searching again in a moment.", cut.Find($"#{NewsPage.SearchHelpId}").TextContent.Trim()));
        Assert.Null(SearchInput(cut).GetAttribute("aria-invalid"));
        Assert.Equal(1, searches);
        await cut.InvokeAsync(() => context.Time.Advance(TimeSpan.FromSeconds(5)));
        cut.WaitForAssertion(() => Assert.Equal(2, searches));
        cut.WaitForAssertion(() => Assert.Equal("Search didn’t run. Press Enter to try again.", cut.Find($"#{NewsPage.SearchHelpId}").TextContent.Trim()));
        await cut.InvokeAsync(() => context.Time.Advance(TimeSpan.FromMinutes(1)));
        Assert.Equal(2, searches);

        cut.Find(".nw-search").KeyUp(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });

        cut.WaitForAssertion(() => Assert.Equal(["Found"], CardNames(cut)));
        Assert.Contains("odc-sr-only", cut.Find($"#{NewsPage.SearchHelpId}").ClassList);
    }

    [Fact]
    public async Task A_busy_search_says_search_is_busy_and_resends_once()
    {
        await using var context = Context([]);
        var searches = 0;
        SetList(context, f => f.Search is null ? Ok(Page([Article("A")])) : ++searches == 1 ? Busy<NewsArticlePage>() : Ok(Page([Article("Found")])));
        var cut = context.Render<NewsPage>();
        cut.WaitForAssertion(() => Assert.Single(CardNames(cut)));

        await Search(cut, "election");

        cut.WaitForAssertion(() => Assert.Equal("Search is busy. Searching again in a moment.", cut.Find($"#{NewsPage.SearchHelpId}").TextContent.Trim()));
        Assert.DoesNotContain("News is busy", cut.Markup, StringComparison.Ordinal);
        await cut.InvokeAsync(() => context.Time.Advance(TimeSpan.FromSeconds(2)));
        cut.WaitForAssertion(() => Assert.Equal(["Found"], CardNames(cut)));
    }

    [Fact]
    public async Task A_changed_query_cancels_the_pending_resend()
    {
        await using var context = Context([]);
        var sent = new List<string?>();
        SetList(context, f =>
        {
            sent.Add(f.Search);
            return f.Search == "first" ? Fail<NewsArticlePage>(HttpStatusCode.TooManyRequests, NewsErrorCodes.SearchLimited, TimeSpan.FromSeconds(5)) : Ok(Page([Article("A")]));
        });
        var cut = context.Render<NewsPage>();
        cut.WaitForAssertion(() => Assert.Single(sent));

        await Search(cut, "first");
        await Search(cut, "second");
        await cut.InvokeAsync(() => context.Time.Advance(TimeSpan.FromSeconds(10)));

        Assert.Equal([null, "first", "second"], sent);
    }
}
