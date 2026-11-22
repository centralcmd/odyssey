using Bunit;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Internet;
using Odyssey.Dtos.Internet;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The podcast pages' pure pieces (issue #559): the audio-state registry and its status lines, the visible and spoken
/// forms, the polling rules, and the Status filter's device members.
/// </summary>
public class PodcastUnitTests
{
    // ── Registry ───────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(PodcastAudioState.None, "Not downloaded", "outline")]
    [InlineData(PodcastAudioState.Queued, "Queued", "info")]
    [InlineData(PodcastAudioState.Downloading, "Downloading", "info")]
    [InlineData(PodcastAudioState.Stored, "On the server", "income")]
    [InlineData(PodcastAudioState.Failed, "Download failed", "error")]
    [InlineData(PodcastAudioState.Deferred, "Waiting for space", "warning")]
    public void Every_state_has_a_text_label_and_a_semantic_tone(PodcastAudioState state, string label, string tone)
    {
        var meta = OdsPodcastAudioState.Meta(state);
        Assert.Equal((label, tone, false), (meta.Label, meta.Tone, meta.Unknown));
    }

    [Fact]
    public void An_unknown_state_is_neutral_under_its_own_name_and_never_polled()
    {
        var meta = OdsPodcastAudioState.Meta(PodcastAudioState.Unknown);

        Assert.Equal(("Unknown", "outline", true), (meta.Label, meta.Tone, meta.Unknown));
        Assert.False(OdsPodcastAudioState.InFlight(PodcastAudioState.Unknown));
        Assert.Equal([PodcastAudioState.Queued, PodcastAudioState.Downloading, PodcastAudioState.Deferred],
            OdsPodcastAudioState.Order.Where(OdsPodcastAudioState.InFlight));
    }

    [Theory]
    [InlineData(PodcastAudioState.Deferred, null, PodcastAudioDeferredReason.AudioQuota, "Waiting: the audio storage limit is reached.")]
    [InlineData(PodcastAudioState.Deferred, null, PodcastAudioDeferredReason.DiskHeadroom, "Waiting: the server is low on disk space.")]
    [InlineData(PodcastAudioState.Failed, PodcastAudioFailureKind.TooLarge, null, "Couldn’t download: the file is larger than the server allows.")]
    [InlineData(PodcastAudioState.Failed, PodcastAudioFailureKind.Unknown, null, "Couldn’t download: an unknown error.")]
    [InlineData(PodcastAudioState.Queued, null, null, "Queued for download to the server.")]
    public void The_status_line_names_the_reason(PodcastAudioState state, PodcastAudioFailureKind? failure, PodcastAudioDeferredReason? reason, string line) =>
        Assert.Equal(line, OdsPodcastAudioState.StatusLine(new PodcastEpisodeAudioInfo { State = state, FailureKind = failure, DeferredReason = reason }));

    [Fact]
    public void The_chip_says_the_state_in_text_and_hides_its_glyph()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<PodcastAudioStateChip>(p => p.Add(c => c.State, PodcastAudioState.Stored));

