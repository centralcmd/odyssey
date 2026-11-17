using System.Net;
using Bunit;
using Moq;
using Odyssey.ApiClient;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Internet;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Internet;
using Xunit;
using static Odyssey.Client.Tests.NewsPageTestContext;
using static Odyssey.Client.Tests.NewsPageTests;

namespace Odyssey.Client.Tests;

/// <summary>
/// An article card on /news (issue #473 §3.2.5–§3.2.8, §3.2.10; AC 3, 4, 7, 9, 17, 19, 25, 30, 51, 55, 58, 63): mark read
/// on expand with Undo, the ⋯ menu, rollback, Hide read, the expanded body order and its four body states, images,
/// the reading panel, language and publisher text.
/// </summary>
public class NewsArticleCardTests
{
    private const string Hostile = "<img src=x onerror=alert(1)>";

    private static IRenderedComponent<Bunit.Rendering.ContainerFragment> Rendered(NewsPageTestContext context, int cards = 1)
    {
        var cut = context.Render<NewsPage>();
        cut.WaitForAssertion(() => Assert.Equal(cards, CardNames(cut).Count));
        return cut;
    }

    private static string Snackbars(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut) =>
        string.Join(" | ", cut.FindAll(".mud-snackbar").Select(s => s.TextContent.Trim()));

    // ── Mark read on open (AC 3, 19) ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Expanding_an_unread_card_marks_it_read_once_with_undo_and_said_once()
    {
        var article = Article("Story");
        await using var context = Context([article], counts: new NewsUnreadCounts { Total = 5 });
        var cut = Rendered(context);
        Assert.Contains("5 unread", cut.Find(".ph-sub").TextContent, StringComparison.Ordinal);

        Expand(cut, article);

        cut.WaitForAssertion(() => Assert.Contains("Marked as read", Snackbars(cut), StringComparison.Ordinal));
        context.News.Verify(n => n.SetReadAsync(article.Id, true, It.IsAny<CancellationToken>()), Times.Once);
        var namerow = cut.Find($"#nwa-{article.Id} .odc-record-namerow");
        Assert.Equal("Read", namerow.QuerySelector(".odc-sr-only")!.TextContent);
        Assert.DoesNotContain("Unread", namerow.TextContent, StringComparison.Ordinal);
        Assert.Contains("4 unread", cut.Find(".ph-sub").TextContent, StringComparison.Ordinal);
        Assert.Contains("Undo", Snackbars(cut), StringComparison.Ordinal);
        // The snackbar speaks it (a live region in MudBlazor 9), so the announcer does not say it again (AC 29).
        Assert.DoesNotContain("Marked as read", Polite(cut), StringComparison.Ordinal);
        Assert.Equal("true", cut.Find($"#nwa-{article.Id} .odc-record-trigger").GetAttribute("aria-expanded"));
    }

