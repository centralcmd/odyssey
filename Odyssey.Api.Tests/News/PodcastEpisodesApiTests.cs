using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Internet.News.Podcasts;
using Odyssey.Core.Internet.News.Podcasts.Files;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Internet;
using Xunit;
using static Odyssey.Api.Tests.News.NewsApiTestSupport;

namespace Odyssey.Api.Tests.News;

/// <summary>The episode, audio and artwork endpoints (issue #556 §5.2–§5.4). Names carry the AC they pin.</summary>
public class PodcastEpisodesApiTests(PodcastEpisodesApiTests.Host host) : SharedApiHostTests<PodcastEpisodesApiTests.Host>(host)
{
    private const string Episodes = "/api/podcast-episodes";

    private static readonly string[] Listener = [.. AllNewsClaims, PermissionClaims.PodcastAudioCreate, PermissionClaims.PodcastAudioDelete];

    private static readonly byte[] AudioBytes = [.. "ID3"u8, 4, 0, 0, 0, 0, 0, 0, .. Enumerable.Range(0, 2048).Select(i => (byte)i)];

    private async Task<Guid> PodcastFeedAsync(string url = "https://show.example.org/podcast.xml", bool archived = false)
    {
        var id = await Factory.SeedFeedAsync(url, title: "The \"Show\"\r\nX", archived: archived);
        await Factory.WithContextAsync(async c =>
        {
            (await c.NewsFeeds.SingleAsync(f => f.NewsFeedId == id)).Kind = NewsFeedKind.Podcast;
            await c.SaveChangesAsync();
        });
        return id;
    }

    private async Task<Guid> EpisodeAsync(Guid feedId, PodcastAudioState state = PodcastAudioState.None, DateTime? sortAt = null, string title = "Episode")
    {
        var id = await PodcastFeedsApiTests.SeedEpisodeAsync(Factory, feedId, state);
        await Factory.WithContextAsync(async c =>
        {
            var article = await c.NewsArticles.SingleAsync(a => a.NewsArticleId == id);
            article.Title = title;
            article.SortAtUtc = sortAt ?? Base;
            (await c.PodcastEpisodes.SingleAsync(e => e.NewsArticleId == id)).SortAtUtc = sortAt ?? Base;
            await c.SaveChangesAsync();
        });
        return id;
    }

    /// <summary>Writes real audio bytes to the store and marks the episode's audio stored.</summary>
    private Task StoreAudioAsync(Guid feedId, Guid episodeId) => StoreAudioAsync(feedId, episodeId, AudioBytes);

    private Task StoreAudioAsync(Guid feedId, Guid episodeId, byte[] bytes) =>
        Task.Run(async () =>
        {
            using var scope = Factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();
            var stored = await store.PutAsync(
                PodcastAudioStorage.FeatureKey, new PodcastAudioLayout(feedId, episodeId), null, null, "audio/mpeg",
                new MemoryStream(bytes), bytes.Length + 1, CancellationToken.None);
            var audio = await context.PodcastEpisodeAudio.SingleAsync(a => a.NewsArticleId == episodeId);
            audio.State = PodcastAudioState.Stored;
            audio.StoredObjectId = stored.Id;
            audio.ContentType = "audio/mpeg";
            audio.SizeBytes = bytes.Length;
            audio.Sha256 = System.Security.Cryptography.SHA256.HashData(bytes);
            audio.StoredAtUtc = Base;
            var totals = await context.NewsStorageTotals.FirstOrDefaultAsync();
            if (totals is null)
            {
                context.NewsStorageTotals.Add(new NewsStorageTotal { Id = NewsStorageTotal.SingletonId, AudioBytes = bytes.Length });
            }
            else
            {
                totals.AudioBytes += bytes.Length;
            }

            await context.SaveChangesAsync();
        });

    private Task<PodcastEpisodeAudio> AudioRowAsync(Guid id) =>
        Factory.WithContextAsync(c => c.PodcastEpisodeAudio.AsNoTracking().SingleAsync(a => a.NewsArticleId == id));

    [Fact]
    public async Task The_list_holds_only_podcast_episodes_newest_first_and_pages_by_cursor()
    {
        var feed = await PodcastFeedAsync();
        var article = await Factory.SeedFeedAsync("https://plain.example.org/rss");
        await Factory.SeedArticlesAsync(article, 2);
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add(await EpisodeAsync(feed, sortAt: Base.AddHours(i), title: $"Episode {i}"));
        }

        using var client = Factory.ForCaller(ReadOnly).CreateClient();
        var first = await client.GetFromJsonAsync<PodcastEpisodePage>($"{Episodes}?limit=2");
        Assert.Equal((3, false), (first!.TotalCount, first.TotalCountCapped));
        Assert.Equal([ids[2], ids[1]], first.Items.Select(i => i.Id));
        Assert.NotNull(first.NextCursor);
        var second = await client.GetFromJsonAsync<PodcastEpisodePage>($"{Episodes}?limit=2&cursor={Uri.EscapeDataString(first.NextCursor!)}");
        Assert.Equal([ids[0]], second!.Items.Select(i => i.Id));
        Assert.Null(second.NextCursor);