        var chip = cut.Find(".odc-chip.odc-podaudio.income");
        Assert.Equal("true", chip.QuerySelector(".material-icons")!.GetAttribute("aria-hidden"));
        Assert.Contains("On the server", chip.TextContent, StringComparison.Ordinal);
    }

    // ── Format ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Abbreviations_have_spoken_forms()
    {
        Assert.Equal(new PodcastSpoken("52 min", "52 minutes", "PT52M7S"), PodcastFormat.Duration(3127));
        Assert.Equal(new PodcastSpoken("1 h 13 min", "1 hour 13 minutes", "PT1H13M30S"), PodcastFormat.Duration(4410));
        Assert.Equal(new PodcastSpoken("45 s", "45 seconds", "PT45S"), PodcastFormat.Duration(45));
        Assert.Null(PodcastFormat.Duration(null));
        Assert.Equal(new PodcastSpoken("12:04", "12 minutes 4 seconds", "PT12M4S"), PodcastFormat.Clock(724_000));
        Assert.Equal(new PodcastSpoken("1:02:09", "1 hour 2 minutes 9 seconds", "PT1H2M9S"), PodcastFormat.Clock(3_729_000));
        Assert.Equal(new PodcastSpoken("S2 · E14", "Season 2, episode 14"), PodcastFormat.SeasonEpisode(2, 14));
        Assert.Equal(new PodcastSpoken("E62", "Episode 62"), PodcastFormat.SeasonEpisode(null, 62));
        Assert.Null(PodcastFormat.SeasonEpisode(null, null));
    }

    [Theory]
    [InlineData("audio/mpeg", "MP3")]
    [InlineData("audio/mp4", "M4A")]
    [InlineData("audio/ogg; codecs=opus", "Ogg")]
    [InlineData("application/octet-stream", "Audio")]
    [InlineData(null, "Audio")]
    public void The_stored_type_reads_as_a_word(string? contentType, string word) => Assert.Equal(word, PodcastFormat.Type(contentType));

    [Fact]
    public void The_stored_line_names_size_and_type() =>
        Assert.Equal("On the server · 47.0 MB · MP3.",
            PodcastFormat.StoredLine(new PodcastEpisodeAudioInfo { State = PodcastAudioState.Stored, SizeBytes = 47L * 1024 * 1024, ContentType = "audio/mpeg" }));

    // ── The Status filter's device members ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(new string[0], null)]
    [InlineData(new[] { PodcastsPage.SavedKey }, true)]
    [InlineData(new[] { PodcastsPage.UnsavedKey }, false)]
    [InlineData(new[] { PodcastsPage.SavedKey, PodcastsPage.UnsavedKey }, null)]
    [InlineData(new[] { "Stored" }, null)]
    public void Saved_and_not_saved_or_together_and_both_mean_either(string[] statuses, bool? expected) =>
        Assert.Equal(expected, PodcastsPage.SavedFilter(statuses));

    // ── Polling ────────────────────────────────────────────────────────────────────────────────────

    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_active_download_polls_every_ten_seconds_and_nothing_in_flight_polls_never()
    {
        var poller = new PodcastAudioPoller();

        Assert.Equal(PodcastAudioPoller.ActiveInterval, poller.NextDelay([PodcastAudioState.Queued, PodcastAudioState.Deferred], T0));
        Assert.Null(poller.NextDelay([PodcastAudioState.Stored, PodcastAudioState.None, PodcastAudioState.Unknown], T0));
    }

    [Fact]
    public void Only_waiting_polls_every_minute_and_stops_after_thirty_minutes_until_check_again()
    {
        var poller = new PodcastAudioPoller();

        Assert.Equal(PodcastAudioPoller.WaitingInterval, poller.NextDelay([PodcastAudioState.Deferred], T0));
        Assert.Equal(PodcastAudioPoller.WaitingInterval, poller.NextDelay([PodcastAudioState.Deferred], T0.AddMinutes(29)));
        Assert.Null(poller.NextDelay([PodcastAudioState.Deferred], T0.AddMinutes(30)));
        Assert.True(poller.WaitStopped);

        poller.CheckAgain();
        Assert.False(poller.WaitStopped);
        Assert.Equal(PodcastAudioPoller.WaitingInterval, poller.NextDelay([PodcastAudioState.Deferred], T0.AddMinutes(31)));
    }

    [Fact]
    public void Failures_back_off_and_the_third_one_shows_the_line_once()
    {
        var poller = new PodcastAudioPoller();

        Assert.False(poller.Failed(500, null));
        Assert.Equal(TimeSpan.FromSeconds(20), poller.Backoff);
        Assert.False(poller.Failed(null, null));
        Assert.Equal(TimeSpan.FromSeconds(40), poller.Backoff);
        Assert.True(poller.Failed(404, null));
        Assert.True(poller.Trouble);
        Assert.False(poller.Failed(500, null));
        Assert.Equal(TimeSpan.FromSeconds(160), poller.Backoff);
        poller.Failed(500, null);
        poller.Failed(500, null);
        Assert.Equal(PodcastAudioPoller.MaxWait, poller.Backoff);

        poller.Succeeded();
        Assert.False(poller.Trouble);
        Assert.Equal(PodcastAudioPoller.ActiveInterval, poller.NextDelay([PodcastAudioState.Downloading], T0));
    }

    [Fact]
    public void A_busy_server_is_waited_out_capped_at_five_minutes_and_a_lost_claim_stops_for_good()
    {
        var poller = new PodcastAudioPoller();

        poller.Failed(429, TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(30), poller.Backoff);
        poller.Failed(503, TimeSpan.FromHours(1));
        Assert.Equal(PodcastAudioPoller.MaxWait, poller.Backoff);

        poller.Failed(403, null);
        Assert.True(poller.Stopped);
        Assert.Null(poller.NextDelay([PodcastAudioState.Queued], T0));
    }

    [Fact]
    public void More_than_fifty_podcasts_are_polled_round_robin_in_list_order()
    {
        var poller = new PodcastAudioPoller();
        var feeds = Enumerable.Range(0, PodcastAudioPoller.MaxFeedsPerTick + 10).Select(_ => Guid.NewGuid()).ToList();

        var first = poller.FeedsForTick(feeds);
        var second = poller.FeedsForTick(feeds);
        var third = poller.FeedsForTick(feeds);

        Assert.Equal(feeds.Take(PodcastAudioPoller.MaxFeedsPerTick), first);
        Assert.Equal(feeds.Skip(PodcastAudioPoller.MaxFeedsPerTick), second);
        Assert.Equal(first, third);
    }
}
