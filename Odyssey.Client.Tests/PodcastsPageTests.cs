using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Pages.Internet;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Internet;
using Xunit;
using static Odyssey.Client.Tests.NewsPageTestContext;

namespace Odyssey.Client.Tests;

/// <summary>
/// <c>/podcasts</c> (issue #559 §3.2; design system ui_kits/web/Podcasts.jsx): the header's sums over the podcast
/// blocks, the overview's tag and saved tiles, the card's chips and spoken forms, the Status filter, the deep link, the
/// audio actions with their claims, cooldown and focus, the Remove dialog, the archived refusal and the polling.
/// </summary>
public class PodcastsPageTests
{
    private static readonly string[] Listener =
        [.. AllNewsClaims, PermissionClaims.PodcastAudioCreate, PermissionClaims.PodcastAudioDelete];

    private static readonly string[] Guest = [PermissionClaims.NewsRead];

    private static readonly Guid TagId = Guid.NewGuid();

    internal static ExistingNewsFeed Podcast(string title, Action<ExistingNewsFeed>? edit = null)
    {
        var feed = NewsFeedsPageTests.Feed(title, f =>
        {
            f.Kind = NewsFeedKind.Podcast;
            f.Podcast = new NewsFeedPodcast { EpisodeCount = 10, SavedEpisodeCount = 4, StoredAudioCount = 1, StoredAudioBytes = 47L * 1024 * 1024 };
        });
        edit?.Invoke(feed);
        return feed;
    }

    internal static PodcastEpisodeSummary Episode(ExistingNewsFeed feed, string title, PodcastAudioState state = PodcastAudioState.None,
        Action<PodcastEpisodeSummary>? edit = null)
    {
        var episode = new PodcastEpisodeSummary
        {
            Id = Guid.NewGuid(),
            FeedId = feed.Id,
            FeedTitle = feed.DisplayTitle,
            Title = title,
            PublishedAtUtc = Now.UtcDateTime.AddHours(-5),
            SortAtUtc = Now.UtcDateTime.AddHours(-5),
            DurationSeconds = 3127,
            SeasonNumber = 2,
            EpisodeNumber = 14,
            Audio = new PodcastEpisodeAudioInfo
            {
                State = state,
                SizeBytes = state == PodcastAudioState.Stored ? 47L * 1024 * 1024 : null,
                ContentType = state == PodcastAudioState.Stored ? "audio/mpeg" : null,
            },
        };
        edit?.Invoke(episode);
        return episode;
    }

