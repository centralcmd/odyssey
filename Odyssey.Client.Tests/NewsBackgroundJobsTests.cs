using Bunit;
using MudBlazor.Services;
using Odyssey.Client.Pages.JobRuns;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Internet;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Background jobs' News pieces (issue #473 §3.6; AC 24, 38, 39): the status panel in its three storage states with
/// both quota bars, the news job card's warning lines, and the NewsFeedJobRun detail.
/// </summary>
public class NewsBackgroundJobsTests : IAsyncLifetime
{
    private readonly BunitContext ctx = new();

    public NewsBackgroundJobsTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ctx.DisposeAsync().AsTask();

    private const long GiB = 1L << 30;

    private static NewsStatus Status(NewsStorageState state = NewsStorageState.Ready, Action<NewsStatus>? edit = null)
    {
        var status = new NewsStatus
        {
            Storage = state, Volume = "news", ArticleCount = 1234, FeedCount = 18, QuarantinedFeedCount = 1,
            ImageBytes = 4 * GiB, ImageQuotaBytes = 20 * GiB, TextAndSourceBytes = 2 * GiB, TextAndSourceQuotaBytes = 20 * GiB,
            VolumeFreeBytes = 100 * GiB,
        };
        edit?.Invoke(status);
        return status;
    }

    [Theory]
    [InlineData(NewsStorageState.Ready, "Ready — storing articles and images")]
    [InlineData(NewsStorageState.Degraded, "Degraded — the file volume isn’t answering; articles keep loading, bodies and images may not")]
    [InlineData(NewsStorageState.Unconfigured, "Not set up — no file volume is bound to News")]
    public void The_panel_renders_in_every_storage_state(NewsStorageState state, string words)
    {
        var cut = ctx.Render<NewsStatusPanel>(p => p.Add(c => c.Status, Status(state)));

        Assert.Contains(words, cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Quarantined feeds", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(state == NewsStorageState.Unconfigured ? 0 : 2, cut.FindAll("[role=progressbar]").Count);
    }

    [Fact]
    public void Each_quota_bar_is_named_by_its_visible_label_and_states_its_value()
    {
        var cut = ctx.Render<NewsStatusPanel>(p => p.Add(c => c.Status, Status()));

        var bars = cut.FindAll("[role=progressbar]");
        var image = bars[0];
        Assert.Equal("Image storage", cut.Find($"#{image.GetAttribute("aria-labelledby")}").TextContent.Trim());
        Assert.Equal("0", image.GetAttribute("aria-valuemin"));
        Assert.Equal("100", image.GetAttribute("aria-valuemax"));
        Assert.Equal("20", image.GetAttribute("aria-valuenow"));
        Assert.Equal($"{NewsStatusPanel.GiB(4 * GiB)} of {NewsStatusPanel.GiB(20 * GiB)} GiB", image.GetAttribute("aria-valuetext"));
        Assert.Equal("Text storage", cut.Find($"#{bars[1].GetAttribute("aria-labelledby")}").TextContent.Trim());
    }

    [Fact]
    public void A_full_text_quota_reads_full_in_words_and_clamps_its_value()
    {
        var cut = ctx.Render<NewsStatusPanel>(p => p.Add(c => c.Status, Status(edit: s =>
        {
            s.TextAndSourceBytes = 25 * GiB;
            s.TextAndSourceQuotaReached = true;
        })));

        var text = cut.FindAll("[role=progressbar]")[1];
        Assert.Equal("100", text.GetAttribute("aria-valuenow"));
        Assert.Equal($"Full, {NewsStatusPanel.GiB(20 * GiB)} of {NewsStatusPanel.GiB(20 * GiB)} GiB", text.GetAttribute("aria-valuetext"));
        var visible = cut.FindAll(".bgj-quota")[1].TextContent;
        Assert.Contains("Full", visible, StringComparison.Ordinal);
        Assert.Contains("warning", visible, StringComparison.Ordinal);
    }

    [Fact]
    public void The_top_feeds_are_links_with_their_language_and_at_most_ten()
    {
        var feeds = Enumerable.Range(0, 12).Select(i => new NewsFeedImageUsage
        {
            FeedId = Guid.NewGuid(), DisplayTitle = $"Feed {i}", Language = i == 0 ? "nb" : null, ImageBytes = GiB,
        }).ToList();
        var cut = ctx.Render<NewsStatusPanel>(p => p.Add(c => c.Status, Status(edit: s => s.TopFeedsByImageBytes = feeds)));

        var links = cut.FindAll(".bgj-news-top a");
        Assert.Equal(NewsStatusPanel.TopFeeds, links.Count);
        Assert.Equal("nb", links[0].GetAttribute("lang"));
        Assert.Null(links[1].GetAttribute("lang"));
        Assert.Equal($"news-feeds?state=All&reveal={feeds[0].FeedId}", links[0].GetAttribute("href"));
    }

    [Fact]
    public void The_news_job_card_has_one_warning_line_per_quota_reached()
    {
        Assert.Empty(BackgroundJobTile.QuotaWarnings(JobType.NewsFeedFetch, Status()));
        Assert.Empty(BackgroundJobTile.QuotaWarnings(JobType.Weather, Status(edit: s => s.TextAndSourceQuotaReached = true)));
        Assert.Empty(BackgroundJobTile.QuotaWarnings(JobType.NewsFeedFetch, null));
        Assert.Equal(
            ["Image storage quota reached", "Text storage quota reached"],
            BackgroundJobTile.QuotaWarnings(JobType.NewsFeedFetch, Status(edit: s => { s.ImageQuotaReached = true; s.TextAndSourceQuotaReached = true; })));
    }

    [Fact]
    public void The_run_detail_shows_the_counters_and_a_manual_run_its_feeds_marked_due()
    {
        NewsFeedJobRunDetail Run(JobRunTrigger trigger) => new()
        {
            Id = Guid.NewGuid(), JobType = JobType.NewsFeedFetch, Trigger = trigger, Status = JobRunStatus.Succeeded,
            StartedAtUtc = DateTime.UtcNow, FeedsFetched = 4, ItemsStored = 12, ItemsRevised = 2, ImagesStored = 9, FeedsQuarantined = 0, FeedsMarked = 3,
        };

        var scheduled = ctx.Render<JobRunDetailNewsFeedFetch>(p => p.Add(c => c.Run, Run(JobRunTrigger.Scheduled)));
        var labels = scheduled.FindAll(".odc-meta-tile-label").Select(e => e.TextContent.Trim()).ToList();
        Assert.Equal(["Feeds fetched", "Items stored", "Items revised", "Images stored", "Feeds quarantined"], labels.Take(5));
        Assert.DoesNotContain("Feeds marked due", labels);

        var manual = ctx.Render<JobRunDetailNewsFeedFetch>(p => p.Add(c => c.Run, Run(JobRunTrigger.Manual)));
        Assert.Equal("Feeds marked due", manual.FindAll(".odc-meta-tile-label").First().TextContent.Trim());
    }
}
