using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Odyssey.ApiClient;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Internet;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Internet;
using Xunit;
using static Odyssey.Client.Tests.NewsPageTestContext;
using static Odyssey.Client.Tests.NewsPageTests;

namespace Odyssey.Client.Tests;

/// <summary>
/// The /news header — sub-line, Overview and Attention (issue #473 §3.2.1, §3.2.2; AC 57, 62) — and its two article
/// dialogs: Tags (§3.2.11; AC 12) and Earlier versions (§3.5; AC 11, 42, 63).
/// </summary>
public class NewsPageHeaderAndDialogsTests
{
    private static IRenderedComponent<Bunit.Rendering.ContainerFragment> Rendered(NewsPageTestContext context)
    {
        var cut = context.Render<NewsPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(CardNames(cut)));
        return cut;
    }

    // ── Header (AC 57) ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_sub_line_and_overview_tiles_come_from_the_counts_and_the_catalogue()
    {
        await using var context = Context([Article("A")], catalogue:
        [
            CatalogueFeed("Example News", f => { f.UnreadCount = 4; f.ArticleCount = 10; }, FeedId),
            CatalogueFeed("Broken", f => { f.Health = NewsFeedHealth.Gone; f.UnreadCount = 1; f.ArticleCount = 2; }),
            CatalogueFeed("Resting", f => { f.State = NewsFeedState.Paused; f.Health = NewsFeedHealth.Paused; f.UnreadCount = 0; f.ArticleCount = 3; }),
            CatalogueFeed("Old", f => { f.State = NewsFeedState.Archived; f.Health = NewsFeedHealth.Archived; f.UnreadCount = 9; f.ArticleCount = 9; }),
        ], counts: new NewsUnreadCounts { Total = 42 });

        var cut = Rendered(context);

        Assert.Equal("42 unread · 3 feeds", cut.Find(".ph-sub").TextContent.Trim());
        var articles = cut.FindComponents<OdsBreakdownTile>().Single(t => t.Instance.Label == "Articles").Instance.Rows;
        Assert.Equal([("Unread", 5), ("Read", 10), ("Updated", 0)], articles.Select(r => (r.Label, (int)r.Count)));
        var feeds = cut.FindComponents<OdsBreakdownTile>().Single(t => t.Instance.Label == "Feeds").Instance.Rows;
        Assert.Equal([("Healthy or pending", 1), ("Need attention", 1), ("Paused", 1), ("Archived", 1)], feeds.Select(r => (r.Label, (int)r.Count)));
    }

    [Fact]
    public async Task By_feed_lists_every_active_feed_by_its_stored_articles_most_first_and_totals_them()
    {
        await using var context = Context([Article("A")], catalogue:
        [
            CatalogueFeed("Example News", f => f.ArticleCount = 10, FeedId),
            CatalogueFeed("Alpha", f => f.ArticleCount = 25),
            CatalogueFeed("Beta", f => f.ArticleCount = 10),
            CatalogueFeed("Broken", f => { f.Health = NewsFeedHealth.Gone; f.ArticleCount = 2; }),
            CatalogueFeed("Resting", f => { f.State = NewsFeedState.Paused; f.Health = NewsFeedHealth.Paused; f.ArticleCount = 30; }),
            CatalogueFeed("Old", f => { f.State = NewsFeedState.Archived; f.Health = NewsFeedHealth.Archived; f.ArticleCount = 40; }),
        ]);

        var cut = Rendered(context);

        var tile = cut.FindComponents<OdsBreakdownTile>().Single(t => t.Instance.Label == "By feed");
        Assert.Equal([("Alpha", 25), ("Beta", 10), ("Example News", 10), ("Broken", 2)], tile.Instance.Rows.Select(r => (r.Label, (int)r.Count)));
        Assert.Contains("47", tile.Find(".odc-breakdown-total").TextContent, StringComparison.Ordinal);
        Assert.Contains("Articles", tile.Find(".odc-breakdown-total").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task By_feed_says_so_when_no_feed_is_active()
    {
        await using var context = Context([Article("A")], catalogue:
        [
            CatalogueFeed("Resting", f => { f.State = NewsFeedState.Paused; f.Health = NewsFeedHealth.Paused; f.ArticleCount = 3; }, FeedId),
        ]);

        var cut = Rendered(context);

        var tile = cut.FindComponents<OdsBreakdownTile>().Single(t => t.Instance.Label == "By feed");
        Assert.Equal("No active feeds.", tile.Find(".odc-breakdown-empty").TextContent.Trim());
    }

    [Fact]
    public async Task Attention_lists_exactly_the_feeds_that_need_it_with_their_severity_and_a_review_link()
    {
        await using var context = Context([Article("A")], catalogue:
        [
            CatalogueFeed("Example News", id: FeedId),
            CatalogueFeed("Gone feed", f => f.Health = NewsFeedHealth.Gone),
            CatalogueFeed("Failing feed", f => f.Health = NewsFeedHealth.Failing),
            CatalogueFeed("Quarantined feed", f => f.Health = NewsFeedHealth.Quarantined),
            CatalogueFeed("Full feed", f => f.Health = NewsFeedHealth.QuotaReached),
            CatalogueFeed("Paused feed", f => { f.Health = NewsFeedHealth.Paused; f.State = NewsFeedState.Paused; }),
            CatalogueFeed("Pending feed", f => f.Health = NewsFeedHealth.Pending),
        ]);
        var cut = Rendered(context);

        var problems = cut.FindComponent<PageHeader>().Instance.Problems!;
        Assert.Equal("Attention", cut.FindComponent<PageHeader>().Instance.ProblemsLabel);
        Assert.Equal(
            [("Failing feed", PageHeaderSeverity.Warning), ("Full feed", PageHeaderSeverity.Warning), ("Gone feed", PageHeaderSeverity.Error), ("Quarantined feed", PageHeaderSeverity.Error)],
            problems.Select(p => (p.Lead!, p.Severity)));
        Assert.All(problems, p => Assert.Equal($"Review {p.Lead}", p.ActionAriaLabel));
        Assert.Contains("Attention", cut.Find(".ph-signal-btn").TextContent, StringComparison.Ordinal);
        Assert.Contains("Gone feed.", cut.Markup, StringComparison.Ordinal);
        Assert.All(cut.FindAll(".alert-fix span"), s => Assert.Equal("true", s.GetAttribute("aria-hidden")));

        var gone = problems.Single(p => p.Lead == "Gone feed");
        await cut.InvokeAsync(() => gone.OnView.InvokeAsync());
        var nav = context.Ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        Assert.Contains("news-feeds?state=All&reveal=", nav.Uri, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false, 0, false)]
    [InlineData(true, false, 2, false)]
    [InlineData(true, true, 2, true)]
    public async Task Quota_rows_need_file_storage_read_and_their_settings_link_system_settings_read(bool fileStorage, bool settings, int rows, bool action)
    {
        var claims = new List<string>(AllNewsClaims);
        if (fileStorage)
        {
            claims.Add(PermissionClaims.FileStorageRead);
        }

        if (settings)
        {
            claims.Add(PermissionClaims.SystemSettingsRead);
        }

        await using var context = Context([Article("A")], claims);
        context.News.Setup(n => n.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(new NewsStatus { ImageQuotaReached = true, TextAndSourceQuotaReached = true }));
        var cut = Rendered(context);

        var problems = cut.FindComponent<PageHeader>().Instance.Problems ?? [];
        Assert.Equal(rows, problems.Count);
        Assert.All(problems, p => Assert.Equal(action, p.OnView.HasDelegate));
        if (action)
        {
            Assert.Equal(["Open Settings for the image quota", "Open Settings for the text quota"], problems.Select(p => p.ActionAriaLabel));
        }

        context.News.Verify(n => n.GetStatusAsync(It.IsAny<CancellationToken>()), fileStorage ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task The_news_actions_menu_holds_mark_all_read_and_manage_feeds()
    {
        await using var context = Context([Article("A")]);
        var cut = Rendered(context);

        Assert.Equal("News actions", cut.Find($"#{NewsPage.ActionMenuId} button").GetAttribute("aria-label"));
        Assert.Equal(["Mark all read…", "Manage feeds"], NewsActions(cut));
        // There is no Hide read: the Status filter does that.
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Hide read", StringComparison.Ordinal));
    }

    // ── Tags dialog (AC 12) ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_tags_dialog_saves_own_tags_with_one_put_and_shows_inherited_ones_read_only()
    {
        var keep = new ExistingNewsTag { NewsTagId = Guid.NewGuid(), Name = "Keep" };
        var drop = new ExistingNewsTag { NewsTagId = Guid.NewGuid(), Name = "Drop" };
        var inherited = new ExistingNewsTag { NewsTagId = Guid.NewGuid(), Name = "From feed" };
        var article = Article("Story", a => { a.TagIds = [keep.NewsTagId, drop.NewsTagId]; a.FeedTagIds = [inherited.NewsTagId]; });
        IReadOnlyCollection<Guid>? sent = null;
        await using var context = Context([article], tags: [keep, drop, inherited]);
        context.News.Setup(n => n.SetArticleTagsAsync(article.Id, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, IReadOnlyCollection<Guid> ids, CancellationToken _) => sent = ids)
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.NoContent));
        var cut = Rendered(context);

        ClickMenu(cut, article, "Tags…");

        cut.WaitForAssertion(() => Assert.Contains("Tags for “Story”", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("From the feed — change them on the Feeds page.", cut.Markup, StringComparison.Ordinal);
        var readOnly = cut.Find("ul[aria-label='Tags from the feed']");
        Assert.Contains("(from feed)", readOnly.TextContent, StringComparison.Ordinal);
        Assert.Empty(readOnly.QuerySelectorAll("button"));

        await cut.InvokeAsync(() => cut.FindComponent<OdsTagMultiSelect>().Instance.ValueChanged.InvokeAsync([keep.NewsTagId.ToString()]));
        cut.FindAll(".ods-modal button").Single(b => b.TextContent.Contains("Save", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Equal([keep.NewsTagId], sent));
        context.News.Verify(n => n.RemoveArticleTagAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        context.News.Verify(n => n.AddArticleTagAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        cut.WaitForAssertion(() => Assert.DoesNotContain("Drop", cut.Find($"#nwa-{article.Id} .odc-record-namerow").TextContent, StringComparison.Ordinal));
        cut.WaitForAssertion(() => Assert.Contains("Tags saved for Story", Polite(cut), StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_archived_tag_refusal_is_inline_on_the_picker()
    {
        var tag = new ExistingNewsTag { NewsTagId = Guid.NewGuid(), Name = "Climate" };
        var article = Article("Story");
        await using var context = Context([article], tags: [tag]);
        context.News.Setup(n => n.SetArticleTagsAsync(article.Id, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail(HttpStatusCode.UnprocessableEntity, NewsErrorCodes.TagNotUsable));
        var cut = Rendered(context);
        ClickMenu(cut, article, "Tags…");
        cut.WaitForAssertion(() => Assert.Single(cut.FindComponents<OdsTagMultiSelect>()));

        await cut.InvokeAsync(() => cut.FindComponent<OdsTagMultiSelect>().Instance.ValueChanged.InvokeAsync([tag.NewsTagId.ToString()]));
        cut.FindAll(".ods-modal button").Single(b => b.TextContent.Contains("Save", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains("One of these tags is archived.", cut.Markup, StringComparison.Ordinal));
    }

    // ── Earlier versions (AC 11, 42, 63) ───────────────────────────────────────────────────────────

    private static List<NewsArticleRevisionSummary> Revisions() =>
    [
        new() { RevisionNumber = 1, Title = "Original title", ValidFromUtc = new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc), ReplacedAtUtc = new DateTime(2026, 11, 11, 9, 0, 0, DateTimeKind.Utc) },
        new() { RevisionNumber = 3, Title = "Third", ValidFromUtc = new DateTime(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc), ReplacedAtUtc = new DateTime(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc) },
        new() { RevisionNumber = 2, Title = "Second", ValidFromUtc = new DateTime(2026, 11, 11, 9, 0, 0, DateTimeKind.Utc), ReplacedAtUtc = new DateTime(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc) },
    ];

    private static NewsArticleRevisionDetail RevisionBody(int number, Action<NewsArticleRevisionDetail>? edit = null)
    {
        var detail = new NewsArticleRevisionDetail { RevisionNumber = number, Title = $"Title {number}", Body = $"Body of version {number}." };
        edit?.Invoke(detail);
        return detail;
    }

    private static async Task<IRenderedComponent<Bunit.Rendering.ContainerFragment>> OpenVersions(NewsPageTestContext context, NewsArticleSummary article)
    {
        var cut = Rendered(context);
        Expand(cut, article);
        cut.WaitForAssertion(() => Assert.Contains("Show earlier versions", cut.Find($"#nwa-{article.Id}").TextContent, StringComparison.Ordinal));
        cut.FindAll($"#nwa-{article.Id} button").Single(b => b.TextContent.Contains("Show earlier versions", StringComparison.Ordinal)).Click();
        await Task.CompletedTask;
        return cut;
    }

    private static NewsArticleSummary Edited() =>
        Article("Story", a => { a.IsRead = true; a.ContentChangedAtUtc = Now.UtcDateTime.AddDays(-1); a.RevisionCount = 3; a.Language = "nb"; });

    [Fact]
    public async Task Versions_are_listed_newest_first_from_metadata_and_only_the_selected_body_loads_then_is_announced()
    {
        var article = Edited();
        await using var context = Context([article]);
        context.News.Setup(n => n.ListRevisionsAsync(article.Id, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(Revisions()));
        context.News.Setup(n => n.GetRevisionAsync(article.Id, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, int n, CancellationToken _) => Ok(RevisionBody(n)));
        var cut = await OpenVersions(context, article);

        cut.WaitForAssertion(() => Assert.Contains("Body of version 3.", cut.Markup, StringComparison.Ordinal));
        Assert.Contains($"3 earlier versions. Up to {NewsLimits.MaxRevisions} are kept, and the original is always kept.", cut.Markup, StringComparison.Ordinal);
        var options = cut.FindComponent<NewsVersionsDialog>().Instance.Options;
        Assert.Equal(["Version 3 · replaced 13 Nov 2026", "Version 2 · replaced 12 Nov 2026", "Original · first fetched 10 Nov 2026"], options.Select(o => o.Label));
        Assert.Equal("Version", cut.FindComponents<OdsSelect>().Last().Instance.Label);
        context.News.Verify(n => n.GetRevisionAsync(article.Id, 3, It.IsAny<CancellationToken>()), Times.Once);
        context.News.Verify(n => n.GetRevisionAsync(article.Id, It.IsNotIn(3), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal("nb", cut.Find("section.nw-version").GetAttribute("lang"));
        cut.WaitForAssertion(() => Assert.Contains("Showing version 3", Polite(cut), StringComparison.Ordinal));

        await cut.InvokeAsync(() => cut.FindComponents<OdsSelect>().Last().Instance.ValueChanged.InvokeAsync("1"));

        cut.WaitForAssertion(() => Assert.Contains("Body of version 1.", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("Body of version 3.", cut.Markup, StringComparison.Ordinal);
        cut.WaitForAssertion(() => Assert.Contains("Showing version 1", Polite(cut), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_late_response_for_a_version_no_longer_selected_is_discarded_and_not_announced()
    {
        var article = Edited();
        var slow = new TaskCompletionSource<ApiResult<NewsArticleRevisionDetail>>();
        await using var context = Context([article]);
        context.News.Setup(n => n.ListRevisionsAsync(article.Id, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(Revisions()));
        context.News.Setup(n => n.GetRevisionAsync(article.Id, 3, It.IsAny<CancellationToken>())).Returns(slow.Task);
        context.News.Setup(n => n.GetRevisionAsync(article.Id, 2, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(RevisionBody(2)));
        var cut = await OpenVersions(context, article);
        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("section.nw-version").GetAttribute("aria-busy")));
        Assert.DoesNotContain("Showing version", Polite(cut), StringComparison.Ordinal);

        await cut.InvokeAsync(() => cut.FindComponents<OdsSelect>().Last().Instance.ValueChanged.InvokeAsync("2"));
        cut.WaitForAssertion(() => Assert.Contains("Body of version 2.", cut.Markup, StringComparison.Ordinal));
        await cut.InvokeAsync(() => slow.SetResult(Ok(RevisionBody(3))));

        Assert.DoesNotContain("Body of version 3.", cut.Markup, StringComparison.Ordinal);
        cut.WaitForAssertion(() => Assert.Contains("Showing version 2", Polite(cut), StringComparison.Ordinal));
        Assert.DoesNotContain("Showing version 3", Polite(cut), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_version_offers_retry_and_a_successful_retry_focuses_the_panel_heading()
    {
        var article = Edited();
        var fail = true;
        await using var context = Context([article]);
        context.News.Setup(n => n.ListRevisionsAsync(article.Id, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(Revisions()));
        context.News.Setup(n => n.GetRevisionAsync(article.Id, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, int n, CancellationToken _) => fail
                ? Fail<NewsArticleRevisionDetail>(HttpStatusCode.ServiceUnavailable, NewsErrorCodes.StorageUnavailable, TimeSpan.Zero)
                : Ok(RevisionBody(n)));
        var cut = await OpenVersions(context, article);

        cut.WaitForAssertion(() => Assert.Contains("Couldn’t load this version.", cut.Find("section.nw-version [role=alert]").TextContent, StringComparison.Ordinal));
        fail = false;
        cut.Find("#nw-version-retry").Click();

        cut.WaitForAssertion(() => Assert.Contains("Body of version 3.", cut.Markup, StringComparison.Ordinal));
        Assert.Contains(context.Ctx.JSInterop.Invocations, i => i.Identifier == "odsFocusById" && (string?)i.Arguments[0] == NewsVersionsDialog.HeadingId);
        Assert.Equal("-1", cut.Find($"#{NewsVersionsDialog.HeadingId}").GetAttribute("tabindex"));
    }

    [Fact]
    public async Task An_omitted_version_shows_the_omitted_alert_and_announces_it_in_place_of_showing_version()
    {
        var article = Edited();
        await using var context = Context([article]);
        context.News.Setup(n => n.ListRevisionsAsync(article.Id, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(Revisions()));
        context.News.Setup(n => n.GetRevisionAsync(article.Id, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, int n, CancellationToken _) => Ok(RevisionBody(n, d => { d.Body = null; d.BodyOmittedReason = NewsBodyOmittedReason.TooLarge; })));
        var cut = await OpenVersions(context, article);

        cut.WaitForAssertion(() => Assert.Contains(NewsArticleCard.OmittedText, cut.Find("section.nw-version [role=status]").TextContent, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("section.nw-version .nw-body"));
        cut.WaitForAssertion(() => Assert.Contains(NewsArticleCard.OmittedText, Polite(cut), StringComparison.Ordinal));
        Assert.DoesNotContain("Showing version", Polite(cut), StringComparison.Ordinal);
    }
}