    private static PodcastEpisodeDetail Detail(PodcastEpisodeSummary e, Action<PodcastEpisodeDetail>? edit = null)
    {
        var detail = new PodcastEpisodeDetail
        {
            Id = e.Id,
            FeedId = e.FeedId,
            FeedTitle = e.FeedTitle,
            Title = e.Title,
            PublishedAtUtc = e.PublishedAtUtc,
            SortAtUtc = e.SortAtUtc,
            DurationSeconds = e.DurationSeconds,
            SeasonNumber = e.SeasonNumber,
            EpisodeNumber = e.EpisodeNumber,
            EpisodeType = e.EpisodeType,
            Audio = e.Audio,
            LastPlayedAtUtc = e.LastPlayedAtUtc,
            Description = "About it.",
        };
        edit?.Invoke(detail);
        return detail;
    }

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(IReadOnlyList<ExistingNewsFeed> feeds, IReadOnlyList<PodcastEpisodeSummary> episodes, IReadOnlyList<string>? claims = null,
            List<ExistingNewsTag>? tags = null)
        {
            Context = new NewsPageTestContext(claims ?? Listener);
            Episodes = [.. episodes];
            Context.Podcasts.Setup(p => p.ListFeedsAsync(1, NewsLimits.MaxPageSize, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Ok(new PagedResult<ExistingNewsFeed> { Items = [.. feeds], TotalCount = feeds.Count }));
            Context.NewsTags.Setup(n => n.ListAllAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok(tags ?? []));
            Context.Podcasts.Setup(p => p.ListEpisodesAsync(It.IsAny<PodcastEpisodeListFilter>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((PodcastEpisodeListFilter filter, int _, string? _, CancellationToken _) =>
                {
                    Filters.Add(filter);
                    if (filter.AudioStates?.Count == PodcastAudioPoller.PolledStates.Count && filter.IncludeArchivedFeeds)
                    {
                        return PollResult ?? Ok(new PodcastEpisodePage { Items = [.. Episodes.Where(e => OdsInFlight(e.Audio.State))] });
                    }

                    return Ok(new PodcastEpisodePage { Items = [.. Episodes], TotalCount = Episodes.Count, AsOfUtc = Now.UtcDateTime });
                });
            Context.Podcasts.Setup(p => p.GetEpisodeAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid id, CancellationToken _) => Details.TryGetValue(id, out var d) ? Ok(d)
                    : Episodes.FirstOrDefault(e => e.Id == id) is { } e ? Ok(Detail(e)) : Fail<PodcastEpisodeDetail>(HttpStatusCode.NotFound));
        }

        private static bool OdsInFlight(PodcastAudioState s) => Odyssey.Client.Components.OdsPodcastAudioState.InFlight(s);

        public NewsPageTestContext Context { get; }

        public List<PodcastEpisodeSummary> Episodes { get; }

        public Dictionary<Guid, PodcastEpisodeDetail> Details { get; } = [];

        public List<PodcastEpisodeListFilter> Filters { get; } = [];

        public ApiResult<PodcastEpisodePage>? PollResult { get; set; }

        public IRenderedComponent<Bunit.Rendering.ContainerFragment> Render(Guid? feedId = null)
        {
            if (feedId is { } id)
            {
                Context.Ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo($"podcasts?feedId={id}");
            }

            var cut = Context.Render<PodcastsPage>();
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".nw-loading")));
            return cut;
        }

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    private static string Polite(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut) =>
        string.Concat(cut.FindAll("[aria-live=polite]").Select(e => e.TextContent));

    private static void Expand(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, PodcastEpisodeSummary episode)
    {
        cut.Find($"#pod-{episode.Id} .odc-record-trigger").Click();
        cut.WaitForElement($"#pod-{episode.Id} .pod-audio");
    }

    private static AngleSharp.Dom.IElement Action(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, PodcastAudioAction action, PodcastEpisodeSummary episode) =>
        cut.Find($"#{PodcastAudioActions.ActionId(action, episode.Id)}");

    // ── Header and overview ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_header_sums_every_podcast_and_the_overview_counts_tags_and_saved_episodes()
    {
        var tag = new ExistingNewsTag { NewsTagId = TagId, Name = "Local" };
        var live = Podcast("Tidewater Talks", f => f.TagIds = [TagId]);
        var archived = Podcast("Garden Hour", f => { f.ArchivedAtUtc = Now.UtcDateTime; f.IsActive = false; f.Health = NewsFeedHealth.Archived; });
        await using var page = new Harness([live, archived], [Episode(live, "One")], tags: [tag]);

        var cut = page.Render();

        Assert.Contains("2 podcasts · 2 episodes on the server (94.0 MB)", cut.Markup, StringComparison.Ordinal);
        var episodes = cut.FindAll(".odc-summary-grid > *").Single(t => t.TextContent.Contains("Episodes", StringComparison.Ordinal) && t.TextContent.Contains("Saved to a device", StringComparison.Ordinal));
        Assert.Contains("8", episodes.TextContent, StringComparison.Ordinal);
        Assert.Contains("12", episodes.TextContent, StringComparison.Ordinal);
        var byTags = cut.FindAll(".odc-summary-grid > *").Single(t => t.TextContent.Contains("By tags", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Local", byTags.TextContent, StringComparison.Ordinal);
        Assert.Contains("No tag", byTags.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_podcasts_offers_new_podcast_with_the_claim_and_a_sentence_without()
    {
        await using (var page = new Harness([], []))
        {
            var cut = page.Render();
            Assert.Contains("No podcasts yet", cut.Markup, StringComparison.Ordinal);
            Assert.Contains(cut.FindAll("button"), b => b.TextContent.Contains("New podcast", StringComparison.Ordinal));
        }

        await using (var page = new Harness([], [], Guest))
        {
            var cut = page.Render();
            Assert.Contains("Ask an administrator or owner to add one.", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("New podcast", StringComparison.Ordinal));
        }
    }

    // ── Cards ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_card_shows_its_state_saved_and_type_chips_and_reads_each_abbreviation_once()
    {
        var feed = Podcast("Tidewater Talks");
        var episode = Episode(feed, "Fog horns", PodcastAudioState.Stored, e =>
        {
            e.LastPlayedAtUtc = Now.UtcDateTime.AddHours(-1);
            e.EpisodeType = PodcastEpisodeType.Bonus;
            e.Explicit = true;
        });
        await using var page = new Harness([feed], [episode]);

        var cut = page.Render();

        var card = cut.Find($"#pod-{episode.Id}");
        Assert.Contains("On the server", card.QuerySelector(".odc-podaudio")!.TextContent, StringComparison.Ordinal);
        var chips = card.QuerySelectorAll(".odc-chip").Select(c => c.TextContent.Trim()).ToList();
        Assert.Contains("Saved to a device", chips.Single(c => c.Contains("Saved", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("Bonus", chips);
        Assert.Contains("Explicit", chips);
        Assert.Contains(card.QuerySelectorAll("[aria-hidden=true]"), e => e.TextContent == "52 min");
        Assert.Contains(card.QuerySelectorAll(".odc-sr-only"), e => e.TextContent == "52 minutes");
        Assert.Contains(card.QuerySelectorAll(".odc-sr-only"), e => e.TextContent == "Season 2, episode 14");
        Assert.Equal("3", card.QuerySelector("[role=heading]")!.GetAttribute("aria-level"));
    }

    [Fact]
    public async Task Open_the_audio_panel_tiles_and_sections_follow_the_fixed_order()
    {
        var feed = Podcast("Tidewater Talks");
        var episode = Episode(feed, "Logbooks", PodcastAudioState.Stored, e => e.LastPlayedAtUtc = Now.UtcDateTime.AddHours(-1));
        await using var page = new Harness([feed], [episode]);
        page.Details[episode.Id] = Detail(episode, d =>
        {
            d.ShowNotes = "Notes.";
            d.Chapters = [new() { StartMs = 724_000, Title = "The winter of 1881", Url = "https://show.example/1881" }];
            d.ChaptersSource = PodcastChapterSource.Json;
        });

        var cut = page.Render();
        Expand(cut, episode);

        cut.WaitForAssertion(() => Assert.Contains("Chapters", cut.Markup, StringComparison.OrdinalIgnoreCase));
        var card = cut.Find($"#pod-{episode.Id}");
        Assert.Contains("On the server · 47.0 MB · MP3.", card.QuerySelector(".pod-audio-line")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("Last saved to a device", card.TextContent, StringComparison.Ordinal);
        var headings = card.QuerySelectorAll("[aria-level='4']").Select(h => h.TextContent.Trim()).ToList();
        Assert.Equal(["About this episode", "Chapters", "Show notes"], headings);
        Assert.Contains("From the publisher’s chapter file", card.TextContent, StringComparison.Ordinal);
        Assert.Equal("PT12M4S", card.QuerySelector(".pod-chapter time")!.GetAttribute("datetime"));
    }

    [Fact]
    public async Task A_guest_sees_states_and_save_to_device_only()
    {
        var feed = Podcast("Tidewater Talks");
        var stored = Episode(feed, "Stored", PodcastAudioState.Stored);
        await using var page = new Harness([feed], [stored], Guest);

        var cut = page.Render();
        Expand(cut, stored);

        var buttons = cut.FindAll($"#pod-{stored.Id} .pod-audio-actions button").Select(b => b.GetAttribute("aria-label")).ToList();
        Assert.Equal(["Save to device, Stored"], buttons);
        Assert.DoesNotContain("Last saved to a device", cut.Find($"#pod-{stored.Id}").TextContent, StringComparison.Ordinal);
    }

    // ── Filters and the deep link ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_restored_status_maps_to_audio_states_and_the_saved_member()
    {
        var feed = Podcast("Tidewater Talks");
        await using var page = new Harness([feed], [Episode(feed, "One")]);
        page.Context.State.Setup(s => s.LoadAsync<PodcastsPage.PodcastsPageState>(PodcastsPage.PageStateKey))
            .ReturnsAsync(new PodcastsPage.PodcastsPageState { Statuses = ["Stored", PodcastsPage.SavedKey, "Bogus"] });

        page.Render();

        var filter = page.Filters[0];
        Assert.Equal([PodcastAudioState.Stored], filter.AudioStates);
        Assert.True(filter.SavedToDevice);
        Assert.False(filter.IncludeArchivedFeeds);
    }

    [Fact]
    public async Task A_deep_link_preselects_its_podcast_and_a_non_podcast_id_is_dropped()
    {
        var feed = Podcast("Tidewater Talks");
        await using (var page = new Harness([feed], [Episode(feed, "One")]))
        {
            page.Render(feed.Id);
            Assert.Equal([feed.Id], page.Filters[0].FeedIds);
        }

        await using (var page = new Harness([feed], [Episode(feed, "One")]))
        {
            page.Render(Guid.NewGuid());
            Assert.Null(page.Filters[0].FeedIds);
        }
    }

    // ── Audio actions ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Download_to_server_queues_moves_focus_to_cancel_and_says_so()
    {
        var feed = Podcast("Tidewater Talks");
        var episode = Episode(feed, "Ferry timetables");
        await using var page = new Harness([feed], [episode]);
        page.Context.Podcasts.Setup(p => p.RequestAudioAsync(episode.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(new PodcastEpisodeAudioInfo { State = PodcastAudioState.Queued, RequestedBy = PodcastAudioRequestedBy.User }));

        var cut = page.Render();
        Expand(cut, episode);
        Assert.Equal("Download to server, Ferry timetables", Action(cut, PodcastAudioAction.Download, episode).GetAttribute("aria-label"));
        Action(cut, PodcastAudioAction.Download, episode).Click();

        cut.WaitForAssertion(() => Assert.Contains("Queued for download to the server.", cut.Find($"#pod-{episode.Id} .pod-audio-line").TextContent, StringComparison.Ordinal));
        cut.WaitForAssertion(() => Assert.Contains("Queued for download to the server.", Polite(cut), StringComparison.Ordinal));
        Assert.Contains(page.Context.Ctx.JSInterop.Invocations, i => i.Identifier == "odsFocusById"
            && Equals(i.Arguments[0], PodcastAudioActions.ActionId(PodcastAudioAction.Cancel, episode.Id)));
    }

    [Fact]
    public async Task During_the_cooldown_download_is_aria_disabled_and_described_then_comes_back_and_is_announced()
    {
        var feed = Podcast("Tidewater Talks");
        var episode = Episode(feed, "Season three", edit: e => e.Audio.RequestAvailableAtUtc = Now.UtcDateTime.AddMinutes(14));
        await using var page = new Harness([feed], [episode]);

        var cut = page.Render();
        Expand(cut, episode);

        var button = Action(cut, PodcastAudioAction.Download, episode);
        Assert.Equal("true", button.GetAttribute("aria-disabled"));
        Assert.Equal($"pod-cd-{episode.Id}", button.GetAttribute("aria-describedby"));
        Assert.Contains("Available again at", cut.Find($"#pod-cd-{episode.Id}").TextContent, StringComparison.Ordinal);
        button.Click();
        page.Context.Podcasts.Verify(p => p.RequestAudioAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);

        page.Context.Time.Advance(TimeSpan.FromMinutes(15));

        cut.WaitForAssertion(() => Assert.Null(Action(cut, PodcastAudioAction.Download, episode).GetAttribute("aria-disabled")));
        Assert.Empty(cut.FindAll($"#pod-cd-{episode.Id}"));
        cut.WaitForAssertion(() => Assert.Contains("Download to server is available again for Season three.", Polite(cut), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Remove_from_server_confirms_then_toasts_and_shows_the_cooldown()
    {
        var feed = Podcast("Tidewater Talks");
        var episode = Episode(feed, "Logbooks", PodcastAudioState.Stored);
        await using var page = new Harness([feed], [episode]);
        page.Context.Podcasts.Setup(p => p.RemoveAudioAsync(episode.Id, It.IsAny<CancellationToken>())).ReturnsAsync(ApiResult.Success(HttpStatusCode.NoContent));

        var cut = page.Render();
        Expand(cut, episode);
        Action(cut, PodcastAudioAction.Remove, episode).Click();
        cut.WaitForAssertion(() => Assert.Contains("Remove audio from the server?", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("The episode, its show notes and chapters stay.", cut.Markup, StringComparison.Ordinal);
        page.Details[episode.Id] = Detail(episode, d => d.Audio = new PodcastEpisodeAudioInfo
        {
            State = PodcastAudioState.None,
            RequestAvailableAtUtc = Now.UtcDateTime.AddMinutes(10),
        });

        cut.FindAll(".mud-dialog button").Single(b => b.TextContent.Trim().EndsWith("Remove", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains("Audio removed from the server.", cut.Markup, StringComparison.Ordinal));
        cut.WaitForAssertion(() => Assert.Equal("true", Action(cut, PodcastAudioAction.Download, episode).GetAttribute("aria-disabled")));
        page.Context.Podcasts.Verify(p => p.RemoveAudioAsync(episode.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task An_archived_podcast_refusal_is_a_card_alert_with_a_link_to_feeds()
    {
        var feed = Podcast("Garden Hour");
        var episode = Episode(feed, "Pruning");
        await using var page = new Harness([feed], [episode]);
        page.Context.Podcasts.Setup(p => p.RequestAudioAsync(episode.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<PodcastEpisodeAudioInfo>(HttpStatusCode.UnprocessableEntity, NewsErrorCodes.FeedInactive));

        var cut = page.Render();
        Expand(cut, episode);
        Action(cut, PodcastAudioAction.Download, episode).Click();

        cut.WaitForAssertion(() => Assert.Contains("This podcast is archived. Unarchive it on the Feeds page to download audio.",
            cut.Find($"#pod-{episode.Id} [role=alert]").TextContent, StringComparison.Ordinal));
        Assert.Equal($"news-feeds?state=All&reveal={feed.Id}", cut.Find($"#pod-{episode.Id} [role=alert] a").GetAttribute("href"));
    }

    // ── Polling ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_poll_rereads_an_episode_that_left_the_in_flight_set_and_announces_it_once()
    {
        var feed = Podcast("Tidewater Talks");
        var episode = Episode(feed, "Logbooks", PodcastAudioState.Downloading);
        await using var page = new Harness([feed], [episode]);
        page.PollResult = Ok(new PodcastEpisodePage { Items = [] });
        page.Details[episode.Id] = Detail(episode, d => d.Audio = new PodcastEpisodeAudioInfo { State = PodcastAudioState.Stored, SizeBytes = 1024 * 1024, ContentType = "audio/mpeg" });

        var cut = page.Render();
        page.Context.Time.Advance(PodcastAudioPoller.ActiveInterval);

        cut.WaitForAssertion(() => Assert.Contains("On the server", cut.Find($"#pod-{episode.Id} .odc-podaudio").TextContent, StringComparison.Ordinal));
        var poll = page.Filters.Single(f => f.IncludeArchivedFeeds);
        Assert.Equal([feed.Id], poll.FeedIds);
        Assert.Equal(PodcastAudioPoller.PolledStates, poll.AudioStates);
        cut.WaitForAssertion(() => Assert.Contains("Logbooks is on the server and ready to save.", Polite(cut), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Three_failed_polls_put_the_line_on_in_flight_cards_and_check_again_retries()
    {
        var feed = Podcast("Tidewater Talks");
        var episode = Episode(feed, "Logbooks", PodcastAudioState.Queued);
        await using var page = new Harness([feed], [episode]);
        page.PollResult = Fail<PodcastEpisodePage>(HttpStatusCode.InternalServerError);

        var cut = page.Render();
        Expand(cut, episode);
        page.Context.Time.Advance(PodcastAudioPoller.ActiveInterval);
        cut.WaitForAssertion(() => Assert.Equal(1, page.Filters.Count(f => f.IncludeArchivedFeeds)));
        Assert.DoesNotContain("Couldn’t refresh the download status.", cut.Markup, StringComparison.Ordinal);
        page.Context.Time.Advance(TimeSpan.FromSeconds(20));
        cut.WaitForAssertion(() => Assert.Equal(2, page.Filters.Count(f => f.IncludeArchivedFeeds)));
        page.Context.Time.Advance(TimeSpan.FromSeconds(40));

        cut.WaitForAssertion(() => Assert.Contains("Couldn’t refresh the download status.", cut.Find($"#pod-{episode.Id} .pod-audio").TextContent, StringComparison.Ordinal));
        cut.WaitForAssertion(() => Assert.Contains("Couldn’t refresh the download status.", Polite(cut), StringComparison.Ordinal));

        page.PollResult = null;
        cut.FindAll($"#pod-{episode.Id} .pod-audio-trouble button").Single().Click();
        cut.WaitForAssertion(() => Assert.DoesNotContain("Couldn’t refresh the download status.", cut.Find($"#pod-{episode.Id} .pod-audio").TextContent, StringComparison.Ordinal));
    }
}
