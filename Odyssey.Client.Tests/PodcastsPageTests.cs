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
            LastListenedAtUtc = e.LastListenedAtUtc,
            PlaybackPositionSeconds = e.PlaybackPositionSeconds,
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
    public async Task A_guest_sees_states_play_and_save_to_device_only()
    {
        var feed = Podcast("Tidewater Talks");
        var stored = Episode(feed, "Stored", PodcastAudioState.Stored);
        await using var page = new Harness([feed], [stored], Guest);

        var cut = page.Render();
        Expand(cut, stored);

        var buttons = cut.FindAll($"#pod-{stored.Id} .pod-audio-actions button").Select(b => b.GetAttribute("aria-label")).ToList();
        // Play is news.read, like the stream it loads (#594 §3); a guest plays but never saves.
        Assert.Equal(["Play, Stored", "Save to device, Stored"], buttons);
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

    [Theory]
    [InlineData(new[] { PodcastsPage.SavedKey }, true)]
    [InlineData(new[] { PodcastsPage.UnsavedKey }, false)]
    [InlineData(new[] { PodcastsPage.SavedKey, PodcastsPage.UnsavedKey }, null)]
    [InlineData(new string[0], null)]
    public async Task The_page_sends_the_device_members_and_the_search_on_its_list_query(string[] statuses, bool? saved)
    {
        var feed = Podcast("Tidewater Talks");
        await using var page = new Harness([feed], [Episode(feed, "One")]);
        page.Context.State.Setup(s => s.LoadAsync<PodcastsPage.PodcastsPageState>(PodcastsPage.PageStateKey))
            .ReturnsAsync(new PodcastsPage.PodcastsPageState { Statuses = [.. statuses], Search = "  ferry  " });

        page.Render();

        var filter = page.Filters[0];
        Assert.Equal(saved, filter.SavedToDevice);
        Assert.Null(filter.AudioStates);
        Assert.Equal("ferry", filter.Search);
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

    [Fact]
    public async Task The_pressed_action_stays_focusable_while_its_request_runs()
    {
        var feed = Podcast("Tidewater Talks");
        var episode = Episode(feed, "Ferry timetables");
        await using var page = new Harness([feed], [episode]);
        var pending = new TaskCompletionSource<ApiResult<PodcastEpisodeAudioInfo>>();
        page.Context.Podcasts.Setup(p => p.RequestAudioAsync(episode.Id, It.IsAny<CancellationToken>())).Returns(pending.Task);

        var cut = page.Render();
        Expand(cut, episode);
        Action(cut, PodcastAudioAction.Download, episode).Click();

        cut.WaitForAssertion(() =>
        {
            var button = Action(cut, PodcastAudioAction.Download, episode);
            Assert.Equal("true", button.GetAttribute("aria-disabled"));
            Assert.False(button.HasAttribute("disabled"));
        });
        Action(cut, PodcastAudioAction.Download, episode).Click();
        page.Context.Podcasts.Verify(p => p.RequestAudioAsync(episode.Id, It.IsAny<CancellationToken>()), Times.Once);

        pending.SetResult(Fail<PodcastEpisodeAudioInfo>(HttpStatusCode.InternalServerError));
        cut.WaitForAssertion(() => Assert.Null(Action(cut, PodcastAudioAction.Download, episode).GetAttribute("aria-disabled")));
    }

    [Fact]
    public async Task Save_to_device_downloads_natively_never_requests_and_rereads_the_saved_episode()
    {
        var feed = Podcast("Tidewater Talks");
        var episode = Episode(feed, "Logbooks", PodcastAudioState.Stored);
        await using var page = new Harness([feed], [episode]);
        var url = $"api/podcast-episodes/{episode.Id}/audio";
        page.Context.Podcasts.Setup(p => p.AudioUrl(episode.Id)).Returns(url);

        var cut = page.Render();
        Expand(cut, episode);
        Assert.DoesNotContain("Saved to a device", cut.Find($"#pod-{episode.Id}").TextContent, StringComparison.Ordinal);
        page.Details[episode.Id] = Detail(episode, d => d.LastPlayedAtUtc = Now.UtcDateTime);
        Action(cut, PodcastAudioAction.Save, episode).Click();

        page.Context.Transfer.Verify(t => t.DownloadAsync(url), Times.Once);
        page.Context.Podcasts.Verify(p => p.RequestAudioAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        cut.WaitForAssertion(() => Assert.Contains("Saved to a device", cut.Find($"#pod-{episode.Id}").TextContent, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, NewsErrorCodes.PodcastAudioBusy, "The audio is downloading right now. Try again when it’s finished.")]
    [InlineData(HttpStatusCode.TooManyRequests, NewsErrorCodes.PodcastAudioCooldown, "You can try again at ")]
    [InlineData((HttpStatusCode)507, NewsErrorCodes.PodcastAudioQuotaReached, "The audio storage limit is reached. Remove audio from the server, or ask an administrator to raise the limit in Settings.")]
    [InlineData(HttpStatusCode.Forbidden, null, "You no longer have permission to do this.")]
    [InlineData(HttpStatusCode.InternalServerError, null, "Couldn’t do that. Try again.")]
    public async Task A_refused_download_is_a_card_alert_naming_why(HttpStatusCode status, string? code, string text)
    {
        var feed = Podcast("Garden Hour");
        var episode = Episode(feed, "Pruning");
        await using var page = new Harness([feed], [episode]);
        page.Context.Podcasts.Setup(p => p.RequestAudioAsync(episode.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<PodcastEpisodeAudioInfo>(status, code, status == HttpStatusCode.TooManyRequests ? TimeSpan.FromMinutes(5) : null));

        var cut = page.Render();
        Expand(cut, episode);
        if (status == HttpStatusCode.TooManyRequests)
        {
            // The refusal re-reads the episode, whose cooldown then describes the button.
            page.Details[episode.Id] = Detail(episode, d => d.Audio = new PodcastEpisodeAudioInfo
            {
                State = PodcastAudioState.None,
                RequestAvailableAtUtc = Now.UtcDateTime.AddMinutes(5),
            });
        }

        Action(cut, PodcastAudioAction.Download, episode).Click();

        cut.WaitForAssertion(() => Assert.Contains(text, cut.Find($"#pod-{episode.Id} [role=alert]").TextContent, StringComparison.Ordinal));
        if (status == HttpStatusCode.TooManyRequests)
        {
            cut.WaitForAssertion(() => Assert.Contains("Available again at", cut.Find($"#pod-cd-{episode.Id}").TextContent, StringComparison.Ordinal));
            Assert.Equal("true", Action(cut, PodcastAudioAction.Download, episode).GetAttribute("aria-disabled"));
        }
    }

    [Fact]
    public async Task A_refusal_for_an_episode_that_is_gone_removes_its_card_and_says_so()
    {
        var feed = Podcast("Garden Hour");
        var episode = Episode(feed, "Pruning");
        await using var page = new Harness([feed], [episode]);
        page.Context.Podcasts.Setup(p => p.RequestAudioAsync(episode.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<PodcastEpisodeAudioInfo>(HttpStatusCode.NotFound));

        var cut = page.Render();
        Expand(cut, episode);
        Action(cut, PodcastAudioAction.Download, episode).Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll($"#pod-{episode.Id}")));
        cut.WaitForAssertion(() => Assert.Contains("This episode is no longer available.", Polite(cut), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancel_releases_the_request_says_so_and_focuses_download()
    {
        var feed = Podcast("Tidewater Talks");
        var episode = Episode(feed, "Logbooks", PodcastAudioState.Queued);
        await using var page = new Harness([feed], [episode]);
        page.Context.Podcasts.Setup(p => p.RemoveAudioAsync(episode.Id, It.IsAny<CancellationToken>())).ReturnsAsync(ApiResult.Success(HttpStatusCode.NoContent));

        var cut = page.Render();
        Expand(cut, episode);
        page.Details[episode.Id] = Detail(episode, d => d.Audio = new PodcastEpisodeAudioInfo { State = PodcastAudioState.None });
        Action(cut, PodcastAudioAction.Cancel, episode).Click();

        cut.WaitForAssertion(() => Assert.Contains("Download cancelled.", Polite(cut), StringComparison.Ordinal));
        cut.WaitForAssertion(() => Assert.Contains("Not downloaded", cut.Find($"#pod-{episode.Id} .odc-podaudio").TextContent, StringComparison.Ordinal));
        Assert.Contains(page.Context.Ctx.JSInterop.Invocations, i => i.Identifier == "odsFocusById"
            && Equals(i.Arguments[0], PodcastAudioActions.ActionId(PodcastAudioAction.Download, episode.Id)));
    }

    [Fact]
    public async Task Cancel_after_the_claim_was_lost_is_a_card_alert_and_keeps_the_card()
    {
        var feed = Podcast("Tidewater Talks");
        var episode = Episode(feed, "Logbooks", PodcastAudioState.Deferred);
        await using var page = new Harness([feed], [episode]);
        page.Context.Podcasts.Setup(p => p.RemoveAudioAsync(episode.Id, It.IsAny<CancellationToken>())).ReturnsAsync(Fail(HttpStatusCode.Forbidden));

        var cut = page.Render();
        Expand(cut, episode);
        Action(cut, PodcastAudioAction.Cancel, episode).Click();

        cut.WaitForAssertion(() => Assert.Contains("You no longer have permission to do this.", cut.Find($"#pod-{episode.Id} [role=alert]").TextContent, StringComparison.Ordinal));
        Assert.Contains("Waiting for space", cut.Find($"#pod-{episode.Id} .odc-podaudio").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Try_again_on_a_failed_download_queues_it_again()
    {
        var feed = Podcast("Tidewater Talks");
        var episode = Episode(feed, "Logbooks", PodcastAudioState.Failed, e => e.Audio.FailureKind = PodcastAudioFailureKind.TooLarge);
        await using var page = new Harness([feed], [episode]);
        page.Context.Podcasts.Setup(p => p.RequestAudioAsync(episode.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(new PodcastEpisodeAudioInfo { State = PodcastAudioState.Queued, RequestedBy = PodcastAudioRequestedBy.User }));

        var cut = page.Render();
        Expand(cut, episode);
        Assert.Contains("Couldn’t download: the file is larger than the server allows.", cut.Find($"#pod-{episode.Id} .pod-audio-line").TextContent, StringComparison.Ordinal);
        Action(cut, PodcastAudioAction.Retry, episode).Click();

        cut.WaitForAssertion(() => Assert.Contains("Queued", cut.Find($"#pod-{episode.Id} .odc-podaudio").TextContent, StringComparison.Ordinal));
        page.Context.Podcasts.Verify(p => p.RequestAudioAsync(episode.Id, It.IsAny<CancellationToken>()), Times.Once);
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
    public async Task A_hidden_tab_pauses_polling_and_a_visible_one_resumes_it()
    {
        var feed = Podcast("Tidewater Talks");
        var episode = Episode(feed, "Logbooks", PodcastAudioState.Downloading);
        await using var page = new Harness([feed], [episode]);
        page.Context.Ctx.JSInterop.Setup<bool>("odsPageHidden").SetResult(true);

        var cut = page.Render();
        page.Context.Time.Advance(PodcastAudioPoller.ActiveInterval);
        cut.WaitForAssertion(() => Assert.Contains(page.Context.Ctx.JSInterop.Invocations, i => i.Identifier == "odsPageHidden"));
        Assert.DoesNotContain(page.Filters, f => f.IncludeArchivedFeeds);

        page.Context.Ctx.JSInterop.Setup<bool>("odsPageHidden").SetResult(false);
        page.Context.Time.Advance(PodcastAudioPoller.ActiveInterval);
        cut.WaitForAssertion(() => Assert.Single(page.Filters, f => f.IncludeArchivedFeeds));
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

    // ── In-browser playback (issue #594) ───────────────────────────────────────────────────────────

    private static Bunit.BunitJSModuleInterop PlayerModule(Harness page, bool canPlay = true)
    {
        var module = page.Context.Ctx.JSInterop.SetupModule(Odyssey.Client.Components.OdsPodcastPlayer.ModulePath);
        module.Setup<bool>("canPlay", _ => true).SetResult(canPlay);
        module.Setup<string>("start", _ => true).SetResult("ok");
        module.Setup<string>("play", _ => true).SetResult("ok");
        module.Setup<int>("readVolume", _ => true).SetResult(80);
        page.Context.Podcasts.Setup(p => p.AudioStreamUrl(It.IsAny<Guid>())).Returns((Guid id) => $"http://localhost/api/podcast-episodes/{id}/audio/stream");
        page.Context.Podcasts.Setup(p => p.AudioStreamPathPattern).Returns("^/api/podcast-episodes/[0-9a-f-]{36}/audio/stream$");
        return module;
    }

    [Fact]
    public async Task Playback_a_stored_episode_leads_with_resume_and_save_to_device_turns_outlined()
    {
        var feed = Podcast("Tidewater Talks");
        var stored = Episode(feed, "Logbooks", PodcastAudioState.Stored, e => { e.PlaybackPositionSeconds = 754; e.DurationSeconds = 3127; });
        await using var page = new Harness([feed], [stored]);
        PlayerModule(page);

        var cut = page.Render();
        Expand(cut, stored);

        var buttons = cut.FindAll($"#pod-{stored.Id} .pod-audio-actions button");
        Assert.Equal("Resume from 12:34, Logbooks", buttons[0].GetAttribute("aria-label"));
        Assert.Contains("Save to device", buttons[1].TextContent, StringComparison.Ordinal);
        Assert.Contains("outlined", buttons[1].ClassName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Playback_play_starts_the_stream_shows_the_player_and_announces_it()
    {
        var feed = Podcast("Tidewater Talks");
        var stored = Episode(feed, "Logbooks", PodcastAudioState.Stored, e => e.DurationSeconds = 3127);
        await using var page = new Harness([feed], [stored]);
        var module = PlayerModule(page);

        var cut = page.Render();
        Expand(cut, stored);
        Action(cut, PodcastAudioAction.Play, stored).Click();

        cut.WaitForElement($"#pod-{stored.Id} .odc-podplayer");
        Assert.Equal("Pause, Logbooks", cut.Find($"#{PodcastAudioActions.PlayerToggleId(stored.Id)}").GetAttribute("aria-label"));
        Assert.Empty(cut.FindAll($"#{PodcastAudioActions.ActionId(PodcastAudioAction.Play, stored.Id)}"));
        page.Context.Podcasts.Verify(p => p.AudioStreamUrl(stored.Id), Times.AtLeastOnce());
        page.Context.Podcasts.Verify(p => p.AudioUrl(It.IsAny<Guid>()), Times.Never());
        Assert.Contains(module.Invocations, i => i.Identifier == "start");
        cut.WaitForAssertion(() => Assert.Contains("Playing.", Polite(cut), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Playback_a_collapsed_playing_card_shows_playing_and_a_pause_button()
    {
        var feed = Podcast("Tidewater Talks");
        var stored = Episode(feed, "Logbooks", PodcastAudioState.Stored, e => e.DurationSeconds = 3127);
        await using var page = new Harness([feed], [stored]);
        PlayerModule(page);

        var cut = page.Render();
        Expand(cut, stored);
        Action(cut, PodcastAudioAction.Play, stored).Click();
        cut.WaitForElement($"#pod-{stored.Id} .odc-podplayer");
        cut.Find($"#pod-{stored.Id} .odc-record-trigger").Click();

        cut.WaitForAssertion(() => Assert.Contains("Playing", cut.Find($"#pod-{stored.Id}").TextContent, StringComparison.Ordinal));
        Assert.Equal("Pause, Logbooks", cut.Find($"#{PodcastEpisodeCard.HeaderToggleId(stored.Id)}").GetAttribute("aria-label"));
    }

    [Fact]
    public async Task Playback_an_unsupported_format_removes_play_and_says_why()
    {
        var feed = Podcast("Tidewater Talks");
        var stored = Episode(feed, "Logbooks", PodcastAudioState.Stored, e => e.Audio.ContentType = "audio/ogg");
        await using var page = new Harness([feed], [stored]);
        var module = PlayerModule(page, canPlay: false);

        var cut = page.Render();
        Expand(cut, stored);
        Action(cut, PodcastAudioAction.Play, stored).Click();

        cut.WaitForElement($"#{PodcastAudioActions.UnsupportedId(stored.Id)}");
        Assert.Empty(cut.FindAll($"#{PodcastAudioActions.ActionId(PodcastAudioAction.Play, stored.Id)}"));
        Assert.DoesNotContain(module.Invocations, i => i.Identifier == "start");
        cut.WaitForAssertion(() => Assert.Contains("can’t play this audio format", Polite(cut), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Playback_a_finished_listen_shows_the_listened_chip_and_tile()
    {
        var feed = Podcast("Tidewater Talks");
        var stored = Episode(feed, "Logbooks", PodcastAudioState.Stored, e => e.LastListenedAtUtc = Now.UtcDateTime.AddDays(-1));
        await using var page = new Harness([feed], [stored]);
        PlayerModule(page);

        var cut = page.Render();
        Assert.Contains("Listened", cut.Find($"#pod-{stored.Id}").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Played", cut.Find($"#pod-{stored.Id}").TextContent, StringComparison.Ordinal);
        Expand(cut, stored);
        Assert.Contains("Last listened", cut.Find($"#pod-{stored.Id}").TextContent, StringComparison.Ordinal);
    }
}