        var oldest = await client.GetFromJsonAsync<PodcastEpisodePage>($"{Episodes}?sortBy=Oldest");
        Assert.Equal(ids, oldest!.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task A_cursor_sent_with_other_filters_is_400_and_an_article_feed_id_is_422()
    {
        var feed = await PodcastFeedAsync();
        var article = await Factory.SeedFeedAsync("https://plain.example.org/rss");
        await EpisodeAsync(feed);
        await EpisodeAsync(feed, sortAt: Base.AddHours(1));
        using var client = Factory.ForCaller(ReadOnly).CreateClient();
        var first = await client.GetFromJsonAsync<PodcastEpisodePage>($"{Episodes}?limit=1");

        var foreign = await client.GetAsync($"{Episodes}?limit=1&sortBy=Oldest&cursor={Uri.EscapeDataString(first!.NextCursor!)}");
        var notPodcast = await client.GetAsync($"{Episodes}?feedIds={article}");

        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notPodcast.StatusCode);
        Assert.True((await notPodcast.ProblemAsync()).HasError("feedIds"));
    }

    [Fact]
    public async Task The_audio_state_filter_and_the_audio_block_carry_no_url_or_object_id()
    {
        var feed = await PodcastFeedAsync();
        var stored = await EpisodeAsync(feed, PodcastAudioState.Queued);
        await StoreAudioAsync(feed, stored);
        await EpisodeAsync(feed, PodcastAudioState.None, Base.AddHours(1));
        using var client = Factory.ForCaller(ReadOnly).CreateClient();

        var response = await client.GetAsync($"{Episodes}?audioState=Stored");
        var body = await response.Content.ReadAsStringAsync();
        var page = await response.ReadAsync<PodcastEpisodePage>();

        var item = Assert.Single(page.Items);
        Assert.Equal((stored, PodcastAudioState.Stored, (long?)AudioBytes.Length, "audio/mpeg"),
            (item.Id, item.Audio.State, item.Audio.SizeBytes, item.Audio.ContentType));
        Assert.DoesNotContain("cdn.example.org", body, StringComparison.Ordinal);
        Assert.DoesNotContain("storedObjectId", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_saved_to_device_filter_ands_with_audio_state_binds_the_cursor_and_feeds_the_podcast_count()
    {
        var feed = await PodcastFeedAsync();
        var savedRemoved = await EpisodeAsync(feed, PodcastAudioState.None, Base);
        var savedQueued = await EpisodeAsync(feed, PodcastAudioState.Queued, Base.AddHours(1));
        var neverSaved = await EpisodeAsync(feed, PodcastAudioState.None, Base.AddHours(2));
        await Factory.WithContextAsync(async c =>
        {
            foreach (var episode in await c.PodcastEpisodes.Where(e => e.NewsArticleId == savedRemoved || e.NewsArticleId == savedQueued).ToListAsync())
            {
                episode.LastPlayedAtUtc = Base.AddDays(1);
            }

            await c.SaveChangesAsync();
        });
        using var client = Factory.ForCaller(ReadOnly).CreateClient();

        var saved = await client.GetFromJsonAsync<PodcastEpisodePage>($"{Episodes}?savedToDevice=true&limit=1");
        var unsaved = await client.GetFromJsonAsync<PodcastEpisodePage>($"{Episodes}?savedToDevice=false");
        var savedAndNone = await client.GetFromJsonAsync<PodcastEpisodePage>($"{Episodes}?savedToDevice=true&audioState=None");
        var foreign = await client.GetAsync($"{Episodes}?savedToDevice=false&limit=1&cursor={Uri.EscapeDataString(saved!.NextCursor!)}");
        var podcast = (await client.GetFromJsonAsync<ExistingNewsFeed>($"/api/news-feeds/{feed}"))!.Podcast!;

        Assert.Equal((2, savedQueued), (saved.TotalCount, Assert.Single(saved.Items).Id));
        Assert.Equal([neverSaved], unsaved!.Items.Select(i => i.Id));
        Assert.Equal([savedRemoved], savedAndNone!.Items.Select(i => i.Id));
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.Equal((3, 2), (podcast.EpisodeCount, podcast.SavedEpisodeCount));
    }

    [Fact]
    public async Task AC25_The_detail_shows_notes_and_the_highest_precedence_chapters_and_an_article_feed_is_404()
    {
        var feed = await PodcastFeedAsync();
        var id = await EpisodeAsync(feed);
        await Factory.SeedTextAsync(feed, id, "The show notes.");
        await Factory.WithContextAsync(async c =>
        {
            c.PodcastChapters.AddRange(
                new PodcastChapter { PodcastChapterId = Guid.NewGuid(), NewsArticleId = id, Source = PodcastChapterSource.AudioFile, Position = 0, StartMs = 0, Title = "From file" },
                new PodcastChapter { PodcastChapterId = Guid.NewGuid(), NewsArticleId = id, Source = PodcastChapterSource.Json, Position = 0, StartMs = 0, Title = "From JSON" },
                new PodcastChapter { PodcastChapterId = Guid.NewGuid(), NewsArticleId = id, Source = PodcastChapterSource.Json, Position = 1, StartMs = 5000, Title = "Second" });
            await c.SaveChangesAsync();
        });
        using var client = Factory.ForCaller(ReadOnly).CreateClient();

        var detail = await client.GetFromJsonAsync<PodcastEpisodeDetail>($"{Episodes}/{id}");

        Assert.Equal("The show notes.", detail!.ShowNotes);
        Assert.Equal(PodcastChapterSource.Json, detail.ChaptersSource);
        Assert.Equal(["From JSON", "Second"], detail.Chapters.Select(c => c.Title));
        Assert.True(detail.HasChapters);

        await Factory.WithContextAsync(async c =>
        {
            (await c.NewsFeeds.SingleAsync(f => f.NewsFeedId == feed)).Kind = NewsFeedKind.Article;
            await c.SaveChangesAsync();
        });
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Episodes}/{id}")).StatusCode);
        using var listener = Factory.ForCaller(Listener).CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await listener.PostAsync($"{Episodes}/{id}/audio", null)).StatusCode);
    }

    [Fact]
    public async Task AC12_A_download_request_queues_and_a_second_inside_the_cooldown_is_429_with_retry_after()
    {
        var feed = await PodcastFeedAsync();
        var id = await EpisodeAsync(feed);
        using var client = Factory.ForCaller(Listener).CreateClient();

        var queued = await client.PostAsync($"{Episodes}/{id}/audio", null);
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        Assert.Equal(PodcastAudioState.Queued, (await queued.ReadAsync<PodcastEpisodeAudioInfo>()).State);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"{Episodes}/{id}/audio", null)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Episodes}/{id}/audio")).StatusCode);
        var again = await client.PostAsync($"{Episodes}/{id}/audio", null);

        Assert.Equal(HttpStatusCode.TooManyRequests, again.StatusCode);
        Assert.Equal(NewsErrorCodes.PodcastAudioCooldown, (await again.ProblemAsync()).Code());
        Assert.NotNull(again.Headers.RetryAfter);
        var listed = await client.GetFromJsonAsync<PodcastEpisodeSummary>($"{Episodes}/{id}");
        Assert.NotNull(listed!.Audio.RequestAvailableAtUtc);
    }

    [Fact]
    public async Task Stored_audio_is_a_200_an_archived_feed_is_422_and_a_full_quota_is_507()
    {
        var feed = await PodcastFeedAsync();
        var stored = await EpisodeAsync(feed, PodcastAudioState.Queued);
        await StoreAudioAsync(feed, stored);
        var waiting = await EpisodeAsync(feed, sortAt: Base.AddHours(1));
        var archivedFeed = await PodcastFeedAsync("https://old.example.org/podcast.xml", archived: true);
        var archived = await EpisodeAsync(archivedFeed);
        using var client = Factory.ForCaller(Listener).CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"{Episodes}/{stored}/audio", null)).StatusCode);
        var inactive = await client.PostAsync($"{Episodes}/{archived}/audio", null);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, NewsErrorCodes.FeedInactive), (inactive.StatusCode, (await inactive.ProblemAsync()).Code()));

        await Factory.WithContextAsync(async c =>
        {
            (await c.NewsStorageTotals.SingleAsync()).AudioPendingReleaseBytes = 50L * 1024 * 1024 * 1024;
            await c.SaveChangesAsync();
        });
        var full = await client.PostAsync($"{Episodes}/{waiting}/audio", null);
        Assert.Equal((HttpStatusCode.InsufficientStorage, NewsErrorCodes.PodcastAudioQuotaReached), (full.StatusCode, (await full.ProblemAsync()).Code()));
    }

    [Fact]
    public async Task AC24_Removing_stored_audio_releases_it_with_the_grace_and_a_download_in_flight_is_409()
    {
        var feed = await PodcastFeedAsync();
        var stored = await EpisodeAsync(feed, PodcastAudioState.Queued);
        await StoreAudioAsync(feed, stored);
        var downloading = await EpisodeAsync(feed, PodcastAudioState.Downloading, Base.AddHours(1));
        var none = await EpisodeAsync(feed, PodcastAudioState.None, Base.AddHours(2));
        using var client = Factory.ForCaller(Listener).CreateClient();

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Episodes}/{stored}/audio")).StatusCode);
        var busy = await client.DeleteAsync($"{Episodes}/{downloading}/audio");
        Assert.Equal((HttpStatusCode.Conflict, NewsErrorCodes.PodcastAudioBusy), (busy.StatusCode, (await busy.ProblemAsync()).Code()));
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Episodes}/{none}/audio")).StatusCode);

        var row = await AudioRowAsync(stored);
        Assert.Equal((PodcastAudioState.None, (Guid?)null), (row.State, row.StoredObjectId));
        Assert.NotNull(row.LastUserRequestAtUtc);
        await Factory.WithContextAsync(async c =>
        {
            var totals = await c.NewsStorageTotals.SingleAsync();
            Assert.Equal((0L, (long)AudioBytes.Length), (totals.AudioBytes, totals.AudioPendingReleaseBytes));
            Assert.Equal(StoredObjectState.PendingDelete, (await c.StoredObjects.SingleAsync(o => o.FeatureKey == PodcastAudioStorage.FeatureKey)).State);
        });
    }

    [Fact]
    public async Task AC20_The_content_is_an_attachment_with_a_safe_file_name_nosniff_corp_and_an_etag()
    {
        var feed = await PodcastFeedAsync();
        var id = await EpisodeAsync(feed, PodcastAudioState.Queued, title: "Part 1; \"quoted\"\r\nInjected: yes");
        await StoreAudioAsync(feed, id);
        using var client = Factory.ForCaller(ReadOnly).CreateClient();

        var response = await client.GetAsync($"{Episodes}/{id}/audio/content");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AudioBytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("audio/mpeg", response.Content.Headers.ContentType!.MediaType);
        var disposition = response.Content.Headers.ContentDisposition!;
        Assert.Equal("attachment", disposition.DispositionType);
        var name = disposition.FileNameStar ?? disposition.FileName!;
        Assert.EndsWith(".mp3", name, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', name);
        Assert.DoesNotContain('\n', name);
        Assert.DoesNotContain('"', name);
        Assert.DoesNotContain(';', name);
        Assert.False(response.Headers.Contains("Injected"));
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("same-site", response.Headers.GetValues("Cross-Origin-Resource-Policy").Single());
        Assert.NotNull(response.Headers.ETag);
        Assert.False(response.Headers.ETag!.IsWeak);

        using var conditional = new HttpRequestMessage(HttpMethod.Get, $"{Episodes}/{id}/audio/content");
        conditional.Headers.IfNoneMatch.Add(response.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(conditional)).StatusCode);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("bytes=0-", true)]
    [InlineData("bytes=0-{last}", true)]
    [InlineData("bytes=-1", false)]
    [InlineData("bytes={last}-", false)]
    [InlineData("bytes=10-", false)]
    [InlineData("bytes=0-99", false)]
    [InlineData("bytes=0-1,5-6", false)]
    public async Task AC21_Only_a_whole_file_delivered_to_a_news_update_holder_stamps_last_played(string? range, bool stamps)
    {
        var feed = await PodcastFeedAsync();
        var id = await EpisodeAsync(feed, PodcastAudioState.Queued);
        await StoreAudioAsync(feed, id);
        using var client = Factory.ForCaller(AllNewsClaims).CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Episodes}/{id}/audio/content");
        if (range is not null)
        {
            request.Headers.TryAddWithoutValidation("Range", range.Replace("{last}", (AudioBytes.Length - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
        }

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsByteArrayAsync();
        if (range is "bytes=0-99")
        {
            // Range processing is honoured.
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal((0L, 99L, (long)AudioBytes.Length), (response.Content.Headers.ContentRange!.From!.Value, response.Content.Headers.ContentRange.To!.Value, response.Content.Headers.ContentRange.Length!.Value));
            Assert.Equal(AudioBytes[..100], body);
        }

        var played = await PlayedAsync(id);
        Assert.Equal(stamps, played is not null);
    }

    [Fact]
    public async Task AC21_A_reader_without_news_update_and_a_head_request_stamp_nothing()
    {
        var feed = await PodcastFeedAsync();
        var id = await EpisodeAsync(feed, PodcastAudioState.Queued);
        await StoreAudioAsync(feed, id);

        using (var guest = Factory.ForCaller(ReadOnly).CreateClient())
        {
            Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"{Episodes}/{id}/audio/content")).StatusCode);
        }

        using (var holder = Factory.ForCaller(AllNewsClaims).CreateClient())
        {
            await holder.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"{Episodes}/{id}/audio/content"));
        }

        Assert.Null(await PlayedAsync(id));
    }

    [Fact]
    public async Task AC21_A_304_and_an_aborted_transfer_stamp_nothing()
    {
        var feed = await PodcastFeedAsync();
        var conditionalId = await EpisodeAsync(feed, PodcastAudioState.Queued);
        await StoreAudioAsync(feed, conditionalId);
        var abortedId = await EpisodeAsync(feed, PodcastAudioState.Queued, Base.AddHours(1));
        var large = new byte[4 * 1024 * 1024];
        AudioBytes.CopyTo(large, 0);
        await StoreAudioAsync(feed, abortedId, large);
        using var client = Factory.ForCaller(AllNewsClaims).CreateClient();

        using var conditional = new HttpRequestMessage(HttpMethod.Get, $"{Episodes}/{conditionalId}/audio/content");
        conditional.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(AudioBytes))}\""));
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(conditional)).StatusCode);

        using (var response = await client.GetAsync($"{Episodes}/{abortedId}/audio/content", HttpCompletionOption.ResponseHeadersRead))
        {
            await using var stream = await response.Content.ReadAsStreamAsync();
            await stream.ReadExactlyAsync(new byte[16]);
        }

        Assert.Null(await PlayedAsync(conditionalId));
        Assert.Null(await PlayedAsync(abortedId));
    }

    [Fact]
    public async Task AC37_An_archived_feeds_stored_audio_is_still_served()
    {
        var feed = await PodcastFeedAsync();
        var id = await EpisodeAsync(feed, PodcastAudioState.Queued);
        await StoreAudioAsync(feed, id);
        await Factory.WithContextAsync(async c =>
        {
            (await c.NewsFeeds.SingleAsync(f => f.NewsFeedId == feed)).ArchivedAtUtc = Base;
            await c.SaveChangesAsync();
        });
        using var client = Factory.ForCaller(ReadOnly).CreateClient();

        var response = await client.GetAsync($"{Episodes}/{id}/audio/content");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AudioBytes, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task AC12_A_request_on_a_download_in_flight_is_202_and_changes_nothing()
    {
        var feed = await PodcastFeedAsync();
        var id = await EpisodeAsync(feed, PodcastAudioState.Downloading);
        var before = await AudioRowAsync(id);
        using var client = Factory.ForCaller(Listener).CreateClient();

        var response = await client.PostAsync($"{Episodes}/{id}/audio", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(PodcastAudioState.Downloading, (await response.ReadAsync<PodcastEpisodeAudioInfo>()).State);
        var after = await AudioRowAsync(id);
        Assert.Equal((before.State, before.AudioFence, before.RequestedAtUtc), (after.State, after.AudioFence, after.RequestedAtUtc));
    }

    [Fact]
    public async Task AC7_A_refused_body_reports_its_omitted_reason_and_no_show_notes()
    {
        var feed = await PodcastFeedAsync();
        var id = await EpisodeAsync(feed);
        await Factory.WithContextAsync(async c =>
        {
            (await c.NewsArticles.SingleAsync(a => a.NewsArticleId == id)).BodyOmittedReason = NewsBodyOmittedReason.TooComplex;
            await c.SaveChangesAsync();
        });
        using var client = Factory.ForCaller(ReadOnly).CreateClient();

        var detail = await client.GetFromJsonAsync<PodcastEpisodeDetail>($"{Episodes}/{id}");

        Assert.Equal((NewsBodyOmittedReason.TooComplex, (string?)null), (detail!.ShowNotesOmittedReason, detail.ShowNotes));
    }

    [Fact]
    public async Task AC31_A_nested_object_in_a_request_body_changes_nothing_beyond_the_scalars()
    {
        var feed = await PodcastFeedAsync();
        var id = await EpisodeAsync(feed);
        using var client = Factory.ForCaller(Listener).CreateClient();
        var injected = JsonBody(new
        {
            audio = new { state = "Stored", storedObjectId = Guid.NewGuid() },
            storedObjectId = Guid.NewGuid(),
            enclosureUrl = "https://evil.example/x.mp3",
            chapters = new[] { new { startMs = 0, title = "x" } },
        });

        var response = await client.PostAsync($"{Episodes}/{id}/audio", injected);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var row = await AudioRowAsync(id);
        Assert.Equal((PodcastAudioState.Queued, (Guid?)null), (row.State, row.StoredObjectId));
        await Factory.WithContextAsync(async c =>
        {
            Assert.Equal("https://cdn.example.org/a.mp3", (await c.PodcastEpisodes.SingleAsync(e => e.NewsArticleId == id)).EnclosureUrl);
            Assert.False(await c.PodcastChapters.AnyAsync(ch => ch.NewsArticleId == id));
        });
    }

    /// <summary>
    /// The stamp is written after the response completes, so a positive case polls until it appears; a negative case
    /// polls the same way for 2 s before it may conclude nothing was written.
    /// </summary>
    private async Task<DateTime?> PlayedAsync(Guid id)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            var played = await Factory.WithContextAsync(c => c.PodcastEpisodes.AsNoTracking().Where(e => e.NewsArticleId == id).Select(e => e.LastPlayedAtUtc).SingleAsync());
            if (played is not null)
            {
                return played;
            }

            await Task.Delay(25);
        }

        return null;
    }

    [Theory]
    [InlineData("POST", "audio", PermissionClaims.PodcastAudioCreate)]
    [InlineData("DELETE", "audio", PermissionClaims.PodcastAudioDelete)]
    public async Task AC30_Each_audio_write_needs_its_own_claim(string method, string path, string claim)
    {
        var feed = await PodcastFeedAsync();
        var id = await EpisodeAsync(feed);
        using (var without = Factory.ForCaller(AllNewsClaims).CreateClient())
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await without.SendAsync(new HttpRequestMessage(new HttpMethod(method), $"{Episodes}/{id}/{path}"))).StatusCode);
        }

        using var with = Factory.ForCaller([.. AllNewsClaims, claim]).CreateClient();
        Assert.NotEqual(HttpStatusCode.Forbidden, (await with.SendAsync(new HttpRequestMessage(new HttpMethod(method), $"{Episodes}/{id}/{path}"))).StatusCode);
    }

    [Fact]
    public async Task AC30_Reads_need_news_read()
    {
        using var client = Factory.ForCaller([PermissionClaims.NewsUpdate]).CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Episodes)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Episodes}/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/podcast-artwork/{Guid.NewGuid()}/content")).StatusCode);
    }

    [Fact]
    public async Task AC26_Artwork_is_served_by_size_and_a_pending_one_is_404()
    {
        var feed = await PodcastFeedAsync();
        var stored = Guid.NewGuid();
        var pending = Guid.NewGuid();
        var small = new byte[] { 1, 2, 3 };
        await Task.Run(async () =>
        {
            using var scope = Factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();
            context.PodcastArtwork.AddRange(
                new PodcastArtwork { PodcastArtworkId = stored, NewsFeedId = feed, SourceUrl = "https://a/1.png", SourceUrlKeyHash = new byte[32], State = PodcastArtworkState.Stored, CreatedAtUtc = Base },
                new PodcastArtwork { PodcastArtworkId = pending, NewsFeedId = feed, SourceUrl = "https://a/2.png", SourceUrlKeyHash = new byte[32], State = PodcastArtworkState.Pending, CreatedAtUtc = Base });
            var file = await store.PutAsync(PodcastArtworkStorage.FeatureKey, new PodcastArtworkLayout(feed), null, "s", "image/webp", new MemoryStream(small), 4, CancellationToken.None);
            context.PodcastArtworkFiles.Add(new PodcastArtworkFile
            {
                PodcastArtworkFileId = Guid.NewGuid(),
                PodcastArtworkId = stored,
                Kind = PodcastArtworkFileKind.Small,
                StoredObjectId = file.Id,
                ContentType = "image/webp",
                SizeBytes = small.Length,
                Sha256 = System.Security.Cryptography.SHA256.HashData(small),
            });
            await context.SaveChangesAsync();
        });
        using var client = Factory.ForCaller(ReadOnly).CreateClient();

        var response = await client.GetAsync($"/api/podcast-artwork/{stored}/content?size=Small");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(small, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/podcast-artwork/{stored}/content?size=Medium")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/podcast-artwork/{pending}/content")).StatusCode);
    }

    [Fact]
    public async Task The_status_carries_the_audio_totals_and_the_top_feeds()
    {
        var feed = await PodcastFeedAsync();
        var id = await EpisodeAsync(feed, PodcastAudioState.Queued);
        await StoreAudioAsync(feed, id);
        using var client = Factory.ForCaller(AllNewsClaims).CreateClient();

        var status = await client.GetFromJsonAsync<NewsStatus>("/api/news-status");

        Assert.Equal((long)AudioBytes.Length, status!.AudioBytes);
        Assert.Equal(SystemSettingsDefaults.PodcastAudioStoreMaxGiB * 1024L * 1024 * 1024, status.AudioQuotaBytes);
        Assert.False(status.AudioQuotaReached);
        Assert.Equal(feed, Assert.Single(status.TopFeedsByAudioBytes).FeedId);
    }

    [Fact]
    public void The_download_name_cannot_carry_a_header_breaking_character()
    {
        var name = PodcastEpisodeService.DownloadName("Show\r\n\"x\";", new string('a', 400) + "\u0007", ".mp3");
        Assert.True(name.Length <= 200);
        Assert.EndsWith(".mp3", name, StringComparison.Ordinal);
        Assert.DoesNotContain(name, c => c is '\r' or '\n' or '"' or ';' or '\u0007');
    }

    // ── In-browser playback (issue #593) ─────────────────────────────────────────────────────────────────

    private Task<PodcastEpisode> EpisodeRowAsync(Guid id) =>
        Factory.WithContextAsync(c => c.PodcastEpisodes.AsNoTracking().SingleAsync(e => e.NewsArticleId == id));

    private static StringContent Playback(object body) => JsonBody(body);

    [Fact]
    public async Task Playback_AC1_The_stream_is_inline_ranged_sandboxed_and_carries_the_content_headers()
    {
        var feed = await PodcastFeedAsync();
        var id = await EpisodeAsync(feed, PodcastAudioState.Queued);
        await StoreAudioAsync(feed, id);
        using var client = Factory.ForCaller(ReadOnly).CreateClient();

        var response = await client.GetAsync($"{Episodes}/{id}/audio/stream");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AudioBytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("audio/mpeg", response.Content.Headers.ContentType!.MediaType);
        var disposition = response.Content.Headers.ContentDisposition!;
        Assert.Equal("inline", disposition.DispositionType);
        Assert.Null(disposition.FileName);
        Assert.Null(disposition.FileNameStar);
        Assert.Equal("bytes", response.Headers.AcceptRanges.Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("same-site", response.Headers.GetValues("Cross-Origin-Resource-Policy").Single());
        Assert.Equal("default-src 'none'; sandbox", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.False(response.Headers.ETag!.IsWeak);
    }

    [Fact]
    public async Task Playback_AC2_AC3_A_range_is_206_an_unsatisfiable_one_416_and_a_matching_etag_304()
    {
        var feed = await PodcastFeedAsync();
        var id = await EpisodeAsync(feed, PodcastAudioState.Queued);
        await StoreAudioAsync(feed, id);
        using var client = Factory.ForCaller(ReadOnly).CreateClient();

        using var ranged = new HttpRequestMessage(HttpMethod.Get, $"{Episodes}/{id}/audio/stream");
        ranged.Headers.Range = new RangeHeaderValue(100, 199);
        var partial = await client.SendAsync(ranged);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(AudioBytes[100..200], await partial.Content.ReadAsByteArrayAsync());
        Assert.Equal((100L, 199L, (long)AudioBytes.Length), (partial.Content.Headers.ContentRange!.From!.Value, partial.Content.Headers.ContentRange.To!.Value, partial.Content.Headers.ContentRange.Length!.Value));

        using var beyond = new HttpRequestMessage(HttpMethod.Get, $"{Episodes}/{id}/audio/stream");
        beyond.Headers.Range = new RangeHeaderValue(AudioBytes.Length + 10, AudioBytes.Length + 20);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, (await client.SendAsync(beyond)).StatusCode);

        using var conditional = new HttpRequestMessage(HttpMethod.Get, $"{Episodes}/{id}/audio/stream");
        conditional.Headers.IfNoneMatch.Add(partial.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(conditional)).StatusCode);
    }

    [Fact]
    public async Task Playback_AC3_The_stream_is_503_while_the_store_is_unavailable()
    {
        var feed = await PodcastFeedAsync();
        var id = await EpisodeAsync(feed, PodcastAudioState.Queued);
        await StoreAudioAsync(feed, id);
        using var client = Factory.ForCaller(ReadOnly).CreateClient();
        var registry = Factory.Services.GetRequiredService<FileStorageVolumeRegistry>();
        registry.MarkDegraded(registry.Volumes.Single(), new IOException("test"));
        try
        {
            var response = await client.GetAsync($"{Episodes}/{id}/audio/stream");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(NewsErrorCodes.StorageUnavailable, (await response.ProblemAsync()).Code());
        }
        finally
        {
            registry.MarkAvailable(registry.Volumes.Single());
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("bytes=0-")]
    public async Task Playback_AC4_Streaming_the_whole_file_stamps_neither_saved_nor_listened(string? range)
    {
        var feed = await PodcastFeedAsync();
        var id = await EpisodeAsync(feed, PodcastAudioState.Queued);
        await StoreAudioAsync(feed, id);
        using var client = Factory.ForCaller(AllNewsClaims).CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Episodes}/{id}/audio/stream");
        if (range is not null)
        {
            request.Headers.TryAddWithoutValidation("Range", range);
        }

        var response = await client.SendAsync(request);
        Assert.Equal(AudioBytes, await response.Content.ReadAsByteArrayAsync());

        Assert.Null(await PlayedAsync(id));
        Assert.Null((await EpisodeRowAsync(id)).LastListenedAtUtc);
    }

    [Theory]
    [InlineData(PodcastAudioState.None)]
    [InlineData(PodcastAudioState.Queued)]
    [InlineData(PodcastAudioState.Downloading)]
    [InlineData(PodcastAudioState.Failed)]
    [InlineData(PodcastAudioState.Deferred)]
    public async Task Playback_AC6_The_stream_is_404_unless_the_audio_is_stored(PodcastAudioState state)
    {
        var feed = await PodcastFeedAsync();
        var id = await EpisodeAsync(feed, state);
        using var client = Factory.ForCaller(ReadOnly).CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Episodes}/{id}/audio/stream")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Episodes}/{Guid.NewGuid()}/audio/stream")).StatusCode);
    }

    [Fact]
    public async Task Playback_AC7_The_stream_needs_news_read_and_the_save_needs_news_update()
    {
        var feed = await PodcastFeedAsync();
        var id = await EpisodeAsync(feed, PodcastAudioState.Queued);
        await StoreAudioAsync(feed, id);

        using (var none = Factory.ForCaller([PermissionClaims.NewsUpdate]).CreateClient())
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await none.GetAsync($"{Episodes}/{id}/audio/stream")).StatusCode);
        }

        using var guest = Factory.ForCaller(ReadOnly).CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"{Episodes}/{id}/audio/stream")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.PutAsync($"{Episodes}/{id}/playback", Playback(new { positionSeconds = 10 }))).StatusCode);
        Assert.Null((await EpisodeRowAsync(id)).PlaybackPositionSeconds);
    }

    [Fact]
    public async Task Playback_AC8_A_position_save_is_204_shows_on_the_next_read_and_repeats_idempotently()
    {
        var feed = await PodcastFeedAsync();
        var id = await EpisodeAsync(feed, PodcastAudioState.Queued);
        await StoreAudioAsync(feed, id);
        using var client = Factory.ForCaller(AllNewsClaims).CreateClient();

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync($"{Episodes}/{id}/playback", Playback(new { positionSeconds = 1234, completed = false }))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync($"{Episodes}/{id}/playback", Playback(new { positionSeconds = 1234, completed = false }))).StatusCode);

        var detail = await client.GetFromJsonAsync<PodcastEpisodeDetail>($"{Episodes}/{id}");
        Assert.Equal(1234, detail!.PlaybackPositionSeconds);
        Assert.Null(detail.LastListenedAtUtc);
    }

    [Fact]
    public async Task Playback_AC9_A_finish_stamps_listened_clears_the_position_and_leaves_saved_alone()
    {
        var feed = await PodcastFeedAsync();
        var id = await EpisodeAsync(feed, PodcastAudioState.Queued);
        await StoreAudioAsync(feed, id);
        using var client = Factory.ForCaller(AllNewsClaims).CreateClient();
        await client.PutAsync($"{Episodes}/{id}/playback", Playback(new { positionSeconds = 300 }));

        var before = DateTime.UtcNow;
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync($"{Episodes}/{id}/playback", Playback(new { completed = true }))).StatusCode);

        var detail = await client.GetFromJsonAsync<PodcastEpisodeDetail>($"{Episodes}/{id}");
        Assert.NotNull(detail!.LastListenedAtUtc);
        Assert.InRange(detail.LastListenedAtUtc!.Value, before.AddSeconds(-5), DateTime.UtcNow.AddSeconds(5));
        Assert.Null(detail.PlaybackPositionSeconds);
        Assert.Null(detail.LastPlayedAtUtc);
    }

    [Fact]
    public async Task Playback_AC10_A_finish_without_stored_audio_is_409_and_writes_nothing()
    {
        var feed = await PodcastFeedAsync();
        var id = await EpisodeAsync(feed, PodcastAudioState.Queued);
        using var client = Factory.ForCaller(AllNewsClaims).CreateClient();

        var response = await client.PutAsync($"{Episodes}/{id}/playback", Playback(new { completed = true }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(NewsErrorCodes.PodcastAudioNotStored, (await response.ProblemAsync()).Code());
        Assert.Null((await EpisodeRowAsync(id)).LastListenedAtUtc);
    }

    [Theory]
    [InlineData(30, false)]
    [InlineData(PodcastLimits.PlaybackCompletionGuardSeconds + 60, true)]
    public async Task Playback_AC11_A_position_save_just_after_a_finish_is_ignored(int secondsSinceFinish, bool applies)
    {
        var feed = await PodcastFeedAsync();
        var id = await EpisodeAsync(feed, PodcastAudioState.Queued);
        await StoreAudioAsync(feed, id);
        await Factory.WithContextAsync(async c =>
        {
            (await c.PodcastEpisodes.SingleAsync(e => e.NewsArticleId == id)).LastListenedAtUtc = DateTime.UtcNow.AddSeconds(-secondsSinceFinish);
            await c.SaveChangesAsync();
        });
        using var client = Factory.ForCaller(AllNewsClaims).CreateClient();

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync($"{Episodes}/{id}/playback", Playback(new { positionSeconds = 3000 }))).StatusCode);

        Assert.Equal(applies ? 3000 : null, (await EpisodeRowAsync(id)).PlaybackPositionSeconds);
    }

    [Theory]
    [InlineData("{\"completed\":false}")]
    [InlineData("{\"positionSeconds\":-1}")]
    [InlineData("{\"positionSeconds\":604801}")]
    public async Task Playback_AC12_An_out_of_range_or_missing_position_is_400(string body)
    {
        var feed = await PodcastFeedAsync();
        var id = await EpisodeAsync(feed, PodcastAudioState.Queued);
        await StoreAudioAsync(feed, id);
        using var client = Factory.ForCaller(AllNewsClaims).CreateClient();

        var response = await client.PutAsync($"{Episodes}/{id}/playback", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.ProblemAsync()).HasError("PositionSeconds"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsync($"{Episodes}/{Guid.NewGuid()}/playback", Playback(new { positionSeconds = 1 }))).StatusCode);
    }

    [Fact]
    public async Task Playback_AC18_Extra_members_change_only_the_two_playback_columns()
    {
        var feed = await PodcastFeedAsync();
        var id = await EpisodeAsync(feed, PodcastAudioState.Queued);
        await StoreAudioAsync(feed, id);
        using var client = Factory.ForCaller(AllNewsClaims).CreateClient();

        var response = await client.PutAsync($"{Episodes}/{id}/playback", Playback(new
        {
            positionSeconds = 42,
            lastPlayedAtUtc = Base,
            lastListenedAtUtc = Base,
            audio = new { state = "None", storedObjectId = Guid.NewGuid() },
            enclosureUrl = "https://evil.example/x.mp3",
        }));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var row = await EpisodeRowAsync(id);
        Assert.Equal((42, (DateTime?)null, (DateTime?)null, "https://cdn.example.org/a.mp3"), (row.PlaybackPositionSeconds, row.LastPlayedAtUtc, row.LastListenedAtUtc, row.EnclosureUrl));
        Assert.Equal(PodcastAudioState.Stored, (await AudioRowAsync(id)).State);
    }

    [Fact]
    public async Task Playback_AC14_A_finished_listen_sets_the_auto_delete_date_and_savedToDevice_ignores_it()
    {
        var feed = await PodcastFeedAsync();
        await Factory.WithContextAsync(async c =>
        {
            var f = await c.NewsFeeds.SingleAsync(x => x.NewsFeedId == feed);
            f.AutoDeleteEnabled = true;
            f.AutoDeleteAfterDays = 30;
            f.AutoDeleteChangedAtUtc = null;
            await c.SaveChangesAsync();
        });
        var id = await EpisodeAsync(feed, PodcastAudioState.Queued);
        await StoreAudioAsync(feed, id);
        using var client = Factory.ForCaller(AllNewsClaims).CreateClient();
        await client.PutAsync($"{Episodes}/{id}/playback", Playback(new { completed = true }));

        var detail = await client.GetFromJsonAsync<PodcastEpisodeDetail>($"{Episodes}/{id}");
        Assert.Equal(detail!.LastListenedAtUtc!.Value.AddDays(30), detail.AutoDeleteAtUtc);

        var saved = await client.GetFromJsonAsync<PodcastEpisodePage>($"{Episodes}?feedIds={feed}&savedToDevice=true");
        Assert.DoesNotContain(saved!.Items, e => e.Id == id);
    }

    public sealed class Host() : OdysseyApiFactory([]);
}