    [Fact]
    public async Task Undo_restores_unread_with_one_call_and_returns_focus_to_the_card()
    {
        var article = Article("Story");
        await using var context = Context([article]);
        var cut = Rendered(context);
        Expand(cut, article);
        cut.WaitForAssertion(() => Assert.Contains("Undo", Snackbars(cut), StringComparison.Ordinal));

        cut.FindAll(".mud-snackbar button").First(b => b.TextContent.Contains("Undo", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => context.News.Verify(n => n.SetReadAsync(article.Id, false, It.IsAny<CancellationToken>()), Times.Once));
        cut.WaitForAssertion(() => Assert.Contains("Unread", cut.Find($"#nwa-{article.Id} .odc-record-namerow").TextContent, StringComparison.Ordinal));
        Assert.Contains(context.Ctx.JSInterop.Invocations, i => i.Identifier == "odsNewsFocusCard" && (string?)i.Arguments[0] == NewsArticleCard.CardId(article.Id));
        cut.WaitForAssertion(() => Assert.Contains("Marked as unread", Polite(cut), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Expanding_a_read_card_sends_nothing()
    {
        var article = Article("Story", a => a.IsRead = true);
        await using var context = Context([article]);
        var cut = Rendered(context);

        Expand(cut, article);

        cut.WaitForAssertion(() => Assert.Contains("Second paragraph.", cut.Markup, StringComparison.Ordinal));
        context.News.Verify(n => n.SetReadAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(cut.FindAll(".mud-snackbar"));
    }

    [Fact]
    public async Task A_reader_without_news_update_marks_nothing_and_sees_only_show_articles_from()
    {
        var article = Article("Story");
        await using var context = Context([article], ReaderOnly);
        var cut = Rendered(context);

        Expand(cut, article);

        cut.WaitForAssertion(() => Assert.Contains("Second paragraph.", cut.Markup, StringComparison.Ordinal));
        context.News.Verify(n => n.SetReadAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(["Show articles from Example News"], MenuLabels(cut, article));
        Assert.Empty(cut.FindAll("#nw-mark-all"));
    }

    [Fact]
    public async Task The_menu_holds_mark_read_tags_and_show_articles_from_and_mark_unread_restores_with_one_call()
    {
        var article = Article("Story", a => { a.IsRead = true; a.ReadAtUtc = Now.UtcDateTime; });
        await using var context = Context([article]);
        var cut = Rendered(context);
        Assert.Equal(["Mark unread", "Tags…", "Show articles from Example News"], MenuLabels(cut, article));

        ClickMenu(cut, article, "Mark unread");

        cut.WaitForAssertion(() => context.News.Verify(n => n.SetReadAsync(article.Id, false, It.IsAny<CancellationToken>()), Times.Once));
        Assert.Contains("Unread", cut.Find($"#nwa-{article.Id} .odc-record-namerow").TextContent, StringComparison.Ordinal);
        cut.WaitForAssertion(() => Assert.Contains("Marked as unread", Polite(cut), StringComparison.Ordinal));
        Assert.Equal(["Mark read", "Tags…", "Show articles from Example News"], MenuLabels(cut, article));
    }

    [Fact]
    public async Task A_failed_write_rolls_back_the_chip_and_the_counts()
    {
        var article = Article("Story");
        await using var context = Context([article], counts: new NewsUnreadCounts { Total = 5 });
        context.News.Setup(n => n.SetReadAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail(HttpStatusCode.InternalServerError));
        var cut = Rendered(context);

        Expand(cut, article);

        cut.WaitForAssertion(() => Assert.Contains(NewsPage.ChangedBackText, Snackbars(cut), StringComparison.Ordinal));
        Assert.Contains("Unread", cut.Find($"#nwa-{article.Id} .odc-record-namerow").TextContent, StringComparison.Ordinal);
        Assert.Contains("5 unread", cut.Find(".ph-sub").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writes_are_serialised_per_article_and_the_last_wins()
    {
        var article = Article("Story");
        var first = new TaskCompletionSource<ApiResult>();
        var calls = new List<bool>();
        await using var context = Context([article]);
        context.News.Setup(n => n.SetReadAsync(article.Id, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, bool isRead, CancellationToken _) =>
            {
                calls.Add(isRead);
                return calls.Count == 1 ? first.Task : Task.FromResult(ApiResult.Success(HttpStatusCode.NoContent));
            });
        var cut = Rendered(context);

        Expand(cut, article);
        cut.WaitForAssertion(() => Assert.Single(calls));
        ClickMenu(cut, article, "Mark unread");
        Assert.Single(calls);
        await cut.InvokeAsync(() => first.SetResult(ApiResult.Success(HttpStatusCode.NoContent)));

        cut.WaitForAssertion(() => Assert.Equal([true, false], calls));
    }

    [Fact]
    public async Task Counts_refresh_a_second_after_the_last_write()
    {
        var article = Article("Story");
        await using var context = Context([article]);
        var cut = Rendered(context);
        Expand(cut, article);
        cut.WaitForAssertion(() => context.News.Verify(n => n.SetReadAsync(article.Id, true, It.IsAny<CancellationToken>()), Times.Once));
        context.News.Invocations.Clear();

        await cut.InvokeAsync(() => context.Time.Advance(NewsPage.CountsDebounce));

        cut.WaitForAssertion(() => context.News.Verify(n => n.GetUnreadCountsAsync(It.IsAny<CancellationToken>()), Times.Once));
    }

    [Fact]
    public async Task A_404_on_the_write_removes_the_card()
    {
        var gone = Article("Gone");
        var next = Article("Next");
        await using var context = Context([gone, next]);
        context.News.Setup(n => n.SetReadAsync(gone.Id, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(Fail(HttpStatusCode.NotFound));
        var cut = Rendered(context, 2);

        Expand(cut, gone);

        cut.WaitForAssertion(() => Assert.Equal(["Next"], CardNames(cut)));
        Assert.Contains(NewsFeedback.GoneText, Snackbars(cut), StringComparison.Ordinal);
        Assert.Contains(context.Ctx.JSInterop.Invocations, i => i.Identifier == "odsNewsFocusCard" && (string?)i.Arguments[0] == NewsArticleCard.CardId(next.Id));
    }

    // ── Hide read (AC 4, 30, 55) ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Hide_read_appears_after_a_read_under_unread_and_is_a_fresh_first_page_with_one_announcement()
    {
        var a = Article("A");
        var b = Article("B");
        var pages = 0;
        await using var context = Context([a, b]);
        context.News.Setup(n => n.ListArticlesAsync(It.IsAny<Odyssey.ApiClient.Resources.NewsArticleListFilter>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++pages == 1 ? Ok(Page([a, b])) : Ok(Page([b], total: 1231)));
        var cut = Rendered(context, 2);
        Assert.DoesNotContain(cut.FindAll(".ph-actions button"), x => x.TextContent.Contains("Hide read", StringComparison.Ordinal));

        Expand(cut, a);
        cut.WaitForAssertion(() => Assert.Contains("Undo", Snackbars(cut), StringComparison.Ordinal));
        // The read card stays where it is under Status = Unread.
        Assert.Equal(["A", "B"], CardNames(cut));

        cut.FindAll(".ph-actions button").Single(x => x.TextContent.Contains("Hide read", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Equal(["B"], CardNames(cut)));
        cut.WaitForAssertion(() => Assert.Contains("1 read article hidden. 1,231 articles", Polite(cut), StringComparison.Ordinal));
        Assert.Contains(context.Ctx.JSInterop.Invocations, i => i.Identifier == "odsNewsFocusListStart");
        // The open Undo is withdrawn: its card has left the screen (AC 30).
        cut.WaitForAssertion(() => Assert.Contains(NewsPage.UndoWithdrawnText, Snackbars(cut), StringComparison.Ordinal));
        Assert.DoesNotContain(cut.FindAll(".mud-snackbar button"), x => x.TextContent.Contains("Undo", StringComparison.Ordinal));
        Assert.DoesNotContain(cut.FindAll(".ph-actions button"), x => x.TextContent.Contains("Hide read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Hide_read_is_not_offered_unless_status_is_exactly_unread()
    {
        var a = Article("A");
        await using var context = Context([a]);
        var cut = Rendered(context);
        await cut.InvokeAsync(() => cut.FindComponents<OdsMultiSelect>().Single(m => m.Instance.Label == "Status").Instance.ValuesChanged.InvokeAsync([]));
        cut.WaitForAssertion(() => Assert.Equal("Status: All", cut.FindComponents<OdsMultiSelect>().Last().Instance.Label));

        Expand(cut, a);

        cut.WaitForAssertion(() => context.News.Verify(n => n.SetReadAsync(a.Id, true, It.IsAny<CancellationToken>()), Times.Once));
        Assert.DoesNotContain(cut.FindAll(".ph-actions button"), x => x.TextContent.Contains("Hide read", StringComparison.Ordinal));
    }

    // ── The expanded card (AC 51, 58) ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_expanded_card_has_no_action_row_no_feed_tile_and_one_plain_reading_panel()
    {
        var article = Article("Story", a => { a.ContentChangedAtUtc = Now.UtcDateTime.AddDays(-5); a.RevisionCount = 2; });
        await using var context = Context([article]);
        var cut = Rendered(context);

        Expand(cut, article);

        cut.WaitForAssertion(() => Assert.Contains("Second paragraph.", cut.Markup, StringComparison.Ordinal));
        var card = cut.Find($"#nwa-{article.Id}");
        var buttons = card.QuerySelectorAll("button").Select(b => b.TextContent.Trim()).ToList();
        Assert.DoesNotContain(buttons, t => t is "Mark read" or "Mark unread" or "Tags…");
        Assert.Equal(["Published", "First seen", "Read", "Original link"], TileLabels(cut));
        var panel = Assert.Single(card.QuerySelectorAll(".nw-reading"));
        Assert.Null(panel.GetAttribute("role"));
        Assert.Null(panel.GetAttribute("tabindex"));
        Assert.Contains("Updated by the publisher on", card.QuerySelector(".mud-alert")!.TextContent, StringComparison.Ordinal);
        Assert.NotNull(card.QuerySelector(".mud-alert time[datetime]"));
        Assert.Contains(buttons, t => t.Contains("Show earlier versions", StringComparison.Ordinal));
        Assert.NotNull(card.QuerySelector("a[href='https://example.org/a'][rel='noopener noreferrer']"));
        // Updated is a text chip in the header; nothing interactive renders inside the expand button.
        Assert.Contains("Updated", card.QuerySelector(".odc-record-namerow")!.TextContent, StringComparison.Ordinal);
        Assert.Empty(card.QuerySelectorAll(".odc-record-trigger button, .odc-record-trigger a, .odc-record-trigger input"));
    }

    private static List<string> TileLabels(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut) =>
        [.. cut.FindComponents<OdsInfoTile>().Select(t => t.Instance.Label ?? string.Empty)];

    [Fact]
    public async Task Own_and_inherited_tags_are_the_header_chips_and_inherited_ones_say_from_feed()
    {
        var own = new ExistingNewsTag { Id = Guid.NewGuid(), Name = "Climate" };
        var inherited = new ExistingNewsTag { Id = Guid.NewGuid(), Name = "Energy" };
        var article = Article("Story", a => { a.TagIds = [own.Id]; a.FeedTagIds = [inherited.Id]; });
        await using var context = Context([article], tags: [own, inherited]);
        var cut = Rendered(context);

        var chips = cut.Find($"#nwa-{article.Id} .odc-record-namerow");
        Assert.Contains("Climate", chips.TextContent, StringComparison.Ordinal);
        var from = chips.QuerySelector(".nw-inherited")!;
        Assert.Contains("Energy", from.TextContent, StringComparison.Ordinal);
        Assert.Contains("(from feed)", from.QuerySelector(".odc-sr-only")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("rss_feed", from.TextContent, StringComparison.Ordinal);
        Assert.Empty(chips.QuerySelectorAll("button"));

        Expand(cut, article);
        cut.WaitForAssertion(() => Assert.Contains("Second paragraph.", cut.Markup, StringComparison.Ordinal));
        // The body never repeats the tags.
        Assert.DoesNotContain("Climate", cut.Find($"#nwa-{article.Id} .odc-record-body, #nwa-{article.Id} .nw-content").TextContent, StringComparison.Ordinal);
    }

    // ── Body states (§3.2.7; AC 17, 63) ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Body_loading_is_a_busy_skeleton_announced_once()
    {
        var article = Article("Story", a => a.IsRead = true);
        var detail = new TaskCompletionSource<ApiResult<NewsArticleDetail>>();
        await using var context = Context([article]);
        context.News.Setup(n => n.GetArticleAsync(article.Id, It.IsAny<CancellationToken>())).Returns(detail.Task);
        var cut = Rendered(context);

        Expand(cut, article);

        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find($"#nwa-{article.Id} .nw-detail-loading").GetAttribute("aria-busy")));
        cut.WaitForAssertion(() => Assert.Contains("Loading article…", Polite(cut), StringComparison.Ordinal));
        await cut.InvokeAsync(() => detail.SetResult(Ok(DetailOf(article))));
        cut.WaitForAssertion(() => Assert.Contains("Second paragraph.", cut.Markup, StringComparison.Ordinal));
        Assert.Equal(2, cut.FindAll($"#nwa-{article.Id} .nw-body p").Count);
        context.News.Verify(n => n.GetArticleAsync(article.Id, It.IsAny<CancellationToken>()), Times.Once);

        // Cached for the card's lifetime: collapse and expand again reads nothing.
        Expand(cut, article);
        Expand(cut, article);
        context.News.Verify(n => n.GetArticleAsync(article.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Body_error_offers_retry_and_a_retry_loads_the_body()
    {
        var article = Article("Story", a => a.IsRead = true);
        var fail = true;
        await using var context = Context([article]);
        context.News.Setup(n => n.GetArticleAsync(article.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => fail ? Fail<NewsArticleDetail>(HttpStatusCode.InternalServerError) : Ok(DetailOf(article)));
        var cut = Rendered(context);

        Expand(cut, article);

        cut.WaitForAssertion(() => Assert.Contains(NewsArticleCard.ErrorText, cut.Find($"#nwa-{article.Id} [role=alert]").TextContent, StringComparison.Ordinal));
        fail = false;
        cut.Find($"#{NewsArticleCard.RetryId(article.Id)}").Click();
        cut.WaitForAssertion(() => Assert.Contains("Second paragraph.", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Body_storage_unavailable_has_its_own_words_and_retry_waits_out_retry_after()
    {
        var article = Article("Story", a => a.IsRead = true);
        var fail = true;
        await using var context = Context([article]);
        context.News.Setup(n => n.GetArticleAsync(article.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => fail
                ? Fail<NewsArticleDetail>(HttpStatusCode.ServiceUnavailable, NewsErrorCodes.StorageUnavailable, TimeSpan.FromSeconds(30))
                : Ok(DetailOf(article)));
        var cut = Rendered(context);

        Expand(cut, article);

        cut.WaitForAssertion(() => Assert.Contains(NewsArticleCard.StorageText, cut.Find($"#nwa-{article.Id} [role=alert]").TextContent, StringComparison.Ordinal));
        Assert.Contains("Available in 30 s", cut.Find($"#nwa-{article.Id}").TextContent, StringComparison.Ordinal);
        Assert.Null(cut.Find($"#{NewsArticleCard.RetryId(article.Id)}").GetAttribute("disabled"));
        fail = false;

        cut.Find($"#{NewsArticleCard.RetryId(article.Id)}").Click();

        cut.WaitForAssertion(() => Assert.Contains("Retrying in 30 seconds", Polite(cut), StringComparison.Ordinal));
        Assert.Equal("true", cut.Find($"#{NewsArticleCard.RetryId(article.Id)}").GetAttribute("aria-disabled"));
        context.News.Verify(n => n.GetArticleAsync(article.Id, It.IsAny<CancellationToken>()), Times.Once);
        await cut.InvokeAsync(() => context.Time.Advance(TimeSpan.FromSeconds(30)));
        cut.WaitForAssertion(() => Assert.Contains("Second paragraph.", cut.Markup, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Body_unavailable_is_an_info_status_whose_admin_sentence_needs_system_settings_read(bool admin)
    {
        var article = Article("Story");
        string[] claims = admin ? [.. AllNewsClaims, PermissionClaims.SystemSettingsRead] : AllNewsClaims;
        await using var context = Context([article], claims);
        context.News.Setup(n => n.GetArticleAsync(article.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(DetailOf(article, d => { d.Body = null; d.BodyUnavailable = true; })));
        var cut = Rendered(context);

        Expand(cut, article);

        cut.WaitForAssertion(() => Assert.Contains(NewsArticleCard.UnavailableText, cut.Find($"#nwa-{article.Id} .nw-reading [role=status]").TextContent, StringComparison.Ordinal));
        Assert.Equal(admin, cut.FindAll($"#nwa-{article.Id} a[href='background-jobs']").Count == 1);
        cut.WaitForAssertion(() => Assert.Contains(NewsArticleCard.UnavailableText, Polite(cut), StringComparison.Ordinal));
        // The read state still applies.
        context.News.Verify(n => n.SetReadAsync(article.Id, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(NewsBodyOmittedReason.TooLarge)]
    [InlineData(NewsBodyOmittedReason.TooComplex)]
    public async Task Body_omitted_shows_the_alert_and_the_original_link_and_still_the_carousel(NewsBodyOmittedReason reason)
    {
        var article = Article("Story", a => a.IsRead = true);
        await using var context = Context([article]);
        context.News.Setup(n => n.GetArticleAsync(article.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(DetailOf(article, d =>
            {
                d.Body = null;
                d.BodyOmittedReason = reason;
                d.Images = [new() { Id = Guid.NewGuid(), AltText = "A", Width = 800, Height = 600 }, new() { Id = Guid.NewGuid(), DisplayOrder = 1 }];
            })));
        var cut = Rendered(context);

        Expand(cut, article);

        cut.WaitForAssertion(() => Assert.Contains(NewsArticleCard.OmittedText, cut.Find($"#nwa-{article.Id} .nw-reading [role=status]").TextContent, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll($"#nwa-{article.Id} .nw-body"));
        Assert.NotNull(cut.Find($"#nwa-{article.Id} .nw-reading a[href='https://example.org/a']"));
        Assert.Contains("(opens in a new tab)", cut.Find($"#nwa-{article.Id} .nw-reading a").TextContent, StringComparison.Ordinal);
        Assert.NotNull(cut.Find($"#nwa-{article.Id} .nw-reading [aria-roledescription=carousel]"));
        cut.WaitForAssertion(() => Assert.Contains(NewsArticleCard.OmittedText, Polite(cut), StringComparison.Ordinal));
        Assert.Single(Polite(cut).Split(" | "), t => t.Contains(NewsArticleCard.OmittedText, StringComparison.Ordinal));
    }

    // ── Images (§6.1; AC 9) ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Images_load_from_the_image_endpoint_and_a_failed_one_reads_image_unavailable()
    {
        var article = Article("Story", a => { a.IsRead = true; a.ImageCount = 2; });
        var first = Guid.NewGuid();
        await using var context = Context([article]);
        context.News.Setup(n => n.GetArticleAsync(article.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(DetailOf(article, d => d.Images = [new() { Id = first, AltText = "Harbour", Width = 1600, Height = 900 }, new() { Id = Guid.NewGuid(), DisplayOrder = 1 }])));
        var cut = Rendered(context);

        Expand(cut, article);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll($"#nwa-{article.Id} .nw-reading img")));
        Assert.Contains("has-images", cut.Find($"#nwa-{article.Id} .nw-reading").ClassList);
        var img = cut.Find($"#nwa-{article.Id} .nw-reading img[alt='Harbour']");
        Assert.Equal($"http://api.test/api/news-articles/{article.Id}/images/{first}/content", img.GetAttribute("src"));
        Assert.Equal("Images for “Story”", cut.Find($"#nwa-{article.Id} [aria-roledescription=carousel]").GetAttribute("aria-label"));
        Assert.Empty(cut.FindAll($"#nwa-{article.Id} a[href*='/images/']"));

        img.TriggerEvent("onerror", EventArgs.Empty);

        cut.WaitForAssertion(() => Assert.Contains("Image unavailable", cut.Find($"#nwa-{article.Id} .nw-reading").TextContent, StringComparison.Ordinal));
    }

    // ── Language and publisher text (AC 7, 25) ─────────────────────────────────────────────────────

    [Fact]
    public async Task The_article_language_reaches_the_title_body_and_alt_text()
    {
        var article = Article("Nyhet", a => { a.Language = "nb"; a.IsRead = true; });
        await using var context = Context([article]);
        context.News.Setup(n => n.GetArticleAsync(article.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(DetailOf(article, d => d.Images = [new() { Id = Guid.NewGuid(), AltText = "Havn" }])));
        var cut = Rendered(context);

        Assert.Equal("nb", cut.Find($"#nwa-{article.Id} .odc-record-name").GetAttribute("lang"));
        Expand(cut, article);
        cut.WaitForAssertion(() => Assert.Equal("nb", cut.Find($"#nwa-{article.Id} .nw-body").GetAttribute("lang")));
        Assert.NotNull(cut.Find($"#nwa-{article.Id} [lang='nb'] img, #nwa-{article.Id} img[lang='nb']"));
    }

    [Fact]
    public async Task A_missing_language_falls_back_to_the_feed_and_a_blank_one_sets_none()
    {
        var withFeed = Article("A", a => a.Language = null);
        var other = Guid.NewGuid();
        var blank = Article("B", a => { a.Language = " "; a.FeedId = other; });
        await using var context = Context([withFeed, blank], catalogue:
            [CatalogueFeed("Example News", f => f.Language = "sv", id: FeedId), CatalogueFeed("Other", id: other)]);
        var cut = Rendered(context, 2);

        Assert.Equal("sv", cut.Find($"#nwa-{withFeed.Id} .odc-record-name").GetAttribute("lang"));
        Assert.Null(cut.Find($"#nwa-{blank.Id} .odc-record-name").GetAttribute("lang"));
    }

    [Fact]
    public async Task Hostile_publisher_text_renders_as_visible_text_and_creates_no_element()
    {
        var article = Article(Hostile, a =>
        {
            a.Author = Hostile;
            a.FeedTitle = Hostile;
            a.Excerpt = Hostile;
            a.Link = "javascript:alert(1)";
            a.IsRead = true;
        });
        await using var context = Context([article], tags: [new() { Id = Guid.NewGuid(), Name = Hostile }]);
        context.News.Setup(n => n.GetArticleAsync(article.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(DetailOf(article, d => { d.Body = $"{Hostile}\n\n{Hostile}"; d.Images = [new() { Id = Guid.NewGuid(), AltText = Hostile }]; })));
        var cut = Rendered(context);

        Expand(cut, article);

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll($"#nwa-{article.Id} .nw-body p").Count));
        Assert.Contains(Hostile, cut.Find(".odc-record-name").TextContent, StringComparison.Ordinal);
        Assert.Contains(Hostile, cut.Find(".nw-body").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("img[src='x']"));
        Assert.Empty(cut.FindAll("a[href^='javascript']"));
        Assert.Empty(cut.FindAll("[onerror]"));
    }
}
