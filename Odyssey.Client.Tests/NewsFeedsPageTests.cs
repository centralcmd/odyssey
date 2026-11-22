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
            Id = Guid.NewGuid(),
            Version = Guid.NewGuid(),
            DisplayTitle = title,
            Title = title,
            SiteUrl = "https://site.example/",
            EffectiveIntervalMinutes = 60,
            DownloadImages = true,
            IsActive = true,
            CreatedAtUtc = Now.UtcDateTime,
            LastCheckedAtUtc = Now.UtcDateTime.AddMinutes(-12),
            LastSuccessAtUtc = Now.UtcDateTime.AddMinutes(-12),
            Health = NewsFeedHealth.Healthy,
            UnreadCount = 4,
            ArticleCount = 311,
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
        context.NewsTags.Setup(n => n.ListAllAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok(tags ?? []));
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
        context.NewsTags.Setup(n => n.ListAllAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new List<ExistingNewsTag>()));

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
        context.NewsTags.Setup(n => n.ListAllAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new List<ExistingNewsTag>()));

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
        context.NewsTags.Setup(n => n.ListAllAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new List<ExistingNewsTag>()));

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
        context.NewsTags.Setup(n => n.ListAllAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new List<ExistingNewsTag>()));

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
    public async Task By_status_shows_the_status_filter_members_and_its_total_is_the_feed_count()
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
        Assert.Equal(
            ["Active", "Paused", "Archived", "Needs attention", "Healthy", "Pending first fetch", "Failing", "Storage limit reached", "Gone", "Quarantined"],
            byStatus.Instance.Rows!.Select(r => r.Label));
        Assert.Equal([2, 1, 1, 2, 0, 0, 1, 1, 0, 0], byStatus.Instance.Rows!.Select(r => (int)r.Count));
        // The rows overlap, so the total is the feed count, not their sum.
        Assert.Equal(feeds.Length, byStatus.Instance.TotalValue);
        Assert.Equal(OdsBreakdown.Tone("pending"), byStatus.Instance.Rows!.Single(r => r.Label == "Paused").IconColor);
    }

    [Fact]
    public async Task The_status_filter_ors_within_a_group_and_ands_lifecycle_with_health()
    {
        var feeds = new[]
        {
            Feed("Failing live", f => f.Health = NewsFeedHealth.Failing),
            Feed("Healthy live"),
            Feed("Gone archived", f => { f.ArchivedAtUtc = Now.UtcDateTime; f.Health = NewsFeedHealth.Gone; }),
        };
        await using var context = Context(feeds);
        var cut = context.Render<NewsFeedsPage>();
        cut.WaitForAssertion(() => Assert.Equal(2, CardNames(cut).Count));

        var status = cut.FindComponents<OdsMultiSelect>().Single(m => m.Instance.Options == NewsVisuals.StatusOptions);
        await cut.InvokeAsync(() => status.Instance.ValuesChanged.InvokeAsync(["active", NewsVisuals.NeedsAttentionKey]));
        cut.WaitForAssertion(() => Assert.Equal(["Failing live"], CardNames(cut)));

        await cut.InvokeAsync(() => status.Instance.ValuesChanged.InvokeAsync([NewsVisuals.NeedsAttentionKey]));
        cut.WaitForAssertion(() => Assert.Equal(2, CardNames(cut).Count));

        await cut.InvokeAsync(() => status.Instance.ValuesChanged.InvokeAsync(["active", "archived", nameof(NewsFeedHealth.Healthy)]));
        cut.WaitForAssertion(() => Assert.Equal(["Healthy live"], CardNames(cut)));
    }

    [Fact]
    public async Task Attention_lists_each_feed_that_needs_it_and_review_reveals_it_on_this_page()
    {
        var gone = Feed("Gone one", f => { f.ArchivedAtUtc = Now.UtcDateTime; f.Health = NewsFeedHealth.Gone; });
        var failing = Feed("Failing one", f => f.Health = NewsFeedHealth.Failing);
        await using var context = Context([gone, failing, Feed("Fine")]);
        var cut = context.Render<NewsFeedsPage>();
        cut.WaitForAssertion(() => Assert.Equal(2, CardNames(cut).Count));

        var header = cut.FindComponent<PageHeader>();
        Assert.Equal("Attention", header.Instance.ProblemsLabel);
        var problems = header.Instance.Problems!.ToList();
        Assert.Equal(["Failing one", "Gone one"], problems.Select(p => p.Lead));
        Assert.Equal([PageHeaderSeverity.Warning, PageHeaderSeverity.Error], problems.Select(p => p.Severity));
        Assert.Equal("Review Gone one", problems[1].ActionAriaLabel);

        await cut.InvokeAsync(() => problems[1].OnView.InvokeAsync());

        cut.WaitForAssertion(() => Assert.Equal(3, CardNames(cut).Count));
        Assert.Equal("true", cut.Find($"#nf-{gone.Id} .odc-record-trigger").GetAttribute("aria-expanded"));
        Assert.Contains(context.Ctx.JSInterop.Invocations, i => i.Identifier == "odsNewsFocusCard");
    }

    [Fact]
    public async Task By_tags_counts_feeds_per_tag_plus_no_tag()
    {
        var tech = new ExistingNewsTag { NewsTagId = Guid.NewGuid(), Name = "Tech" };
        await using var context = Context([Feed("A", f => f.TagIds = [tech.NewsTagId]), Feed("B")], tags: [tech]);
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
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll($"#nf-{feed.Id} .odc-infotile"), t => t.TextContent.Contains("Storage limit", StringComparison.Ordinal)));
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

        cut.WaitForAssertion(() => Assert.Contains("Check every", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("fetch.example", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Feed URL", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Last result", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("www.example-site.test", cut.Markup, StringComparison.Ordinal);
        // A reader still gets View articles, and nothing else.
        Assert.Equal(["View articles"], MenuLabels(cut, feed));
    }

    [Fact]
    public async Task With_news_feeds_update_the_whole_url_is_a_link_and_its_tile_menu_copies_it()
    {
        var feed = Feed("Example News", f => f.Management!.LastErrorMessage = "fetch.example answered 503");
        await using var context = Context([feed]);
        var cut = context.Render<NewsFeedsPage>();
        cut.WaitForAssertion(() => Assert.Single(CardNames(cut)));

        Expand(cut, feed);

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find($"#nf-{feed.Id} .nw-tile.wide a.nw-link-url")));
        var link = cut.Find($"#nf-{feed.Id} a.nw-link-url");
        Assert.Equal(feed.Management!.Url, link.QuerySelector("bdi")!.TextContent);
        Assert.Contains("s3cret", link.TextContent, StringComparison.Ordinal);
        Assert.Equal("_blank", link.GetAttribute("target"));
        Assert.Equal("noopener noreferrer", link.GetAttribute("rel"));
        Assert.Equal("no-referrer", link.GetAttribute("referrerpolicy"));
        Assert.Contains("(opens in a new tab)", link.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Show full address", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("fetch.example answered 503", cut.Markup, StringComparison.Ordinal);
        Assert.NotNull(cut.Find($"#nf-{feed.Id} .nw-tile-menu button[aria-label='Actions for the feed URL of Example News']"));
    }

    [Fact]
    public async Task No_archive_first_note_is_shown()
    {
        var quarantined = Feed("Q", f => f.Health = NewsFeedHealth.Quarantined);
        await using var both = Context([quarantined]);
        var cut = both.Render<NewsFeedsPage>();
        cut.WaitForAssertion(() => Assert.Single(CardNames(cut)));
        Expand(cut, quarantined);
        cut.WaitForAssertion(() => Assert.Contains("Check every", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("archive it first", cut.Markup, StringComparison.Ordinal);
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

        // View articles first; Delete… on any feed, archived or not; the tags live in the Edit dialog.
        Assert.Equal(["View articles", "Edit", "Refresh now", "Pause", "Archive", "Delete…"], MenuLabels(cut, active));
        Assert.Equal(["View articles", "Edit", "Resume", "Archive", "Delete…"], MenuLabels(cut, paused));
        Assert.Equal(["View articles", "Edit", "Pause", "Release from quarantine", "Archive", "Delete…"], MenuLabels(cut, quarantined));
        Assert.Equal(["View articles", "Unarchive", "Delete…"], MenuLabels(cut, archived));
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
        // A live feed: there is no archive-first step.
        var feed = Feed("Example News");
        await using var context = Context([feed]);
        context.News.Setup(n => n.DeleteFeedAsync(feed.Id, It.IsAny<CancellationToken>())).ReturnsAsync(ApiResult.Success(HttpStatusCode.Accepted));
        var cut = context.Render<NewsFeedsPage>();
        cut.WaitForAssertion(() => Assert.Single(CardNames(cut)));

        ClickMenu(cut, feed, "Delete…");
        cut.WaitForAssertion(() => Assert.Contains("and its 311 articles? This cannot be undone.", cut.Find("#nf-del-desc").TextContent, StringComparison.Ordinal));
        Assert.Equal("Example News", cut.Find("#nf-del-desc strong").TextContent.Trim());
        Assert.Contains("Delete “Example News”?", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("I understand this cannot be undone", cut.Markup, StringComparison.Ordinal);
        Assert.Equal("true", cut.Find("#nf-del-confirm").GetAttribute("aria-disabled"));
        // aria-disabled alone gives no reason: the visible hint is the button's description.
        Assert.Equal("nf-del-reason", cut.Find("#nf-del-confirm").GetAttribute("aria-describedby"));
        Assert.Equal("Tick the box above to delete.", cut.Find("#nf-del-reason").TextContent.Trim());
        cut.Find("#nf-del-confirm").Click();
        context.News.Verify(n => n.DeleteFeedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);

        cut.Find("#nf-del-ack").Change(true);
        Assert.Null(cut.Find("#nf-del-confirm").GetAttribute("aria-describedby"));
        Assert.Empty(cut.FindAll("#nf-del-reason"));
        cut.Find("#nf-del-confirm").Click();

        cut.WaitForAssertion(() => Assert.Empty(CardNames(cut)));
        // Said once, by the snackbar (a live region in MudBlazor 9), so the page announcer stays silent (AC 29).
        cut.WaitForAssertion(() => Assert.Contains("Feed deleted. Its articles are being removed in the background.", cut.Find(".mud-snackbar").TextContent, StringComparison.Ordinal));
        Assert.DoesNotContain("Feed deleted.", Polite(cut), StringComparison.Ordinal);
    }

    // ── Create flow ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_created_feed_is_expanded_pending_and_check_again_reads_it_once()
    {
        var created = Feed("Example News", f => { f.Health = NewsFeedHealth.Pending; f.LastCheckedAtUtc = null; f.LastSuccessAtUtc = null; });
        await using var context = Context([Feed("Other")]);
        context.News.Setup(n => n.CreateFeedAsync(It.IsAny<NewNewsFeed>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok(created));
        var reads = 0;
        context.News.Setup(n => n.GetFeedAsync(created.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++reads < 2 ? Ok(created) : Ok(created with { Health = NewsFeedHealth.Healthy }));
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
        Assert.Contains("Not fetched yet.", cut.Find($"#nf-{created.Id}").TextContent, StringComparison.Ordinal);
        // No polling: nothing reads the feed until Check again, and there is no Stop checking.
        Assert.DoesNotContain(cut.FindAll($"#nf-{created.Id} button"), b => b.TextContent.Contains("Stop checking", StringComparison.Ordinal));
        Assert.Equal(0, reads);

        CheckAgain(cut, created);
        cut.WaitForAssertion(() => Assert.Contains("Example News: still waiting for the first fetch.", Polite(cut), StringComparison.Ordinal));
        CheckAgain(cut, created);
        cut.WaitForAssertion(() => Assert.Contains("Example News: Healthy.", Polite(cut), StringComparison.Ordinal));
        Assert.Equal(2, reads);
        Assert.Contains("Healthy", cut.Find($"#nf-{created.Id} .odc-newshealth").TextContent, StringComparison.Ordinal);
    }

    private static void CheckAgain(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, ExistingNewsFeed feed) =>
        cut.FindAll($"#nf-{feed.Id} button").First(b => b.TextContent.Contains("Check again", StringComparison.Ordinal)).Click();

    [Fact]
    public async Task A_moved_feed_shows_no_suggestion_block()
    {
        var feed = Feed("Example News", f => f.Management!.SuggestedUrl = "https://fetch.example/new.xml");
        await using var context = Context([feed]);
        var cut = context.Render<NewsFeedsPage>();
        cut.WaitForAssertion(() => Assert.Single(CardNames(cut)));
        Expand(cut, feed);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll($"#nf-{feed.Id} .odc-infotile")));
        Assert.DoesNotContain("has moved", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Use new address", StringComparison.Ordinal));
        Assert.DoesNotContain("fetch.example/new.xml", cut.Markup, StringComparison.Ordinal);
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
