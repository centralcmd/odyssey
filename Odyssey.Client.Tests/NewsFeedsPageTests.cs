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

namespace Odyssey.Client.Tests;

/// <summary>
/// <c>/news-feeds</c> (issue #473 §3.3; AC 13, 14, 15, 17, 18, 32, 46, 59, 60, 65): the full set in pages of
/// <see cref="NewsLimits.MaxPageSize"/>, the client-side filter bar with Status defaulting to Active, the overview,
/// the health chip and quota reason, the management block only with <c>news-feeds.update</c>, the claim- and
/// state-gated menu, the state-swap announcements, the create flow's poll, delete and Use new address.
/// </summary>
public class NewsFeedsPageTests
{
    private static readonly string[] ReaderOnly = [PermissionClaims.NewsRead];

    internal static ExistingNewsFeed Feed(string title, Action<ExistingNewsFeed>? edit = null, bool managed = true)
    {
        var feed = new ExistingNewsFeed
        {
            Id = Guid.NewGuid(), Version = Guid.NewGuid(), DisplayTitle = title, Title = title, SiteUrl = "https://site.example/",
            EffectiveIntervalMinutes = 60, DownloadImages = true, IsActive = true, CreatedAtUtc = Now.UtcDateTime,
            LastCheckedAtUtc = Now.UtcDateTime.AddMinutes(-12), LastSuccessAtUtc = Now.UtcDateTime.AddMinutes(-12),
            Health = NewsFeedHealth.Healthy, UnreadCount = 4, ArticleCount = 311,
            Management = managed ? new NewsFeedManagement { Url = "https://fetch.example/feed.xml?token=s3cret" } : null,
        };
        edit?.Invoke(feed);
        return feed;
    }

    private static PagedResult<ExistingNewsFeed> Page(IEnumerable<ExistingNewsFeed> feeds)
    {
        var items = feeds.ToList();
        return new PagedResult<ExistingNewsFeed> { Items = items, TotalCount = items.Count };
    }

    private static NewsPageTestContext Context(IReadOnlyList<ExistingNewsFeed> feeds, IReadOnlyList<string>? claims = null, List<ExistingNewsTag>? tags = null)
    {
        var context = new NewsPageTestContext(claims);
        context.News.Setup(n => n.ListFeedsAsync(1, NewsLimits.MaxPageSize, null, null, NewsFeedState.All, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Page(feeds)));
        context.News.Setup(n => n.ListTagsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Ok(tags ?? []));
        return context;
    }

    private static List<string> CardNames(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut) =>
        [.. cut.FindAll(".odc-record-name").Select(e => e.TextContent.Trim())];

    private static void Expand(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, ExistingNewsFeed feed) =>
        cut.Find($"#nf-{feed.Id} .odc-record-trigger").Click();

