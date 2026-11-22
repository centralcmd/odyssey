using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Internet;
using Odyssey.Client.Pages.JobRuns;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;
using Xunit;
using static Odyssey.Client.Tests.NewsPageTestContext;

namespace Odyssey.Client.Tests;

/// <summary>
/// The podcast changes outside <c>/podcasts</c> (issue #559 §3.3–§3.6): the feed dialog's Type and Podcast settings
/// and the always-confirmed switch back, the Feeds page's Type filter, By type tile and podcast card, the News page's
/// articles-only catalogue and redirect, and the status panel's audio bar.
/// </summary>
public class PodcastFeedSurfaceTests
{
    // ── The feed dialog ────────────────────────────────────────────────────────────────────────────

    private static IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderDialog(NewsPageTestContext context, ExistingNewsFeed? feed = null,
        NewsFeedKind initialKind = NewsFeedKind.Article, List<ExistingNewsFeed>? saved = null) =>
        context.Ctx.Render(b =>
        {
            b.OpenComponent<MudBlazor.MudDialogProvider>(0);
            b.CloseComponent();
            b.OpenComponent<MudBlazor.MudPopoverProvider>(1);
            b.CloseComponent();
            b.OpenComponent<NewsFeedDialog>(2);
            b.AddComponentParameter(3, nameof(NewsFeedDialog.Open), true);
            b.AddComponentParameter(4, nameof(NewsFeedDialog.Feed), feed);
            b.AddComponentParameter(5, nameof(NewsFeedDialog.InitialKind), initialKind);
            b.AddComponentParameter(6, nameof(NewsFeedDialog.Saved),
                EventCallback.Factory.Create<ExistingNewsFeed>(new object(), f => saved?.Add(f)));
            b.CloseComponent();
        });

    private static Task<bool> Submit(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut) =>
        cut.InvokeAsync(() => cut.FindComponents<OdsFormDialog>()[0].Instance.OnSubmit!());

    private static AngleSharp.Dom.IElement Switch(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string label) =>
        cut.FindAll("label").First(l => l.TextContent.Contains(label, StringComparison.Ordinal)).QuerySelector("input")!;

    [Fact]
    public async Task New_podcast_opens_on_podcast_and_sends_the_type_and_the_automation()
    {
        await using var context = new NewsPageTestContext();
        NewNewsFeed? sent = null;
        context.News.Setup(n => n.CreateFeedAsync(It.IsAny<NewNewsFeed>(), It.IsAny<CancellationToken>()))
            .Callback((NewNewsFeed f, CancellationToken _) => sent = f)
            .ReturnsAsync(Ok(PodcastsPageTests.Podcast("Show")));
        var cut = RenderDialog(context, initialKind: NewsFeedKind.Podcast);

        Assert.Contains("New podcast", cut.Markup, StringComparison.Ordinal);
        Assert.Equal("true", cut.Find($"#{NewsFeedDialog.KindId} input[value='Podcast']").HasAttribute("checked") ? "true" : "false");
        Assert.Equal("Podcast settings", cut.Find("fieldset.pod-fieldset legend").TextContent.Trim());
        Assert.Equal(NewsFeedDialog.AutoDownloadHelpId, Switch(cut, "Download new episodes automatically").GetAttribute("aria-describedby"));
        Assert.Empty(cut.FindAll($"#{NewsFeedDialog.DaysId}, #{NewsFeedDialog.DaysId} input"));

        Switch(cut, "Download new episodes automatically").Change(true);
        Switch(cut, "Remove audio automatically").Change(true);
        cut.WaitForAssertion(() => Assert.Contains($"{PodcastLimits.MinAutoDeleteDays} to {PodcastLimits.MaxAutoDeleteDays:N0} days.", cut.Markup, StringComparison.Ordinal));
        cut.Find("input[type=url], input#nf-form-url, #nf-form-url input").Input("https://show.example/podcast.xml");

        Assert.True(await Submit(cut));
        Assert.Equal((NewsFeedKind.Podcast, true, true, PodcastLimits.DefaultAutoDeleteDays),
            (sent!.Kind, sent.AutoDownload, sent.AutoDeleteEnabled, sent.AutoDeleteAfterDays));
    }

