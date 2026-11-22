using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Context;
using Odyssey.Core.Internet.News.Fetching;
using Odyssey.Core.Internet.News.Podcasts;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;
using Odyssey.Testing.News;
using Xunit;
using static Odyssey.Testing.News.PodcastAudioFixtures;

namespace Odyssey.Core.Tests.Internet;

/// <summary>
/// The audio download job on EF InMemory (issue #556 §3.5, §3.6, AC 16–19, 29): a stored download with its chapter
/// markers and counted bytes; detection by magic bytes; the caps, the quota and the identity-encoding rule; back-off on
/// a transient failure; expired-lease recovery; one download per feed per run; a long redirect chain; and the stall
/// and floor limits of the streaming entry point.
/// </summary>
public sealed class PodcastAudioDownloadTests : IAsyncLifetime
{
    private const string FeedUrl = "https://show.example.org/podcast.xml";
    private const string Audio1 = "https://cdn.example.org/1.mp3";
    private const string Audio2 = "https://cdn.example.org/2.mp3";

    private static readonly InMemoryDatabaseRoot DatabaseRoot = new();

    private readonly string databaseName = $"podcast-audio-{Guid.NewGuid():N}";
    private readonly DirectoryInfo volume = Directory.CreateTempSubdirectory("odyssey-podcast-audio-");
    private NewsJobHarness news = null!;

