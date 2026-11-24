using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Moq;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Auth;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Internet;
using Odyssey.Dtos.Internet;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The in-browser podcast player (issue #594): <see cref="OdsPodcastPlayer"/>'s accessibility contract, the shared
/// <see cref="OdsPodcastPlayback"/> rules, and <see cref="PodcastPlaybackSession"/>'s save queue on a fake clock.
/// </summary>
public class PodcastPlayerTests : IAsyncDisposable
{
    private readonly BunitContext ctx = new();

    public PodcastPlayerTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
    }

    public async ValueTask DisposeAsync()
    {
        await ctx.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private IRenderedComponent<OdsPodcastPlayer> Player(
        Action<ComponentParameterCollectionBuilder<OdsPodcastPlayer>>? more = null,
        OdsPodcastPlayerState state = OdsPodcastPlayerState.Paused, double current = 754, double? duration = 3127) =>
        ctx.Render<OdsPodcastPlayer>(p =>
        {
            p.Add(x => x.Title, "Fog horns");
            p.Add(x => x.State, state);
            p.Add(x => x.CurrentSeconds, current);
            p.Add(x => x.DurationSeconds, duration);
            p.Add(x => x.Volume, 70);
            more?.Invoke(p);
        });

    // ── The shared rules ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, 3127.0, "Play")]
    [InlineData(754, 3127.0, "Resume from 12:34")]
    [InlineData(3124, 3127.0, "Play")]
    [InlineData(3124, null, "Resume from 52:04")]
    [InlineData(4000, 3600.0, "Play")]
    public void The_play_label_resumes_unless_near_a_known_end(int? position, double? length, string expected) =>
        Assert.Equal(expected, OdsPodcastPlayback.PlayActionLabel(position, length));

    [Fact]
    public void Clock_and_spoken_forms_cover_hours_and_unknown()
    {
        Assert.Equal("1:13:30", OdsPodcastPlayback.Clock(4410));
        Assert.Equal("0:05", OdsPodcastPlayback.Clock(5));
        Assert.Equal("--:--", OdsPodcastPlayback.Clock(double.PositiveInfinity));
        Assert.Equal("--:--", OdsPodcastPlayback.Clock(null));
        Assert.Equal("1 hour 13 minutes 30 seconds", OdsPodcastPlayback.Spoken(4410));
        Assert.Equal("0 seconds", OdsPodcastPlayback.Spoken(0));
        Assert.False(OdsPodcastPlayback.LengthKnown(0));
        Assert.False(OdsPodcastPlayback.LengthKnown(double.NaN));
    }

    // ── The view ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Names_carry_state_and_title_and_the_times_are_hidden_behind_the_slider_value()
    {
        var cut = Player();

        Assert.Equal("Player, Fog horns", cut.Find(".odc-podplayer").GetAttribute("aria-label"));
        Assert.Equal("group", cut.Find(".odc-podplayer").GetAttribute("role"));
        Assert.Equal("Play, Fog horns", cut.Find(".odc-podplayer-toggle").GetAttribute("aria-label"));
        Assert.Null(cut.Find(".odc-podplayer-toggle").GetAttribute("aria-pressed"));
        var seek = cut.Find(".odc-podplayer-seek");
        Assert.Equal("Position", seek.GetAttribute("aria-label"));
        Assert.Equal("12 minutes 34 seconds of 52 minutes 7 seconds", seek.GetAttribute("aria-valuetext"));
        Assert.All(cut.FindAll(".odc-podplayer-time"), t => Assert.Equal("true", t.GetAttribute("aria-hidden")));
        Assert.Equal("Mute", cut.Find(".odc-podplayer-mute").GetAttribute("aria-label"));
        Assert.Equal("70 percent", cut.Find(".odc-podplayer-vol").GetAttribute("aria-valuetext"));

        cut.Render(p => p.Add(x => x.State, OdsPodcastPlayerState.Playing).Add(x => x.Muted, true));
        Assert.Equal("Pause, Fog horns", cut.Find(".odc-podplayer-toggle").GetAttribute("aria-label"));
        Assert.Equal("Unmute", cut.Find(".odc-podplayer-mute").GetAttribute("aria-label"));
        Assert.Equal("70 percent, muted", cut.Find(".odc-podplayer-vol").GetAttribute("aria-valuetext"));
    }

    [Fact]
    public void Playback_ticks_never_change_the_slider_value_text_but_a_pause_and_focus_do()
    {
        var cut = Player(state: OdsPodcastPlayerState.Playing);
        var before = cut.Find(".odc-podplayer-seek").GetAttribute("aria-valuetext");

        cut.Render(p => p.Add(x => x.CurrentSeconds, 800));
        Assert.Equal(before, cut.Find(".odc-podplayer-seek").GetAttribute("aria-valuetext"));

        cut.Find(".odc-podplayer-seek").Focus();
        Assert.Equal("13 minutes 20 seconds of 52 minutes 7 seconds", cut.Find(".odc-podplayer-seek").GetAttribute("aria-valuetext"));

        cut.Render(p => p.Add(x => x.CurrentSeconds, 900).Add(x => x.State, OdsPodcastPlayerState.Paused));
        Assert.Equal("15 minutes of 52 minutes 7 seconds", cut.Find(".odc-podplayer-seek").GetAttribute("aria-valuetext"));
    }

    [Fact]
    public void An_unknown_length_disables_the_slider_and_says_so()
    {
        var cut = Player(current: 95, duration: double.PositiveInfinity);

        var seek = cut.Find(".odc-podplayer-seek");
        Assert.Equal("true", seek.GetAttribute("aria-disabled"));
        Assert.Equal("1 minute 35 seconds elapsed, length unknown", seek.GetAttribute("aria-valuetext"));
        Assert.Contains("--:--", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Loading_is_busy_but_focusable_and_the_status_line_follows_the_state()
    {
        var cut = Player(state: OdsPodcastPlayerState.Loading);
        var toggle = cut.Find(".odc-podplayer-toggle");
        Assert.Equal(("true", "true"), (toggle.GetAttribute("aria-busy"), toggle.GetAttribute("aria-disabled")));
        Assert.False(toggle.HasAttribute("disabled"));
        Assert.Equal("Loading audio…", cut.Find(".odc-podplayer-status").TextContent);

        cut.Render(p => p.Add(x => x.State, OdsPodcastPlayerState.Blocked).Add(x => x.SaveNote, OdsPodcastPlayerSaveNote.Trouble));
        Assert.Equal("Press play to start. Couldn’t save your place.", cut.Find(".odc-podplayer-status").TextContent);
    }

    [Theory]
    [InlineData(OdsPodcastPlayerError.Network, true, "alert")]
    [InlineData(OdsPodcastPlayerError.Removed, false, "alert")]
    [InlineData(OdsPodcastPlayerError.Decode, false, "alert")]
    [InlineData(OdsPodcastPlayerError.Forbidden, false, "alert")]
    [InlineData(OdsPodcastPlayerError.Unsupported, false, "status")]
    public void Only_a_network_failure_offers_try_again(OdsPodcastPlayerError error, bool retry, string role)
    {
        var cut = Player(p => p.Add(x => x.Error, error).Add(x => x.OnRetry, () => { }).Add(x => x.RetryId, "retry").Add(x => x.ContentTypeLabel, "Ogg"));

        var alert = cut.Find(".odc-podplayer-alert");
        Assert.Equal(role, alert.GetAttribute("role"));
        Assert.Equal("-1", alert.GetAttribute("tabindex"));
        Assert.Equal(retry, cut.FindAll("#retry").Count == 1);
        if (error == OdsPodcastPlayerError.Unsupported)
        {
            Assert.Contains("(Ogg)", alert.TextContent, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_committed_seek_and_a_volume_change_reach_the_page()
    {
        int? seek = null;
        int? volume = null;
        var cut = Player(p => p.Add(x => x.OnSeek, v => seek = v).Add(x => x.OnVolume, v => volume = v));

        cut.Find(".odc-podplayer-seek").Change("1800");
        cut.Find(".odc-podplayer-vol").Change("35");

        Assert.Equal((1800, 35), (seek, volume));
        Assert.Equal("30 minutes of 52 minutes 7 seconds", cut.Find(".odc-podplayer-seek").GetAttribute("aria-valuetext"));
    }

    // ── The session's save queue ───────────────────────────────────────────────────────────────────

    private sealed class Host : IPodcastPlaybackHost
    {
        public List<string> Said { get; } = [];

        public List<Guid> Ended { get; } = [];

        public OdsPodcastPlayerError? Classify { get; set; } = OdsPodcastPlayerError.Network;

        public Task ChangedAsync() => Task.CompletedTask;

        public void Say(string text) => Said.Add(text);

        public Task EndedAsync(Guid episodeId, bool saved)
        {
            Ended.Add(episodeId);
            return Task.CompletedTask;
        }

        public Task<OdsPodcastPlayerError?> ClassifyFailureAsync(Guid episodeId) => Task.FromResult(Classify);

        public Task FocusAsync(string id) => Task.CompletedTask;
    }

    private sealed record Saved(Guid Id, int? Position, bool Completed);

    private (PodcastPlaybackSession Session, Host Host, List<Saved> Saves, FakeTimeProvider Clock, Mock<IPodcastsApiClient> Api) Session(
        HttpStatusCode status = HttpStatusCode.NoContent)
    {
        var module = ctx.JSInterop.SetupModule(OdsPodcastPlayer.ModulePath);
        module.Setup<string>("start", _ => true).SetResult("ok");
        module.Setup<string>("play", _ => true).SetResult("ok");
        module.Setup<int>("readVolume", _ => true).SetResult(70);
        module.Setup<bool>("canPlay", _ => true).SetResult(true);
        var api = new Mock<IPodcastsApiClient>();
        var saves = new List<Saved>();
        api.Setup(a => a.AudioStreamUrl(It.IsAny<Guid>())).Returns((Guid id) => $"http://localhost/api/podcast-episodes/{id}/audio/stream");
        api.Setup(a => a.AudioStreamPathPattern).Returns("^/api/podcast-episodes/[0-9a-f-]{36}/audio/stream$");
        api.Setup(a => a.SavePlaybackAsync(It.IsAny<Guid>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, int? position, bool completed, CancellationToken _) =>
            {
                saves.Add(new Saved(id, position, completed));
                return status == HttpStatusCode.NoContent ? ApiResult.Success(status) : ApiResult.Failure(status, new ApiProblem { Status = (int)status });
            });
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero));
        var host = new Host();
        var tokens = new AntiforgeryTokenStore(new ServiceCollection().AddSingleton(new HttpClient(new StubHandler()) { BaseAddress = new Uri("http://localhost/") }).BuildServiceProvider());
        return (new PodcastPlaybackSession(ctx.JSInterop.JSRuntime, api.Object, tokens, clock, host), host, saves, clock, api);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"token":"t"}""", System.Text.Encoding.UTF8, "application/json") });
    }

    private static PodcastEpisodeSummary Episode(int? position = 754) => new()
    {
        Id = Guid.NewGuid(),
        Title = "Fog horns",
        DurationSeconds = 3127,
        PlaybackPositionSeconds = position,
        Audio = new PodcastEpisodeAudioInfo { State = PodcastAudioState.Stored, ContentType = "audio/mpeg" },
    };

    private static async Task TickAsync(PodcastPlaybackSession session, FakeTimeProvider clock, int seconds, double from)
    {
        for (var i = 1; i <= seconds; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await session.OnPlayerEvent("time", from + i, 3127, null);
        }
    }

    [Fact]
    public async Task Starting_resumes_at_the_saved_position_and_plays()
    {
        var (session, host, _, _, _) = Session();
        var episode = Episode();

        await session.StartAsync(episode, "Fog horns", canSave: true);

        Assert.Equal((episode.Id, OdsPodcastPlayerState.Playing, 754d, 70), (session.EpisodeId!.Value, session.State, session.CurrentSeconds, session.Volume));
        Assert.Contains("Playing.", host.Said);
    }

    [Fact]
    public async Task Periodic_saves_come_every_fifteen_seconds_of_playback_and_never_closer_than_ten()
    {
        var (session, _, saves, clock, _) = Session();
        await session.StartAsync(Episode(), "Fog horns", canSave: true);

        await TickAsync(session, clock, 14, 754);
        Assert.Empty(saves);
        await TickAsync(session, clock, 1, 768);
        Assert.Single(saves);
        Assert.Equal((769, false), (saves[0].Position, saves[0].Completed));
        await TickAsync(session, clock, 14, 769);
        Assert.Single(saves);
        await TickAsync(session, clock, 1, 783);
        Assert.Equal(2, saves.Count);
    }

    [Fact]
    public async Task A_pause_just_before_the_end_sends_only_the_finish_and_nothing_follows_it()
    {
        var (session, host, saves, clock, _) = Session();
        var episode = Episode();
        await session.StartAsync(episode, "Fog horns", canSave: true);

        await session.OnPlayerEvent("pause", 3127, 3127, null);
        await session.OnPlayerEvent("ended", 3127, 3127, null);
        clock.Advance(PodcastPlayerTiming.PauseBeforeEndedGrace * 2);
        await TickAsync(session, clock, 30, 0);

        Assert.Equal([new Saved(episode.Id, null, true)], saves);
        Assert.Equal(OdsPodcastPlayerState.Ended, session.State);
        Assert.Equal("Finished. Listened just now.", session.Status);
        Assert.Contains("Finished Fog horns.", host.Said);
        Assert.Equal([episode.Id], host.Ended);
    }

    [Fact]
    public async Task A_pause_on_its_own_saves_after_the_grace()
    {
        var (session, _, saves, clock, _) = Session();
        var episode = Episode();
        await session.StartAsync(episode, "Fog horns", canSave: true);

        await session.OnPlayerEvent("pause", 1000, 3127, null);
        Assert.Empty(saves);
        clock.Advance(PodcastPlayerTiming.PauseBeforeEndedGrace);

        Assert.Equal([new Saved(episode.Id, 1000, false)], saves);
    }

    [Fact]
    public async Task A_reader_without_news_update_plays_and_never_saves()
    {
        var (session, _, saves, clock, _) = Session();
        await session.StartAsync(Episode(), "Fog horns", canSave: false);

        await TickAsync(session, clock, 40, 754);
        await session.OnPlayerEvent("ended", 3127, 3127, null);
        await session.StopAsync();

        Assert.Empty(saves);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_refused_save_stops_saving_and_says_so_once(HttpStatusCode status)
    {
        var (session, host, saves, clock, _) = Session(status);
        await session.StartAsync(Episode(), "Fog horns", canSave: true);

        await TickAsync(session, clock, 45, 754);

        Assert.Single(saves);
        Assert.Equal(OdsPodcastPlayerSaveNote.Off, session.SaveNote);
        Assert.Single(host.Said, s => s == "Your place isn’t being saved.");
    }

    [Fact]
    public async Task Three_failed_saves_show_the_trouble_note_once()
    {
        var (session, host, saves, clock, _) = Session(HttpStatusCode.InternalServerError);
        await session.StartAsync(Episode(), "Fog horns", canSave: true);

        await TickAsync(session, clock, 60, 754);

        Assert.True(saves.Count >= 3);
        Assert.Equal(OdsPodcastPlayerSaveNote.Trouble, session.SaveNote);
        Assert.Single(host.Said, s => s == "Couldn’t save your place.");
    }

    [Fact]
    public async Task A_finish_refused_as_not_stored_shows_the_audio_removed()
    {
        var (session, _, _, _, _) = Session(HttpStatusCode.Conflict);
        await session.StartAsync(Episode(), "Fog horns", canSave: true);

        await session.OnPlayerEvent("ended", 3127, 3127, null);

        Assert.Equal(OdsPodcastPlayerError.Removed, session.Error);
    }

    [Fact]
    public async Task A_decode_error_is_shown_and_an_aborted_one_is_ignored()
    {
        var (session, _, _, _, _) = Session();
        await session.StartAsync(Episode(), "Fog horns", canSave: true);

        await session.OnPlayerEvent("error", 10, 3127, "1");
        Assert.Null(session.Error);
        await session.OnPlayerEvent("error", 10, 3127, "3");
        Assert.Equal(OdsPodcastPlayerError.Decode, session.Error);
    }

    [Fact]
    public async Task Stopping_saves_the_position_and_releases_the_episode()
    {
        var (session, _, saves, _, _) = Session();
        var episode = Episode();
        await session.StartAsync(episode, "Fog horns", canSave: true);
        await session.OnPlayerEvent("time", 1500, 3127, null);

        await session.StopAsync();

        Assert.Null(session.EpisodeId);
        Assert.Equal([new Saved(episode.Id, 1500, false)], saves);
    }

    [Fact]
    public async Task An_autoplay_refusal_is_the_blocked_state_and_says_press_play()
    {
        var (session, host, _, _, _) = Session();
        ctx.JSInterop.SetupModule(OdsPodcastPlayer.ModulePath).Setup<string>("start", _ => true).SetResult("NotAllowedError");

        await session.StartAsync(Episode(), "Fog horns", canSave: true);

        Assert.Equal(OdsPodcastPlayerState.Blocked, session.State);
        Assert.Contains("Press play to start.", host.Said);
    }

    [Theory]
    [InlineData(OdsPodcastPlayerError.Network)]
    [InlineData(OdsPodcastPlayerError.Removed)]
    [InlineData(OdsPodcastPlayerError.Forbidden)]
    public async Task A_network_or_source_error_takes_the_pages_classification(OdsPodcastPlayerError kind)
    {
        var (session, host, _, _, _) = Session();
        host.Classify = kind;
        await session.StartAsync(Episode(), "Fog horns", canSave: true);

        await session.OnPlayerEvent("error", 10, 3127, "2");

        Assert.Equal((kind, OdsPodcastPlayerState.Paused), (session.Error!.Value, session.State));
    }

    [Fact]
    public async Task A_signed_out_stream_stops_the_session_without_an_error()
    {
        var (session, host, _, _, _) = Session();
        host.Classify = null;
        await session.StartAsync(Episode(), "Fog horns", canSave: true);

        await session.OnPlayerEvent("error", 10, 3127, "4");

        Assert.Null(session.EpisodeId);
        Assert.Null(session.Error);
    }

    [Fact]
    public async Task A_module_that_fails_to_start_shows_the_network_error_not_a_spinner()
    {
        var (session, _, _, _, _) = Session();
        ctx.JSInterop.SetupModule(OdsPodcastPlayer.ModulePath).Setup<string>("start", _ => true).SetException(new Microsoft.JSInterop.JSException("import failed"));

        await session.StartAsync(Episode(), "Fog horns", canSave: true);

        Assert.Equal((OdsPodcastPlayerState.Paused, OdsPodcastPlayerError.Network), (session.State, session.Error!.Value));
    }
}