    [Theory]
    [InlineData("", "Enter the number of days.")]
    [InlineData("2.5", "Enter a whole number of days.")]
    [InlineData("0", "Enter a number from")]
    public void The_day_count_is_checked_like_the_server(string value, string message) =>
        Assert.StartsWith(message, NewsFeedDialog.ValidateDays(value.Length == 0 ? null : decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public async Task Articles_selected_sends_the_automation_off_and_keeps_the_day_count()
    {
        var feed = PodcastsPageTests.Podcast("Show", f =>
        {
            f.Podcast!.AutoDownload = true;
            f.Podcast.AutoDeleteEnabled = true;
            f.Podcast.AutoDeleteAfterDays = 14;
            f.Kind = NewsFeedKind.Article;
        });
        await using var context = new NewsPageTestContext();
        UpdateNewsFeed? sent = null;
        context.News.Setup(n => n.UpdateFeedAsync(feed.Id, It.IsAny<UpdateNewsFeed>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, UpdateNewsFeed u, CancellationToken _) => sent = u)
            .ReturnsAsync(Ok(feed));
        var cut = RenderDialog(context, feed);

        Assert.True(await Submit(cut));

        Assert.Equal((NewsFeedKind.Article, false, false, 14), (sent!.Kind, sent.AutoDownload, sent.AutoDeleteEnabled, sent.AutoDeleteAfterDays));
    }

    [Fact]
    public async Task Switching_a_podcast_to_articles_is_confirmed_with_the_fresh_count_and_cancel_reverts_and_focuses_podcast()
    {
        var feed = PodcastsPageTests.Podcast("Show");
        var fresh = PodcastsPageTests.Podcast("Show", f => f.Podcast!.StoredAudioCount = 3);
        await using var context = new NewsPageTestContext();
        context.News.Setup(n => n.GetFeedAsync(feed.Id, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(fresh));
        var cut = RenderDialog(context, feed);

        cut.Find($"#{NewsFeedDialog.KindId} input[value='Article']").Change(true);
        Assert.False(await Submit(cut));

        cut.WaitForAssertion(() => Assert.Contains("Switch to articles?", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("Audio for 3 episodes will be removed from the server. Episodes and their show notes are kept.", cut.Markup, StringComparison.Ordinal);
        context.News.Verify(n => n.UpdateFeedAsync(It.IsAny<Guid>(), It.IsAny<UpdateNewsFeed>(), It.IsAny<CancellationToken>()), Times.Never);

        var confirm = cut.FindComponent<OdsConfirmDialog>();
        await cut.InvokeAsync(() => confirm.Instance.OpenChanged.InvokeAsync(false));

        cut.WaitForAssertion(() => Assert.True(cut.Find($"#{NewsFeedDialog.KindId} input[value='Podcast']").HasAttribute("checked")
            || cut.FindComponent<OdsRadioGroup>().Instance.Value == nameof(NewsFeedKind.Podcast)));
        Assert.Contains(context.Ctx.JSInterop.Invocations, i => i.Identifier == "odsFocusRadioGroup" && Equals(i.Arguments[0], NewsFeedDialog.KindId));
    }

    // ── The Feeds page ─────────────────────────────────────────────────────────────────────────────

    private static NewsPageTestContext FeedsContext(IReadOnlyList<ExistingNewsFeed> feeds)
    {
        var context = new NewsPageTestContext();
        context.News.Setup(n => n.ListFeedsAsync(1, NewsLimits.MaxPageSize, null, null, NewsFeedState.All, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(new PagedResult<ExistingNewsFeed> { Items = [.. feeds], TotalCount = feeds.Count }));
        context.NewsTags.Setup(n => n.ListAllAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new List<ExistingNewsTag>()));
        return context;
    }

    [Fact]
    public async Task Kind_podcast_ticks_the_type_filter_and_the_podcast_card_counts_episodes_and_views_them()
    {
        var article = NewsFeedsPageTests.Feed("Harbour Gazette");
        var podcast = PodcastsPageTests.Podcast("Tidewater Talks", f =>
        {
            f.Podcast!.Author = "Harbour Radio";
            f.Podcast.ArtworkId = Guid.NewGuid();
        });
        await using var context = FeedsContext([article, podcast]);
        context.Ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("news-feeds?kind=podcast");

        var cut = context.Render<NewsFeedsPage>();

        cut.WaitForAssertion(() => Assert.Equal(["Tidewater Talks"], cut.FindAll(".odc-record-name").Select(e => e.TextContent.Trim())));
        var card = cut.Find($"#nf-{podcast.Id}");
        Assert.Contains("Podcast", card.QuerySelector(".odc-chip")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("Harbour Radio", card.TextContent, StringComparison.Ordinal);
        Assert.Contains("episodes", card.TextContent, StringComparison.Ordinal);
        Assert.Contains("on the server", card.TextContent, StringComparison.Ordinal);
        Assert.StartsWith("http://localhost/api/podcast-artwork/", card.QuerySelector("img")!.GetAttribute("src"), StringComparison.Ordinal);
        var byType = cut.FindAll(".odc-summary-grid > *").Single(t => t.TextContent.Contains("By type", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Articles", byType.TextContent, StringComparison.Ordinal);
        Assert.Contains("Podcasts", byType.TextContent, StringComparison.Ordinal);

        var page = cut.FindComponent<NewsFeedsPage>().Instance;
        Assert.Equal("View episodes", page.MenuFor(podcast)[0].Label);
        Assert.Equal("View articles", page.MenuFor(article)[0].Label);
    }

    [Fact]
    public async Task An_expanded_podcast_card_shows_its_automation_tiles()
    {
        var podcast = PodcastsPageTests.Podcast("Tidewater Talks", f =>
        {
            f.Podcast!.AutoDownload = true;
            f.Podcast.AutoDeleteEnabled = true;
            f.Podcast.AutoDeleteAfterDays = 30;
        });
        await using var context = FeedsContext([podcast]);

        var cut = context.Render<NewsFeedsPage>();
        cut.WaitForElement($"#nf-{podcast.Id} .odc-record-trigger").Click();

        cut.WaitForAssertion(() => Assert.Contains("Auto-download", cut.Find($"#nf-{podcast.Id}").TextContent, StringComparison.Ordinal));
        Assert.Contains("After 30 days", cut.Find($"#nf-{podcast.Id}").TextContent, StringComparison.Ordinal);
        Assert.Contains("Audio on the server", cut.Find($"#nf-{podcast.Id}").TextContent, StringComparison.Ordinal);
    }

    // ── News ───────────────────────────────────────────────────────────────────────────────────────

    private static NewsPageTestContext NewsContext(params NewsFeedCatalogueEntry[] catalogue)
    {
        var context = new NewsPageTestContext();
        context.News.Setup(n => n.GetCatalogueAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Ok(catalogue.ToList()));
        context.NewsTags.Setup(n => n.ListAllAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new List<ExistingNewsTag>()));
        context.News.Setup(n => n.GetUnreadCountsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new NewsUnreadCounts()));
        context.News.Setup(n => n.ListArticlesAsync(It.IsAny<Odyssey.ApiClient.Resources.NewsArticleListFilter>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(new NewsArticlePage { Items = [], TotalCount = 0 }));
        return context;
    }

    private static readonly NewsFeedCatalogueEntry ArticleEntry =
        new() { Id = Guid.NewGuid(), DisplayTitle = "Harbour Gazette", Kind = NewsFeedKind.Article, ArticleCount = 3 };

    private static readonly NewsFeedCatalogueEntry PodcastEntry =
        new() { Id = Guid.NewGuid(), DisplayTitle = "Tidewater Talks", Kind = NewsFeedKind.Podcast, ArticleCount = 9 };

    [Fact]
    public async Task News_redirects_a_podcast_link_to_podcasts()
    {
        await using var context = NewsContext(ArticleEntry, PodcastEntry);
        var nav = context.Ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"news?feedId={PodcastEntry.Id}");

        var cut = context.Render<NewsPage>();

        cut.WaitForAssertion(() => Assert.EndsWith($"podcasts?feedId={PodcastEntry.Id}", nav.Uri, StringComparison.Ordinal));
        context.News.Verify(n => n.ListArticlesAsync(It.IsAny<Odyssey.ApiClient.Resources.NewsArticleListFilter>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task News_leaves_podcasts_out_of_its_feed_filter()
    {
        await using var context = NewsContext(ArticleEntry, PodcastEntry);

        var cut = context.Render<NewsPage>();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".nw-loading")));
        Assert.Equal(["Harbour Gazette"], cut.FindComponent<NewsPage>().Instance.FeedOptions.Select(o => o.Label));
    }

    // ── Background jobs ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_status_panel_counts_artwork_in_the_image_bar_and_adds_an_audio_bar_with_the_graced_bytes()
    {
        const long GiB = 1024L * 1024 * 1024;
        using var ctx = new BunitContext();
        ctx.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        var status = new NewsStatus
        {
            Storage = NewsStorageState.Ready,
            ImageBytes = 4 * GiB,
            ArtworkBytes = 1 * GiB,
            ImageQuotaBytes = 20 * GiB,
            TextAndSourceBytes = 1 * GiB,
            TextAndSourceQuotaBytes = 20 * GiB,
            AudioBytes = 9 * GiB,
            AudioPendingReleaseBytes = 1 * GiB,
            AudioQuotaBytes = 10 * GiB,
            AudioQuotaReached = true,
            TopFeedsByAudioBytes = [new() { FeedId = Guid.NewGuid(), DisplayTitle = "Tidewater Talks", AudioBytes = 9 * GiB }],
        };

        var cut = ctx.Render<NewsStatusPanel>(p => p.Add(c => c.Status, status));

        var bars = cut.FindAll("[role=progressbar]");
        Assert.Equal(3, bars.Count);
        Assert.Equal("25", bars[0].GetAttribute("aria-valuenow"));
        Assert.Equal("Audio", cut.Find($"#{bars[2].GetAttribute("aria-labelledby")}").TextContent.Trim());
        Assert.Equal($"Full, {NewsStatusPanel.GiB(10 * GiB)} of {NewsStatusPanel.GiB(10 * GiB)} GiB", bars[2].GetAttribute("aria-valuetext"));
        Assert.Contains($"{NewsStatusPanel.GiB(GiB)} GiB awaiting removal", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Largest podcasts by audio", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Artwork", cut.Markup, StringComparison.Ordinal);
    }
}