    private static List<string> MenuLabels(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, ExistingNewsFeed feed)
    {
        cut.Find($"#nf-{feed.Id} .odc-record-ctl button[aria-label='Actions for {feed.DisplayTitle}']").Click();
        cut.WaitForElement(".mud-menu-item");
        var labels = cut.FindAll(".mud-menu-item .odc-menu-item-body > span:first-child").Select(e => e.TextContent.Trim()).Where(t => t.Length > 0).ToList();
        cut.Find($"#nf-{feed.Id} .odc-record-ctl button[aria-label='Actions for {feed.DisplayTitle}']").Click();
        // The close renders asynchronously: without this wait, the NEXT call can read this menu's items
        // still in the DOM alongside its own (seen on a loaded CI runner as a list with a stale tail).
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".mud-menu-item")));
        return labels;
    }

    private static void ClickMenu(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, ExistingNewsFeed feed, string label)
    {
        cut.Find($"#nf-{feed.Id} .odc-record-ctl button[aria-label='Actions for {feed.DisplayTitle}']").Click();
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll(".mud-menu-item"), i => i.TextContent.Contains(label, StringComparison.Ordinal)));
        cut.FindAll(".mud-menu-item").First(i => i.TextContent.Contains(label, StringComparison.Ordinal)).Click();
    }

    private static string Polite(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut) =>
        string.Concat(cut.FindAll("[role=status][aria-live=polite]").Select(e => e.TextContent));

    // ── Loading ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Loads_the_full_set_in_pages_of_the_max_size_with_state_all_and_drops_duplicates()
    {
        var first = Enumerable.Range(0, NewsLimits.MaxPageSize).Select(i => Feed($"Feed {i:000}")).ToList();
        var second = new List<ExistingNewsFeed> { first[0], Feed("Last") };
        await using var context = new NewsPageTestContext();
        context.News.Setup(n => n.ListFeedsAsync(It.IsAny<int>(), NewsLimits.MaxPageSize, null, null, NewsFeedState.All, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((int page, int _, string? _, Guid? _, NewsFeedState? _, IReadOnlyCollection<NewsFeedHealth>? _, NewsFeedSortBy? _, bool? _, CancellationToken _) =>
                Ok(Page(page == 1 ? first : second)));
        context.News.Setup(n => n.ListTagsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new List<ExistingNewsTag>()));

        var cut = context.Render<NewsFeedsPage>();

        cut.WaitForAssertion(() => Assert.Contains($"{(NewsLimits.MaxPageSize + 1).ToString("N0")} feeds", cut.Markup, StringComparison.Ordinal));
        context.News.Verify(n => n.ListFeedsAsync(It.IsAny<int>(), It.IsAny<int>(), null, null, NewsFeedState.All, null, null, null, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Stops_after_five_requests()
    {
        await using var context = new NewsPageTestContext();
        context.News.Setup(n => n.ListFeedsAsync(It.IsAny<int>(), NewsLimits.MaxPageSize, null, null, NewsFeedState.All, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Ok(Page(Enumerable.Range(0, NewsLimits.MaxPageSize).Select(i => Feed($"F{i}")))));
        context.News.Setup(n => n.ListTagsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new List<ExistingNewsTag>()));

        var cut = context.Render<NewsFeedsPage>();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".nw-loading")));
        context.News.Verify(n => n.ListFeedsAsync(It.IsAny<int>(), It.IsAny<int>(), null, null, NewsFeedState.All, null, null, null, It.IsAny<CancellationToken>()),
            Times.Exactly(NewsFeedsPage.MaxListRequests));
    }

    [Fact]
    public async Task A_failed_page_discards_the_partial_set()
    {
        await using var context = new NewsPageTestContext();
        context.News.Setup(n => n.ListFeedsAsync(It.IsAny<int>(), NewsLimits.MaxPageSize, null, null, NewsFeedState.All, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((int page, int _, string? _, Guid? _, NewsFeedState? _, IReadOnlyCollection<NewsFeedHealth>? _, NewsFeedSortBy? _, bool? _, CancellationToken _) =>
                page == 1 ? Ok(Page(Enumerable.Range(0, NewsLimits.MaxPageSize).Select(i => Feed($"F{i}")))) : Fail<PagedResult<ExistingNewsFeed>>(HttpStatusCode.InternalServerError));
        context.News.Setup(n => n.ListTagsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new List<ExistingNewsTag>()));

        var cut = context.Render<NewsFeedsPage>();

        cut.WaitForAssertion(() => Assert.Contains("Couldn’t load feeds.", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(CardNames(cut));
        Assert.Single(cut.FindAll("#nf-load-retry"));
    }

    [Fact]
    public async Task A_busy_first_load_shows_the_region_state_without_a_snackbar()
    {
        await using var context = new NewsPageTestContext();
        context.News.Setup(n => n.ListFeedsAsync(It.IsAny<int>(), It.IsAny<int>(), null, null, NewsFeedState.All, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Busy<PagedResult<ExistingNewsFeed>>());
        context.News.Setup(n => n.ListTagsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new List<ExistingNewsTag>()));

        var cut = context.Render<NewsFeedsPage>();

        cut.WaitForAssertion(() => Assert.Contains("News is busy. Retrying in a moment.", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".mud-snackbar"));
    }

    // ── Empty states ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task No_feeds_offers_new_feed_with_the_claim_and_a_sentence_without()
    {
        await using (var admin = Context([]))
        {
            var cut = admin.Render<NewsFeedsPage>();
            cut.WaitForAssertion(() => Assert.Contains("No feeds yet", cut.Markup, StringComparison.Ordinal));
            Assert.Equal("-1", cut.Find("h2.nw-empty-h").GetAttribute("tabindex"));
            Assert.Contains(cut.FindAll(".odc-empty button"), b => b.TextContent.Contains("New feed", StringComparison.Ordinal));
        }

        await using (var reader = Context([], ReaderOnly))
        {
            var cut = reader.Render<NewsFeedsPage>();
            cut.WaitForAssertion(() => Assert.Contains("Ask an administrator to add feeds.", cut.Markup, StringComparison.Ordinal));
            Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("New feed", StringComparison.Ordinal));
        }
    }

    // ── Filter bar, overview, sub-line ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Status_defaults_to_active_and_all_shows_every_feed()
    {
        var paused = Feed("Paused one", f => f.IsActive = false);
        var archived = Feed("Archived one", f => { f.ArchivedAtUtc = Now.UtcDateTime; f.IsActive = false; });
        await using var context = Context([Feed("Active one"), paused, archived]);
        var cut = context.Render<NewsFeedsPage>();

        cut.WaitForAssertion(() => Assert.Equal(["Active one"], CardNames(cut)));
        var status = cut.FindComponents<OdsMultiSelect>().Single(m => m.Instance.Label is "Status" or "Status: All");
        Assert.Equal("Status", status.Instance.Label);

        await cut.InvokeAsync(() => status.Instance.ValuesChanged.InvokeAsync([]));

        cut.WaitForAssertion(() => Assert.Equal(3, CardNames(cut).Count));
        Assert.Equal("Status: All", cut.FindComponents<OdsMultiSelect>().Single(m => m.Instance.Label is "Status" or "Status: All").Instance.Label);
    }

    [Fact]
    public async Task By_status_rows_sum_to_the_feed_count_and_need_attention_stays_in_the_sub_line()
    {
        var feeds = new[]
        {
            Feed("A", f => f.Health = NewsFeedHealth.Failing),
            Feed("B", f => { f.IsActive = false; f.Health = NewsFeedHealth.Paused; }),
            Feed("C", f => { f.ArchivedAtUtc = Now.UtcDateTime; f.Health = NewsFeedHealth.Archived; }),
            Feed("D", f => { f.Health = NewsFeedHealth.QuotaReached; f.QuotaReason = NewsFeedQuotaReason.DailyCap; }),
        };
        await using var context = Context(feeds);
        var cut = context.Render<NewsFeedsPage>();

        cut.WaitForAssertion(() => Assert.Contains("4 feeds · 2 need attention", cut.Markup, StringComparison.Ordinal));
        var byStatus = cut.FindComponents<OdsBreakdownTile>().Single(t => t.Instance.Label == "By status");
        Assert.Equal(["Active", "Paused", "Archived"], byStatus.Instance.Rows!.Select(r => r.Label));
        Assert.Equal(feeds.Length, byStatus.Instance.Rows!.Sum(r => (int)r.Count));
    }

    [Fact]
    public async Task By_tags_counts_feeds_per_tag_plus_no_tag()
    {
        var tech = new ExistingNewsTag { Id = Guid.NewGuid(), Name = "Tech" };
        await using var context = Context([Feed("A", f => f.TagIds = [tech.Id]), Feed("B")], tags: [tech]);
        var cut = context.Render<NewsFeedsPage>();

        cut.WaitForAssertion(() => Assert.Equal(2, CardNames(cut).Count));
        var byTags = cut.FindComponents<OdsBreakdownTile>().Single(t => t.Instance.Label == "By tags");
        Assert.Equal([("Tech", 1), ("No tag", 1)], byTags.Instance.Rows!.Select(r => (r.Label, (int)r.Count)));
    }

    [Fact]
    public async Task Search_matches_the_name_or_the_site_host()
    {
        await using var context = Context([Feed("Fjord Weather", f => f.SiteUrl = "https://yr.no/"), Feed("Tech Daily")]);
        var cut = context.Render<NewsFeedsPage>();
        cut.WaitForAssertion(() => Assert.Equal(2, CardNames(cut).Count));

        await cut.InvokeAsync(() => cut.FindComponent<OdsSearchField>().Instance.ValueChanged.InvokeAsync("yr.no"));

        cut.WaitForAssertion(() => Assert.Equal(["Fjord Weather"], CardNames(cut)));
        Assert.Empty(cut.FindAll(".odc-pager"));
    }

    // ── Card ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_card_shows_health_as_text_the_site_host_the_check_time_and_the_interval()
    {
        await using var context = Context([Feed("Example News", f => f.Language = "nb")]);
        var cut = context.Render<NewsFeedsPage>();

        cut.WaitForAssertion(() => Assert.Single(CardNames(cut)));
        Assert.Equal("nb", cut.Find(".odc-record-name").GetAttribute("lang"));
        Assert.Contains("Healthy", cut.Find(".odc-newshealth").TextContent, StringComparison.Ordinal);
        var meta = cut.Find(".odc-record-meta").TextContent;
        Assert.Contains("site.example", meta, StringComparison.Ordinal);
        Assert.Contains("Checked 12 min ago", meta, StringComparison.Ordinal);
        Assert.Contains("Every 60 min", meta, StringComparison.Ordinal);
        Assert.NotNull(cut.Find(".odc-record-meta time[datetime]"));
        Assert.Equal("3", cut.Find($"[role=heading]").GetAttribute("aria-level"));
    }

    [Fact]
    public async Task Storage_limit_reached_is_described_by_its_reason()
    {
        var feed = Feed("Busy site", f => { f.Health = NewsFeedHealth.QuotaReached; f.QuotaReason = NewsFeedQuotaReason.TextQuota; });
        await using var context = Context([feed]);
        var cut = context.Render<NewsFeedsPage>();
        cut.WaitForAssertion(() => Assert.Single(CardNames(cut)));

        var chip = cut.Find(".odc-newshealth");
        Assert.Contains("Storage limit reached", chip.TextContent, StringComparison.Ordinal);
        var describedBy = chip.GetAttribute("aria-describedby");
        Assert.Equal("Text storage quota is full. An administrator can raise it in Settings.", cut.Find($"#{describedBy}").TextContent.Trim());

        Expand(cut, feed);
        cut.WaitForAssertion(() => Assert.Contains("Storage limit", cut.Find(".nw-facts").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_news_feeds_update_no_url_error_detail_or_fetch_host_reaches_the_dom()
    {
        var feed = Feed("Example News", f =>
        {
            f.SiteUrl = "https://www.example-site.test/";
            f.Health = NewsFeedHealth.Failing;
        }, managed: false);
        await using var context = Context([feed], ReaderOnly);
        var cut = context.Render<NewsFeedsPage>();
        cut.WaitForAssertion(() => Assert.Single(CardNames(cut)));

        Expand(cut, feed);

        cut.WaitForAssertion(() => Assert.Contains("Settings", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("fetch.example", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Feed URL", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Last result", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("www.example-site.test", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll($"#nf-{feed.Id} .odc-record-ctl button[aria-label^='Actions for']"));
        Assert.DoesNotContain("To delete this feed, archive it first.", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_news_feeds_update_the_url_shows_masked_until_show_full_address()
    {
        var feed = Feed("Example News", f => f.Management!.LastErrorMessage = "fetch.example answered 503");
        await using var context = Context([feed]);
        var cut = context.Render<NewsFeedsPage>();
        cut.WaitForAssertion(() => Assert.Single(CardNames(cut)));

        Expand(cut, feed);

        cut.WaitForAssertion(() => Assert.Contains("https://fetch.example/feed.xml?…", cut.Find(".nw-feedurl-text").TextContent, StringComparison.Ordinal));
        Assert.DoesNotContain("s3cret", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("(query hidden)", cut.Find(".nw-feedurl").TextContent, StringComparison.Ordinal);
        Assert.Contains("fetch.example answered 503", cut.Markup, StringComparison.Ordinal);
        var toggle = cut.FindAll(".nw-feedurl button").Single(b => b.TextContent.Contains("Show full address", StringComparison.Ordinal));
        Assert.Equal("false", toggle.GetAttribute("aria-pressed"));

        toggle.Click();

        cut.WaitForAssertion(() => Assert.Contains("s3cret", cut.Find(".nw-feedurl-text").TextContent, StringComparison.Ordinal));
        Assert.Equal("true", cut.FindAll(".nw-feedurl button").Single(b => b.TextContent.Contains("Show full address", StringComparison.Ordinal)).GetAttribute("aria-pressed"));
    }

    [Fact]
    public async Task The_archive_first_sentence_shows_only_to_a_caller_with_both_claims_on_a_non_archived_feed()
    {
        var quarantined = Feed("Q", f => f.Health = NewsFeedHealth.Quarantined);
        await using (var both = Context([quarantined]))
        {
            var cut = both.Render<NewsFeedsPage>();
            cut.WaitForAssertion(() => Assert.Single(CardNames(cut)));
            Expand(cut, quarantined);
            cut.WaitForAssertion(() => Assert.Contains("To delete this feed, archive it first. Archive is in the ⋯ menu on this card.", cut.Markup, StringComparison.Ordinal));
        }

        await using (var updateOnly = Context([quarantined], [PermissionClaims.NewsRead, PermissionClaims.NewsFeedsUpdate]))
        {
            var cut = updateOnly.Render<NewsFeedsPage>();
            cut.WaitForAssertion(() => Assert.Single(CardNames(cut)));
            Expand(cut, quarantined);
            cut.WaitForAssertion(() => Assert.Contains("Settings", cut.Markup, StringComparison.Ordinal));
            Assert.DoesNotContain("To delete this feed, archive it first.", cut.Markup, StringComparison.Ordinal);
        }
    }

    // ── Menu ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_menu_lists_only_what_the_feeds_state_allows_and_never_a_disabled_item()
    {
        var active = Feed("Active");
        var paused = Feed("Paused", f => { f.IsActive = false; f.Health = NewsFeedHealth.Paused; });
        var quarantined = Feed("Quarantined", f => f.Health = NewsFeedHealth.Quarantined);
        var archived = Feed("Archived", f => { f.ArchivedAtUtc = Now.UtcDateTime; f.IsActive = false; f.Health = NewsFeedHealth.Archived; });
        await using var context = Context([active, paused, quarantined, archived]);
        context.State.Setup(s => s.LoadAsync<NewsFeedsPage.NewsFeedsPageState>(NewsFeedsPage.PageStateKey))
            .ReturnsAsync(new NewsFeedsPage.NewsFeedsPageState { Statuses = [] });
        var cut = context.Render<NewsFeedsPage>();
        cut.WaitForAssertion(() => Assert.Equal(4, CardNames(cut).Count));

        Assert.Equal(["Edit", "Tags…", "Refresh now", "Pause", "Archive"], MenuLabels(cut, active));
        Assert.Equal(["Edit", "Tags…", "Resume", "Archive"], MenuLabels(cut, paused));
        Assert.Equal(["Edit", "Tags…", "Pause", "Release from quarantine", "Archive"], MenuLabels(cut, quarantined));
        Assert.Equal(["Tags…", "Unarchive", "Delete…"], MenuLabels(cut, archived));
        Assert.Empty(cut.FindAll(".mud-menu-item.mud-disabled, .mud-menu-item[aria-disabled=true]"));
    }

    [Fact]
    public async Task Pausing_a_feed_out_of_the_filter_announces_where_it_moved()
    {
        var feed = Feed("Example News");
        await using var context = Context([feed, Feed("Zed")]);
        UpdateNewsFeed? sent = null;
        context.News.Setup(n => n.UpdateFeedAsync(feed.Id, It.IsAny<UpdateNewsFeed>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, UpdateNewsFeed u, CancellationToken _) => sent = u)
            .ReturnsAsync(() => Ok(Feed("Example News", f => { f.Id = feed.Id; f.IsActive = false; f.Health = NewsFeedHealth.Paused; })));
        var cut = context.Render<NewsFeedsPage>();
        cut.WaitForAssertion(() => Assert.Equal(2, CardNames(cut).Count));

        ClickMenu(cut, feed, "Pause");

        cut.WaitForAssertion(() => Assert.Equal(["Zed"], CardNames(cut)));
        cut.WaitForAssertion(() => Assert.Contains("Example News paused and moved to Paused", Polite(cut), StringComparison.Ordinal));
        Assert.False(sent!.IsActive);
        Assert.Equal(feed.Version, sent.Version);
        Assert.Equal(feed.Management!.Url, sent.Url);
        cut.WaitForAssertion(() => Assert.Contains(context.Ctx.JSInterop.Invocations, i => i.Identifier == "odsNewsFocusCard"));
    }

    [Fact]
    public async Task Archiving_under_all_keeps_the_card_and_says_archived()
    {
        var feed = Feed("Example News");
        await using var context = Context([feed]);
        context.State.Setup(s => s.LoadAsync<NewsFeedsPage.NewsFeedsPageState>(NewsFeedsPage.PageStateKey))
            .ReturnsAsync(new NewsFeedsPage.NewsFeedsPageState { Statuses = [] });
        context.News.Setup(n => n.ArchiveFeedAsync(feed.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Feed("Example News", f => { f.Id = feed.Id; f.ArchivedAtUtc = Now.UtcDateTime; f.Health = NewsFeedHealth.Archived; })));
        var cut = context.Render<NewsFeedsPage>();
        cut.WaitForAssertion(() => Assert.Single(CardNames(cut)));

        ClickMenu(cut, feed, "Archive");

        cut.WaitForAssertion(() => Assert.Contains("Example News archived", Polite(cut), StringComparison.Ordinal));
        Assert.Single(CardNames(cut));
        Assert.DoesNotContain("moved to", Polite(cut), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_404_on_an_action_removes_the_card()
    {
        var feed = Feed("Example News");
        await using var context = Context([feed]);
        context.News.Setup(n => n.RefreshFeedAsync(feed.Id, It.IsAny<CancellationToken>())).ReturnsAsync(Fail(HttpStatusCode.NotFound));
        var cut = context.Render<NewsFeedsPage>();
        cut.WaitForAssertion(() => Assert.Single(CardNames(cut)));

        ClickMenu(cut, feed, "Refresh now");

        cut.WaitForAssertion(() => Assert.Contains("No longer available.", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(CardNames(cut));
    }

    [Fact]
    public async Task A_busy_action_shows_the_busy_snackbar()
    {
        var feed = Feed("Example News");
        await using var context = Context([feed]);
        context.News.Setup(n => n.RefreshFeedAsync(feed.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail(HttpStatusCode.ServiceUnavailable, NewsErrorCodes.Busy));
        var cut = context.Render<NewsFeedsPage>();
        cut.WaitForAssertion(() => Assert.Single(CardNames(cut)));

        ClickMenu(cut, feed, "Refresh now");

        cut.WaitForAssertion(() => Assert.Contains(NewsFeedback.BusyText, cut.Find(".mud-snackbar").TextContent, StringComparison.Ordinal));
    }

    // ── Delete ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_needs_the_acknowledgement_and_a_202_removes_the_card()
    {
        var feed = Feed("Example News", f => { f.ArchivedAtUtc = Now.UtcDateTime; f.IsActive = false; f.Health = NewsFeedHealth.Archived; });
        await using var context = Context([feed]);
        context.State.Setup(s => s.LoadAsync<NewsFeedsPage.NewsFeedsPageState>(NewsFeedsPage.PageStateKey))
            .ReturnsAsync(new NewsFeedsPage.NewsFeedsPageState { Statuses = ["archived"] });
        context.News.Setup(n => n.DeleteFeedAsync(feed.Id, It.IsAny<CancellationToken>())).ReturnsAsync(ApiResult.Success(HttpStatusCode.Accepted));
        var cut = context.Render<NewsFeedsPage>();
        cut.WaitForAssertion(() => Assert.Single(CardNames(cut)));

        ClickMenu(cut, feed, "Delete…");
        cut.WaitForAssertion(() => Assert.Contains("and its 311 articles? This cannot be undone.", cut.Find("#nf-del-desc").TextContent, StringComparison.Ordinal));
        Assert.Equal("Example News", cut.Find("#nf-del-desc em").TextContent.Trim());
        Assert.Equal("true", cut.Find("#nf-del-confirm").GetAttribute("aria-disabled"));
        Assert.Contains("Tick the box above to delete.", cut.Markup, StringComparison.Ordinal);
        cut.Find("#nf-del-confirm").Click();
        context.News.Verify(n => n.DeleteFeedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);

        cut.Find("#nf-del-ack").Change(true);
        cut.Find("#nf-del-confirm").Click();

        cut.WaitForAssertion(() => Assert.Empty(CardNames(cut)));
        // Said once, by the snackbar (a live region in MudBlazor 9), so the page announcer stays silent (AC 29).
        cut.WaitForAssertion(() => Assert.Contains("Feed deleted. Its articles are being removed in the background.", cut.Find(".mud-snackbar").TextContent, StringComparison.Ordinal));
        Assert.DoesNotContain("Feed deleted.", Polite(cut), StringComparison.Ordinal);
    }

    // ── Create flow ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_created_feed_is_expanded_pending_polled_and_its_first_state_change_announced_once()
    {
        var created = Feed("Example News", f => { f.Health = NewsFeedHealth.Pending; f.LastCheckedAtUtc = null; f.LastSuccessAtUtc = null; });
        await using var context = Context([Feed("Other")]);
        context.News.Setup(n => n.CreateFeedAsync(It.IsAny<NewNewsFeed>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok(created));
        var polls = 0;
        context.News.Setup(n => n.GetFeedAsync(created.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++polls < 2 ? Ok(created) : Ok(created with { Health = NewsFeedHealth.Healthy }));
        var cut = context.Render<NewsFeedsPage>();
        cut.WaitForAssertion(() => Assert.Single(CardNames(cut)));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "New feed").Click();
        var dialog = cut.FindComponent<NewsFeedDialog>();
        cut.Find($"input#{NewsFeedDialog.UrlId}, #{NewsFeedDialog.UrlId} input").Input("https://example.com/feed.xml");
        var form = dialog.FindComponent<OdsFormDialog>();
        Assert.True(await cut.InvokeAsync(() => form.Instance.OnSubmit!()));

        cut.WaitForAssertion(() => Assert.Equal(2, CardNames(cut).Count));
        Assert.Contains("Pending first fetch", cut.Find($"#nf-{created.Id} .odc-newshealth").TextContent, StringComparison.Ordinal);
        Assert.Equal("true", cut.Find($"#nf-{created.Id} .odc-record-trigger").GetAttribute("aria-expanded"));
        Assert.Contains(cut.FindAll($"#nf-{created.Id} button"), b => b.TextContent.Contains("Stop checking", StringComparison.Ordinal));
        Assert.Contains(cut.FindAll($"#nf-{created.Id} button"), b => b.TextContent.Contains("Check again", StringComparison.Ordinal));

        context.Time.Advance(NewsFeedsPage.PollInterval);
        cut.WaitForAssertion(() => Assert.Equal(1, polls));
        context.Time.Advance(NewsFeedsPage.PollInterval);
        cut.WaitForAssertion(() => Assert.Contains("Example News: fetched", Polite(cut), StringComparison.Ordinal));
        context.Time.Advance(NewsFeedsPage.PollInterval * 3);
        Assert.Equal(2, polls);
    }

    [Fact]
    public async Task A_poll_that_reaches_two_minutes_says_so()
    {
        var created = Feed("Example News", f => f.Health = NewsFeedHealth.Pending);
        await using var context = Context([]);
        context.News.Setup(n => n.CreateFeedAsync(It.IsAny<NewNewsFeed>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok(created));
        context.News.Setup(n => n.GetFeedAsync(created.Id, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(created));
        var cut = context.Render<NewsFeedsPage>();
        cut.WaitForAssertion(() => Assert.Contains("No feeds yet", cut.Markup, StringComparison.Ordinal));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "New feed").Click();
        cut.Find($"input#{NewsFeedDialog.UrlId}, #{NewsFeedDialog.UrlId} input").Input("https://example.com/feed.xml");
        Assert.True(await cut.InvokeAsync(() => cut.FindComponent<NewsFeedDialog>().FindComponent<OdsFormDialog>().Instance.OnSubmit!()));

        for (var i = 0; i < 12; i++)
        {
            context.Time.Advance(NewsFeedsPage.PollInterval);
        }

        cut.WaitForAssertion(() => Assert.Contains("Example News hasn't been fetched yet. Check again later.", Polite(cut), StringComparison.Ordinal));
        Assert.DoesNotContain(cut.FindAll($"#nf-{created.Id} button"), b => b.TextContent.Contains("Stop checking", StringComparison.Ordinal));
    }

    // ── Use new address ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Use_new_address_sends_one_put_with_the_suggested_url_and_the_version()
    {
        var feed = Feed("Example News", f => f.Management!.SuggestedUrl = "https://fetch.example/new.xml");
        await using var context = Context([feed]);
        UpdateNewsFeed? sent = null;
        context.News.Setup(n => n.UpdateFeedAsync(feed.Id, It.IsAny<UpdateNewsFeed>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, UpdateNewsFeed u, CancellationToken _) => sent = u)
            .ReturnsAsync(() => Ok(Feed("Example News", f => { f.Id = feed.Id; f.Management!.Url = "https://fetch.example/new.xml"; })));
        var cut = context.Render<NewsFeedsPage>();
        cut.WaitForAssertion(() => Assert.Single(CardNames(cut)));
        Expand(cut, feed);
        cut.WaitForAssertion(() => Assert.Contains("This feed has moved to", cut.Markup, StringComparison.Ordinal));
        Assert.NotNull(cut.Find(".nw-moved a[href='https://fetch.example/new.xml'][rel='noopener noreferrer']"));

        cut.FindAll("button").First(b => b.TextContent.Contains("Use new address", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains("Feed address updated", cut.Find(".mud-snackbar").TextContent, StringComparison.Ordinal));
        Assert.DoesNotContain("Feed address updated", Polite(cut), StringComparison.Ordinal);
        Assert.Equal("https://fetch.example/new.xml", sent!.Url);
        Assert.Equal(feed.Version, sent.Version);
        context.News.Verify(n => n.UpdateFeedAsync(It.IsAny<Guid>(), It.IsAny<UpdateNewsFeed>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.DoesNotContain("This feed has moved to", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Use_new_address_is_absent_without_the_update_claim()
    {
        var feed = Feed("Example News", f => f.Management!.SuggestedUrl = "https://fetch.example/new.xml");
        // A management block without the claim cannot happen on the wire, but the button still keys on the claim.
        await using var context = Context([feed], [PermissionClaims.NewsRead]);
        var cut = context.Render<NewsFeedsPage>();
        cut.WaitForAssertion(() => Assert.Single(CardNames(cut)));
        Expand(cut, feed);

        cut.WaitForAssertion(() => Assert.Contains("This feed has moved to", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Use new address", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_409_on_use_new_address_opens_the_reload_flow()
    {
        var feed = Feed("Example News", f => f.Management!.SuggestedUrl = "https://fetch.example/new.xml");
        await using var context = Context([feed]);
        context.News.Setup(n => n.UpdateFeedAsync(feed.Id, It.IsAny<UpdateNewsFeed>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<ExistingNewsFeed>(HttpStatusCode.Conflict, NewsErrorCodes.FeedChanged));
        var cut = context.Render<NewsFeedsPage>();
        cut.WaitForAssertion(() => Assert.Single(CardNames(cut)));
        Expand(cut, feed);

        cut.FindAll("button").First(b => b.TextContent.Contains("Use new address", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains("This feed was changed elsewhere. Reload it to see the latest settings.", cut.Markup, StringComparison.Ordinal));
    }

    // ── Routes and keys ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_reveal_link_clears_status_to_all_and_opens_the_feed()
    {
        var paused = Feed("Paused one", f => { f.IsActive = false; f.Health = NewsFeedHealth.Paused; });
        await using var context = Context([Feed("Active"), paused]);
        context.Ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo($"news-feeds?state=All&reveal={paused.Id}");
        var cut = context.Render<NewsFeedsPage>();

        cut.WaitForAssertion(() => Assert.Equal(2, CardNames(cut).Count));
        Assert.Equal("true", cut.Find($"#nf-{paused.Id} .odc-record-trigger").GetAttribute("aria-expanded"));
        context.State.Verify(s => s.QueueSave(NewsFeedsPage.PageStateKey, It.Is<NewsFeedsPage.NewsFeedsPageState>(st => st.Statuses!.Count == 0)), Times.Never);
    }

    [Fact]
    public void The_route_policy_and_state_key_are_the_spec_ones()
    {
        Assert.Equal("news-feeds-page", NewsFeedsPage.PageStateKey);
        var route = typeof(NewsFeedsPage).GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.RouteAttribute), false)
            .Cast<Microsoft.AspNetCore.Components.RouteAttribute>().Single();
        Assert.Equal("/news-feeds", route.Template);
        var policy = typeof(NewsFeedsPage).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Single();
        Assert.Equal(PermissionClaims.NewsRead, policy.Policy);
    }
}