    private void UseDatabase(DbContextOptionsBuilder<OdysseyContext> options) =>
        options.UseInMemoryDatabase(databaseName, DatabaseRoot).ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));

    public async Task InitializeAsync() => news = await NewsJobHarness.CreateAsync(
        UseDatabase, volume.FullName, configureServices: services => services.AddSingleton<PodcastAudioDownloadJob>());

    public async Task DisposeAsync()
    {
        await news.DisposeAsync();
        volume.Delete(recursive: true);
    }

    private PodcastAudioDownloadJob Job => news.Host.Services.GetRequiredService<PodcastAudioDownloadJob>();

    private static string Show(params string[] audio) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd">
        <channel><title>The Show</title><link>https://show.example.org/</link>
        {string.Concat(audio.Select((url, i) => $"""
            <item><guid isPermaLink="false">ep-{i}</guid><title>Episode {i}</title><description>Notes.</description>
            <pubDate>Sun, 2{i} Oct 2026 07:00:00 GMT</pubDate><enclosure url="{url}" type="audio/mpeg" length="10"/></item>
            """))}
        </channel></rss>
        """;

    /// <summary>A podcast feed with its episodes ingested and every audio row queued by a user.</summary>
    private async Task<Guid> QueuedAsync(params string[] audio)
    {
        news.Server.Feed(FeedUrl, Show(audio));
        var feedId = await news.AddFeedAsync(FeedUrl);
        await news.WithContextAsync(async c =>
        {
            (await c.NewsFeeds.SingleAsync(f => f.NewsFeedId == feedId)).Kind = NewsFeedKind.Podcast;
            await c.SaveChangesAsync();
        });
        await news.Job.RunOnceAsync(CancellationToken.None);
        var now = news.Clock.GetUtcNow().UtcDateTime;
        await news.WithContextAsync(async c =>
        {
            var requests = new PodcastAudioRequests();
            foreach (var id in await c.PodcastEpisodeAudio.Select(a => a.NewsArticleId).ToListAsync())
            {
                await requests.RequestAsync(c, id, now, CancellationToken.None);
                now = now.AddSeconds(1);
            }
        });
        news.Advance(TimeSpan.FromSeconds(audio.Length));
        return feedId;
    }

    private Task<List<PodcastEpisodeAudio>> AudioAsync() =>
        news.WithContextAsync(c => c.PodcastEpisodeAudio.AsNoTracking().Join(
                c.PodcastEpisodes, a => a.NewsArticleId, e => e.NewsArticleId, (a, e) => new { a, e.EnclosureUrl })
            .OrderBy(x => x.EnclosureUrl).Select(x => x.a).ToListAsync());

    private Task<NewsStorageTotal> TotalsAsync() => news.WithContextAsync(c => c.NewsStorageTotals.AsNoTracking().SingleAsync());

    private void Serve(string url, byte[] body, Action<HttpResponseMessage>? shape = null) =>
        news.Server.Map(url, _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
            response.Content.Headers.TryAddWithoutValidation("Content-Type", "application/octet-stream");
            shape?.Invoke(response);
            return response;
        });

    [Fact]
    public async Task AC11_AC29_A_queued_episode_is_stored_with_its_file_chapters_and_its_bytes_counted()
    {
        var file = Mp3([new(0, null, "Intro"), new(30_000, null, "Main")]);
        Serve(Audio1, file);
        await QueuedAsync(Audio1);

        await Job.RunOnceAsync(CancellationToken.None);

        var audio = Assert.Single(await AudioAsync());
        Assert.Equal((PodcastAudioState.Stored, "audio/mpeg", (long?)file.Length, 0L), (audio.State, audio.ContentType, audio.SizeBytes, audio.ReservedBytes));
        Assert.NotNull(audio.StoredObjectId);
        Assert.Null(audio.LeaseOwner);
        var totals = await TotalsAsync();
        Assert.Equal((file.LongLength, 0L), (totals.AudioBytes, totals.AudioReservedBytes));
        var chapters = await news.WithContextAsync(c => c.PodcastChapters.AsNoTracking()
            .Where(ch => ch.Source == PodcastChapterSource.AudioFile).OrderBy(ch => ch.Position).Select(ch => ch.Title).ToListAsync());
        Assert.Equal(["Intro", "Main"], chapters);
        var request = news.Server.Requests.Last(r => r.RequestUri!.AbsoluteUri == Audio1);
        Assert.Equal(["identity"], request.Headers.AcceptEncoding.Select(e => e.Value));
        Assert.False(request.Headers.Contains("Referer"));
        Assert.False(request.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task A_re_download_replaces_the_file_chapters_in_place()
    {
        Serve(Audio1, Mp3([new(0, null, "Old one"), new(1_000, null, "Old two"), new(2_000, null, "Old three")]));
        await QueuedAsync(Audio1);
        await Job.RunOnceAsync(CancellationToken.None);
        await news.WithContextAsync(async c =>
        {
            var row = await c.PodcastEpisodeAudio.SingleAsync();
            row.State = PodcastAudioState.Queued;
            row.NextAttemptAtUtc = null;
            await c.SaveChangesAsync();
        });
        Serve(Audio1, Mp3([new(0, null, "New")]));

        await Job.RunOnceAsync(CancellationToken.None);

        var titles = await news.WithContextAsync(c => c.PodcastChapters.AsNoTracking()
            .Where(ch => ch.Source == PodcastChapterSource.AudioFile).Select(ch => ch.Title).ToListAsync());
        Assert.Equal(["New"], titles);
    }

    public static TheoryData<string, byte[]> NotAudio => new()
    {
        { "html", "<!doctype html><html>a landing page</html>"u8.ToArray() },
        { "png", [0x89, .. "PNG"u8, 0x0D, 0x0A, 0x1A, 0x0A, .. new byte[256]] },
        { "zip", [.. "PK"u8, 3, 4, .. new byte[256]] },
    };

    [Theory]
    [MemberData(nameof(NotAudio))]
    public async Task AC13_A_body_that_is_not_audio_fails_at_once_with_no_committed_object(string name, byte[] body)
    {
        Serve(Audio1, body, r => r.Content.Headers.ContentType = new("audio/mpeg"));
        await QueuedAsync(Audio1);

        await Job.RunOnceAsync(CancellationToken.None);

        var audio = Assert.Single(await AudioAsync());
        Assert.True((PodcastAudioState.Failed, PodcastAudioFailureKind.NotAudio) == (audio.State, audio.FailureKind), name);
        Assert.Equal((0L, 0L), ((await TotalsAsync()).AudioReservedBytes, (await TotalsAsync()).AudioBytes));
        await AssertNothingStoredAsync();
    }

    private async Task AssertNothingStoredAsync()
    {
        Assert.False(await news.WithContextAsync(c => c.StoredObjects.AnyAsync(o => o.FeatureKey == "PodcastAudio" && o.State == StoredObjectState.Committed)));
        var folder = $"{Path.DirectorySeparatorChar}podcast-audio{Path.DirectorySeparatorChar}";
        Assert.DoesNotContain(volume.EnumerateFiles("*", SearchOption.AllDirectories), f => f.FullName.Contains(folder, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AC14_A_declared_length_above_the_episode_cap_is_refused_before_the_body()
    {
        Serve(Audio1, Mp3(), r => r.Content.Headers.ContentLength = 10L * 1024 * 1024 * 1024);
        await QueuedAsync(Audio1);

        await Job.RunOnceAsync(CancellationToken.None);

        var audio = Assert.Single(await AudioAsync());
        Assert.Equal((PodcastAudioState.Failed, PodcastAudioFailureKind.TooLarge), (audio.State, audio.FailureKind));
    }

    [Fact]
    public async Task AC14_A_chunked_body_past_the_episode_cap_fails_too_large_with_its_reservation_returned()
    {
        // Its own host with a 1 MiB transport ceiling, so the effective episode cap is 1 MiB.
        var root = Directory.CreateTempSubdirectory("odyssey-podcast-cap-");
        var database = $"podcast-cap-{Guid.NewGuid():N}";
        await using var small = await NewsJobHarness.CreateAsync(
            o => o.UseInMemoryDatabase(database, DatabaseRoot).ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)),
            root.FullName, configureServices: services => services.AddSingleton<PodcastAudioDownloadJob>(), maxFileSizeBytes: 1024 * 1024);
        try
        {
            small.Server.Feed(FeedUrl, Show(Audio1));
            small.Server.Map(Audio1, _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ScriptedStream(small.Clock, [Mp3Frames(2 * 1024 * 1024 / Mp3FrameLength)])),
            });
            var feedId = await small.AddFeedAsync(FeedUrl);
            await small.WithContextAsync(async c =>
            {
                (await c.NewsFeeds.SingleAsync(f => f.NewsFeedId == feedId)).Kind = NewsFeedKind.Podcast;
                await c.SaveChangesAsync();
            });
            await small.Job.RunOnceAsync(CancellationToken.None);
            await small.WithContextAsync(async c =>
                await new PodcastAudioRequests().RequestAsync(c, (await c.PodcastEpisodeAudio.SingleAsync()).NewsArticleId, small.Clock.GetUtcNow().UtcDateTime, CancellationToken.None));

            await small.Host.Services.GetRequiredService<PodcastAudioDownloadJob>().RunOnceAsync(CancellationToken.None);

            await small.WithContextAsync(async c =>
            {
                var audio = await c.PodcastEpisodeAudio.AsNoTracking().SingleAsync();
                Assert.Equal((PodcastAudioState.Failed, PodcastAudioFailureKind.TooLarge, 0L), (audio.State, audio.FailureKind, audio.ReservedBytes));
                Assert.Equal(0L, (await c.NewsStorageTotals.AsNoTracking().SingleAsync()).AudioReservedBytes);
                Assert.False(await c.StoredObjects.AnyAsync(o => o.FeatureKey == "PodcastAudio" && o.State == StoredObjectState.Committed));
            });
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AC16_A_compressed_response_is_refused_and_nothing_is_stored()
    {
        Serve(Audio1, Mp3(), r => r.Content.Headers.ContentEncoding.Add("gzip"));
        await QueuedAsync(Audio1);

        await Job.RunOnceAsync(CancellationToken.None);

        var audio = Assert.Single(await AudioAsync());
        Assert.Equal((PodcastAudioState.Failed, PodcastAudioFailureKind.Refused), (audio.State, audio.FailureKind));
        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task AC18_A_full_quota_defers_without_spending_an_attempt()
    {
        Serve(Audio1, Mp3());
        await QueuedAsync(Audio1);
        await news.WithContextAsync(async c =>
        {
            (await c.NewsStorageTotals.SingleAsync()).AudioPendingReleaseBytes = 50L * 1024 * 1024 * 1024;
            await c.SaveChangesAsync();
        });

        await Job.RunOnceAsync(CancellationToken.None);

        var audio = Assert.Single(await AudioAsync());
        Assert.Equal((PodcastAudioState.Deferred, PodcastAudioDeferredReason.AudioQuota, (byte)0, 0L),
            (audio.State, audio.DeferredReason, audio.Attempts, audio.ReservedBytes));
        Assert.Equal(news.Clock.GetUtcNow().UtcDateTime + PodcastTiming.DeferredRetry, audio.NextAttemptAtUtc);
        Assert.Equal(0L, (await TotalsAsync()).AudioReservedBytes);
    }

    [Fact]
    public async Task Low_disk_headroom_defers_the_download()
    {
        Serve(Audio1, Mp3());
        await QueuedAsync(Audio1);
        news.Host.Space.Override = (2L * 1024 * 1024 * 1024, 4L * 1024 * 1024 * 1024);

        await Job.RunOnceAsync(CancellationToken.None);

        var audio = Assert.Single(await AudioAsync());
        Assert.Equal((PodcastAudioState.Deferred, PodcastAudioDeferredReason.DiskHeadroom), (audio.State, audio.DeferredReason));
    }

    [Fact]
    public async Task AC17_A_transient_failure_backs_off_and_the_fourth_attempt_fails_for_good()
    {
        news.Server.Status(Audio1, HttpStatusCode.ServiceUnavailable);
        await QueuedAsync(Audio1);

        foreach (var wait in PodcastTiming.Backoff)
        {
            await Job.RunOnceAsync(CancellationToken.None);
            var queued = Assert.Single(await AudioAsync());
            Assert.Equal((PodcastAudioState.Queued, PodcastAudioFailureKind.HttpError), (queued.State, queued.FailureKind));
            Assert.Equal(news.Clock.GetUtcNow().UtcDateTime + wait, queued.NextAttemptAtUtc);
            news.Advance(wait);
        }

        await Job.RunOnceAsync(CancellationToken.None);
        Assert.Equal(PodcastAudioState.Failed, Assert.Single(await AudioAsync()).State);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task A_missing_file_fails_at_once(HttpStatusCode status)
    {
        news.Server.Status(Audio1, status);
        await QueuedAsync(Audio1);

        await Job.RunOnceAsync(CancellationToken.None);

        var audio = Assert.Single(await AudioAsync());
        Assert.Equal((PodcastAudioState.Failed, PodcastAudioFailureKind.Gone), (audio.State, audio.FailureKind));
    }

    [Fact]
    public async Task AC17a_An_expired_lease_returns_to_the_queue_with_its_reservation_and_the_feed_is_claimable_again()
    {
        Serve(Audio1, Mp3());
        Serve(Audio2, Mp3());
        await QueuedAsync(Audio1, Audio2);
        var now = news.Clock.GetUtcNow().UtcDateTime;
        Guid dead = Guid.Empty;
        await news.WithContextAsync(async c =>
        {
            var row = await c.PodcastEpisodeAudio.OrderBy(a => a.RequestedAtUtc).FirstAsync();
            dead = row.NewsArticleId;
            row.State = PodcastAudioState.Downloading;
            row.LeaseOwner = Guid.NewGuid();
            row.LeaseExpiresAtUtc = now.AddSeconds(-1);
            row.ReservedBytes = 1000;
            (await c.NewsStorageTotals.SingleAsync()).AudioReservedBytes = 1000;
            await c.SaveChangesAsync();
        });

        Assert.Equal(1, await Job.RecoverExpiredAsync(CancellationToken.None));

        var recovered = (await AudioAsync()).Single(a => a.NewsArticleId == dead);
        Assert.Equal((PodcastAudioState.Queued, (byte)0, 0L, (Guid?)null), (recovered.State, recovered.Attempts, recovered.ReservedBytes, recovered.LeaseOwner));
        Assert.Equal(0L, (await TotalsAsync()).AudioReservedBytes);

        // The dead download no longer blocks its feed or host: the next run claims an episode of the same feed.
        await Job.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, (await AudioAsync()).Count(a => a.State == PodcastAudioState.Stored));
    }

    [Fact]
    public async Task AC17_One_feed_downloads_one_episode_at_a_time()
    {
        Serve(Audio1, Mp3());
        Serve(Audio2, Mp3());
        await QueuedAsync(Audio1, Audio2);

        await Job.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, (await AudioAsync()).Count(a => a.State == PodcastAudioState.Stored));

        await Job.RunOnceAsync(CancellationToken.None);
        Assert.All(await AudioAsync(), a => Assert.Equal(PodcastAudioState.Stored, a.State));
    }

    [Fact]
    public async Task AC37_A_download_on_an_archived_feed_waits()
    {
        Serve(Audio1, Mp3());
        var feedId = await QueuedAsync(Audio1);
        await news.WithContextAsync(async c =>
        {
            (await c.NewsFeeds.SingleAsync(f => f.NewsFeedId == feedId)).ArchivedAtUtc = news.Clock.GetUtcNow().UtcDateTime;
            await c.SaveChangesAsync();
        });

        await Job.RunOnceAsync(CancellationToken.None);

        Assert.Equal(PodcastAudioState.Queued, Assert.Single(await AudioAsync()).State);
        Assert.False(await Job.HasWorkAsync(CancellationToken.None));
    }

    private async Task RequestOneAsync(string enclosure)
    {
        news.Server.Feed(FeedUrl, Show(enclosure));
        var feedId = await news.AddFeedAsync(FeedUrl);
        await news.WithContextAsync(async c =>
        {
            (await c.NewsFeeds.SingleAsync(f => f.NewsFeedId == feedId)).Kind = NewsFeedKind.Podcast;
            await c.SaveChangesAsync();
        });
        await news.Job.RunOnceAsync(CancellationToken.None);
        await news.WithContextAsync(async c =>
            await new PodcastAudioRequests().RequestAsync(c, (await c.PodcastEpisodeAudio.SingleAsync()).NewsArticleId, news.Clock.GetUtcNow().UtcDateTime, CancellationToken.None));
    }

    private void Chain(int hops, string target)
    {
        for (var hop = 0; hop < hops; hop++)
        {
            news.Server.Redirect($"https://track{hop}.example.net/x", HttpStatusCode.Found, hop == hops - 1 ? target : $"https://track{hop + 1}.example.net/x");
        }
    }

    [Theory]
    [InlineData(10, PodcastAudioState.Stored)]
    [InlineData(11, PodcastAudioState.Failed)]
    public async Task AC15_AC16_Ten_audio_hops_are_followed_with_no_credential_on_any_hop_and_an_eleventh_fails(int hops, PodcastAudioState expected)
    {
        Chain(hops, Audio1);
        Serve(Audio1, Mp3());
        await RequestOneAsync("https://track0.example.net/x");

        await Job.RunOnceAsync(CancellationToken.None);

        Assert.Equal(expected, Assert.Single(await AudioAsync()).State);
        var audioHops = news.Server.Requests.Where(r => r.RequestUri!.Host.StartsWith("track", StringComparison.Ordinal) || r.RequestUri.AbsoluteUri == Audio1).ToList();
        Assert.Equal(Math.Min(hops, PodcastLimits.AudioMaxRedirects + 1) + (expected == PodcastAudioState.Stored ? 1 : 0), audioHops.Count);
        Assert.All(audioHops, r =>
        {
            Assert.Equal(["identity"], r.Headers.AcceptEncoding.Select(e => e.Value));
            Assert.False(r.Headers.Contains("Referer"));
            Assert.False(r.Headers.Contains("Authorization"));
            Assert.False(r.Headers.Contains("Cookie"));
        });
    }

    [Theory]
    [InlineData("https://127.0.0.1/a.mp3")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://10.0.0.5/a.mp3")]
    public async Task AC15_A_redirect_to_a_private_or_metadata_address_ends_refused(string target)
    {
        Chain(2, target);
        await RequestOneAsync("https://track0.example.net/x");

        await Job.RunOnceAsync(CancellationToken.None);

        var audio = Assert.Single(await AudioAsync());
        Assert.Equal((PodcastAudioState.Failed, PodcastAudioFailureKind.Refused), (audio.State, audio.FailureKind));
        Assert.DoesNotContain(news.Server.Requests, r => r.RequestUri!.AbsoluteUri == target);
    }

    [Fact]
    public async Task AC32_No_log_line_names_the_enclosure_url_or_a_file_path()
    {
        const string Private = "https://cdn.example.org/1.mp3?token=s3cret";
        Serve(Private, Mp3([new(0, null, "Intro")]));
        await RequestOneAsync(Private);

        await Job.RunOnceAsync(CancellationToken.None);

        Assert.Equal(PodcastAudioState.Stored, Assert.Single(await AudioAsync()).State);
        var path = await news.WithContextAsync(c => c.StoredObjects.Where(o => o.FeatureKey == "PodcastAudio").Select(o => o.RelativePath).SingleAsync());
        Assert.StartsWith("podcast-audio/", path, StringComparison.Ordinal);
        Assert.DoesNotContain(news.Host.Logs.Entries, e => e.Message.Contains("s3cret", StringComparison.Ordinal)
            || e.Message.Contains("/1.mp3", StringComparison.Ordinal) || e.Message.Contains(path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AC17_A_download_that_stalls_is_cut_off_and_requeued_with_back_off()
    {
        var body = new ScriptedStream(news.Clock, [Mp3Frames(200)], stallAfter: true);
        ServeStream(Audio1, body);
        await QueuedAsync(Audio1);

        var run = Job.RunOnceAsync(CancellationToken.None);
        Assert.True(await body.Stalled.WaitAsync(TimeSpan.FromSeconds(30)));
        news.Advance(NewsStreamFetch.StallTimeout + TimeSpan.FromSeconds(1));
        await run;

        var audio = Assert.Single(await AudioAsync());
        Assert.Equal((PodcastAudioState.Queued, PodcastAudioFailureKind.Timeout, (byte)1, 0L), (audio.State, audio.FailureKind, audio.Attempts, audio.ReservedBytes));
        Assert.Equal(0L, (await TotalsAsync()).AudioReservedBytes);
        await AssertNothingStoredAsync();
        // A publisher's stall is not the volume's fault: no hold, no error about the volume.
        Assert.DoesNotContain(news.Host.Logs.Entries, e => e.Message.Contains("held in this worker", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_download_that_lost_its_row_returns_no_reservation_a_second_time()
    {
        Guid id = Guid.Empty;
        // The recovery a peer ran while this download was in flight: the row is requeued with a new fence and its bytes
        // returned; the 500 left are another download's.
        var body = new ScriptedStream(news.Clock, [Mp3Frames(200), Mp3Frames(10)], beforeChunk: async index =>
        {
            if (index != 1)
            {
                return;
            }

            news.Advance(PodcastTiming.AudioLeaseRenewal + TimeSpan.FromSeconds(1));
            await news.WithContextAsync(async c =>
            {
                var row = await c.PodcastEpisodeAudio.SingleAsync(a => a.NewsArticleId == id);
                row.State = PodcastAudioState.Queued;
                row.LeaseOwner = null;
                row.LeaseExpiresAtUtc = null;
                row.ReservedBytes = 0;
                row.AudioFence = Guid.NewGuid();
                (await c.NewsStorageTotals.SingleAsync()).AudioReservedBytes = 500;
                await c.SaveChangesAsync();
            });
        });
        ServeStream(Audio1, body);
        await QueuedAsync(Audio1);
        id = (await AudioAsync()).Single().NewsArticleId;

        await Job.RunOnceAsync(CancellationToken.None);

        var audio = Assert.Single(await AudioAsync());
        Assert.Equal((PodcastAudioState.Queued, (byte)0), (audio.State, audio.Attempts));
        Assert.Equal(500L, (await TotalsAsync()).AudioReservedBytes);
        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task AC36_A_long_download_renews_its_lease_and_heartbeats_as_it_goes()
    {
        var started = news.Clock.GetUtcNow().UtcDateTime;
        var chunk = Mp3Frames(3 * 1024 * 1024 / Mp3FrameLength);
        var body = new ScriptedStream(news.Clock, [chunk, chunk, chunk, chunk], beforeChunk: index =>
        {
            if (index > 0)
            {
                news.Advance(PodcastTiming.AudioLeaseRenewal + TimeSpan.FromSeconds(1));
            }

            return Task.CompletedTask;
        });
        ServeStream(Audio1, body);
        await QueuedAsync(Audio1);

        await Job.RunOnceAsync(CancellationToken.None);

        Assert.Equal(PodcastAudioState.Stored, Assert.Single(await AudioAsync()).State);
        var beat = await news.WithContextAsync(c => c.JobHeartbeats.AsNoTracking()
            .Where(h => h.JobType == Dtos.Application.JobType.PodcastAudioDownload).MaxAsync(h => h.LastTickAtUtc));
        Assert.True(beat >= started + TimeSpan.FromMinutes(2), $"Last heartbeat {beat:O}, started {started:O}.");
    }

    private void ServeStream(string url, Stream body) =>
        news.Server.Map(url, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });

    [Fact]
    public async Task AC17_The_stream_stops_after_a_minute_without_a_byte()
    {
        var blocking = new BlockingStream();
        var paced = new NewsStreamFetch.PacedStream(blocking, news.Clock, CancellationToken.None, CancellationToken.None);
        var read = paced.ReadAsync(new byte[16]).AsTask();
        Assert.True(await blocking.Entered.WaitAsync(TimeSpan.FromSeconds(30)));
        news.Advance(NewsStreamFetch.StallTimeout + TimeSpan.FromSeconds(1));

        var stopped = await Assert.ThrowsAsync<NewsStreamAbortedException>(() => read);
        Assert.Equal(NewsStreamAbort.Stall, stopped.Reason);
    }

    [Fact]
    public async Task AC17_The_stream_stops_below_the_throughput_floor_after_the_first_minute()
    {
        var trickle = new TrickleStream(news.Clock, bytesPerRead: 1024, every: TimeSpan.FromSeconds(1));
        var paced = new NewsStreamFetch.PacedStream(trickle, news.Clock, CancellationToken.None, CancellationToken.None);
        var buffer = new byte[4096];

        var stopped = await Assert.ThrowsAsync<NewsStreamAbortedException>(async () =>
        {
            while (true)
            {
                _ = await paced.ReadAsync(buffer);
            }
        });

        Assert.Equal(NewsStreamAbort.TooSlow, stopped.Reason);
        Assert.InRange(trickle.Elapsed, NewsStreamFetch.FloorGrace + NewsStreamFetch.FloorWindow, NewsStreamFetch.FloorGrace + NewsStreamFetch.FloorWindow + TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(0L, 120)]
    [InlineData(32L * 1024 * 60, 180)]
    [InlineData(10L * 1024 * 1024 * 1024, 3600)]
    public void The_total_deadline_follows_the_expected_size(long bytes, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), NewsStreamFetch.TotalDeadline(bytes));

    private sealed class BlockingStream : Stream
    {
        public SemaphoreSlim Entered { get; } = new(0);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.Release();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Hands out <c>bytesPerRead</c> bytes and moves the fake clock on by <c>every</c> per read: 1 KiB/s.</summary>
    private sealed class TrickleStream(Odyssey.Testing.FileStorage.TestClock clock, int bytesPerRead, TimeSpan every) : Stream
    {
        public TimeSpan Elapsed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            clock.Advance(every);
            Elapsed += every;
            return ValueTask.FromResult(Math.Min(bytesPerRead, buffer.Length));
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A non-seekable body with no length (so the response is chunked): the chunks in order, a hook before each, and —
    /// with <c>stallAfter</c> — a read after the last that blocks until cancelled, signalling <see cref="Stalled"/>.
    /// </summary>
    private sealed class ScriptedStream(
        Odyssey.Testing.FileStorage.TestClock clock, IReadOnlyList<byte[]> chunks, bool stallAfter = false, Func<int, Task>? beforeChunk = null) : Stream
    {
        private int chunk;
        private int offset;
        private bool entered;

        public SemaphoreSlim Stalled { get; } = new(0);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _ = clock;
            if (chunk >= chunks.Count)
            {
                if (!stallAfter)
                {
                    return 0;
                }

                Stalled.Release();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }

            if (!entered)
            {
                entered = true;
                if (beforeChunk is not null)
                {
                    await beforeChunk(chunk);
                }
            }

            var current = chunks[chunk];
            var count = Math.Min(buffer.Length, current.Length - offset);
            current.AsMemory(offset, count).CopyTo(buffer);
            offset += count;
            if (offset == current.Length)
            {
                chunk++;
                offset = 0;
                entered = false;
            }

            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
