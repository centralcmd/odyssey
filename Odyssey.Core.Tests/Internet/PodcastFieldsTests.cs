using System.Text;
using Odyssey.Core.Internet.News.Parsing;
using Odyssey.Core.Internet.News.Podcasts;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;
using Xunit;

namespace Odyssey.Core.Tests.Internet;

/// <summary>The podcast field rules (issue #556 §8.3, §8.5, §8.6) and the parser's podcast namespaces.</summary>
public sealed class PodcastFieldsTests
{
    private const string Rss = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd"
             xmlns:podcast="https://podcastindex.org/namespace/1.0" xmlns:psc="http://podlove.org/simple-chapters"
             xmlns:content="http://purl.org/rss/1.0/modules/content/">
          <channel>
            <title>The Show</title>
            <link>https://show.example.org/</link>
            <itunes:author>  Jane  Host </itunes:author>
            <itunes:explicit>no</itunes:explicit>
            <itunes:type>serial</itunes:type>
            <image><url>https://show.example.org/rss-cover.png</url></image>
            <itunes:image href="https://show.example.org/cover.jpg"/>
            <item>
              <guid>ep-14</guid>
              <title>Episode 14</title>
              <pubDate>Sun, 25 Oct 2026 07:00:00 GMT</pubDate>
              <description><![CDATA[<p>Show <b>notes</b> <script>alert(1)</script></p>]]></description>
              <enclosure url="https://cdn.example.org/ep14.mp3?token=abc" type="audio/mpeg" length="49283012"/>
              <itunes:duration>1:02:03</itunes:duration>
              <itunes:season>2</itunes:season>
              <itunes:episode>14</itunes:episode>
              <itunes:episodeType>bonus</itunes:episodeType>
              <itunes:explicit>true</itunes:explicit>
              <itunes:image href="https://show.example.org/ep14.jpg"/>
              <itunes:subtitle><![CDATA[A <i>short</i> subtitle]]></itunes:subtitle>
              <podcast:chapters url="https://show.example.org/ep14.json" type="application/json+chapters"/>
              <psc:chapters version="1.2">
                <psc:chapter start="00:05:00" title="Second" href="https://show.example.org/second"/>
                <psc:chapter start="00:00:00.5" title="Intro"/>
                <psc:chapter start="00:05:00" title="Duplicate start"/>
                <psc:chapter start="nonsense" title="Skipped"/>
                <psc:chapter start="00:10:00" title="" href="javascript:alert(1)"/>
              </psc:chapters>
            </item>
            <item>
              <guid>video</guid>
              <title>A video</title>
              <enclosure url="https://cdn.example.org/v.mp4" type="video/mp4"/>
            </item>
            <item>
              <guid>untyped</guid>
              <title>Untyped audio</title>
              <enclosure url="https://cdn.example.org/u.MP3"/>
            </item>
          </channel>
        </rss>
        """;

    private static NewsParsedFeed Parse(string text, string url = "https://show.example.org/feed.xml")
    {
        var result = NewsFeedParser.Parse(Encoding.UTF8.GetBytes(text), new Uri(url));
        Assert.Equal(NewsFeedParseOutcome.Ok, result.Outcome);
        return result.Feed!;
    }

    [Fact]
    public void AC3_An_rss_itunes_item_becomes_an_episode_with_every_field_normalised()
    {
        var feed = Parse(Rss);
        var item = feed.Items.Single(i => i.Guid == "ep-14");

        var episode = PodcastFields.NormalizeEpisode(item.Podcast, item.BaseUrl)!;

        Assert.Equal("https://cdn.example.org/ep14.mp3?token=abc", episode.EnclosureUrl);
        Assert.Equal(("audio/mpeg", 49283012L), (episode.EnclosureDeclaredType, episode.EnclosureDeclaredLength));
        Assert.Equal(3723, episode.DurationSeconds);
        Assert.Equal((2, 14), (episode.SeasonNumber, episode.EpisodeNumber));
        Assert.Equal(PodcastEpisodeType.Bonus, episode.EpisodeType);
        Assert.True(episode.Explicit);
        Assert.Equal("https://show.example.org/ep14.jpg", episode.ImageUrl);
        Assert.Equal("A short subtitle", episode.Description);
        Assert.Equal("https://show.example.org/ep14.json", episode.ChaptersUrl);
        Assert.Equal(32, episode.EpisodeHash.Length);

        // §8.6: sorted, the first of a duplicated start kept, an unparseable start skipped, an empty title numbered and
        // a javascript: link dropped.
        Assert.Equal(
            [(500L, "Intro", (string?)null), (300_000L, "Second", "https://show.example.org/second"), (600_000L, "Chapter 3", null)],
            episode.FeedChapters.Select(c => (c.StartMs, c.Title, c.Url)));
    }

    [Fact]
    public void AC3_The_channel_fields_prefer_the_itunes_image_and_clean_the_author()
    {
        var channel = PodcastFields.NormalizeFeed(Parse(Rss).Podcast)!;

        Assert.Equal("Jane Host", channel.Author);
        Assert.Equal("https://show.example.org/cover.jpg", channel.ImageUrl);
        Assert.False(channel.Explicit);
        Assert.Equal(PodcastType.Serial, channel.PodcastType);
    }

    [Fact]
    public void AC5_AC6_A_video_item_is_no_episode_and_an_untyped_mp3_enclosure_is_audio_not_an_image()
    {
        var feed = Parse(Rss);

        Assert.Null(PodcastFields.NormalizeEpisode(feed.Items.Single(i => i.Guid == "video").Podcast, null));
        var untyped = feed.Items.Single(i => i.Guid == "untyped");
        Assert.Equal("https://cdn.example.org/u.MP3", PodcastFields.NormalizeEpisode(untyped.Podcast, null)!.EnclosureUrl);
        Assert.Empty(untyped.Images);
        Assert.Empty(feed.Items.Single(i => i.Guid == "ep-14").Images);
    }

    [Fact]
    public void AC4_An_atom_entry_and_a_json_feed_item_produce_episodes_from_their_audio_enclosures()
    {
        var atom = Parse("""
            <feed xmlns="http://www.w3.org/2005/Atom" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd">
              <title>Atom show</title>
              <entry>
                <id>tag:example.org,2026:1</id><title>Atom episode</title><updated>2026-10-25T07:00:00Z</updated>
                <link rel="alternate" href="https://atom.example.org/1"/>
                <link rel="enclosure" type="audio/mp4" length="1000" href="https://cdn.example.org/1.m4a"/>
                <itunes:duration>95</itunes:duration>
              </entry>
            </feed>
            """);
        var json = Parse("""
            {"version":"https://jsonfeed.org/version/1.1","title":"Json show","items":[
              {"id":"1","title":"Json episode","content_text":"Notes",
               "attachments":[{"url":"https://cdn.example.org/1.png","mime_type":"image/png"},
                              {"url":"https://cdn.example.org/1.mp3","mime_type":"audio/mpeg","size_in_bytes":2000,"duration_in_seconds":61.7}]}]}
            """);

        var fromAtom = PodcastFields.NormalizeEpisode(atom.Items.Single().Podcast, null)!;
        var fromJson = PodcastFields.NormalizeEpisode(json.Items.Single().Podcast, null)!;

        Assert.Equal(("https://cdn.example.org/1.m4a", 95), (fromAtom.EnclosureUrl, fromAtom.DurationSeconds));
        Assert.Equal(("https://cdn.example.org/1.mp3", 2000L, 61), (fromJson.EnclosureUrl, fromJson.EnclosureDeclaredLength, fromJson.DurationSeconds));
    }

    [Theory]
    [InlineData("3723", 3723)]
    [InlineData("62:03", 3723)]
    [InlineData("1:02:03.900", 3723)]
    [InlineData("1:2:3:4", null)]
    [InlineData("-5", null)]
    [InlineData("12 minutes", null)]
    [InlineData("999999999:00:00", null)]
    [InlineData("", null)]
    public void Durations_follow_the_three_itunes_forms_and_refuse_anything_else(string value, int? seconds) =>
        Assert.Equal(seconds, PodcastFields.ParseDuration(value));

    [Theory]
    [InlineData("01:02:03.25", 3_723_250L)]
    [InlineData("02:03", 123_000L)]
    [InlineData("7.5", 7_500L)]
    [InlineData("1:2:3:4", null)]
    [InlineData("1.x", null)]
    public void Podlove_starts_are_normal_play_time_in_milliseconds(string value, long? ms) =>
        Assert.Equal(ms, PodcastFields.ParseNormalPlayTime(value));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1d)]
    [InlineData(700_000d)]
    public void AC28_A_non_finite_negative_or_overlong_json_time_is_refused(double seconds) =>
        Assert.Null(PodcastFields.MillisecondsFromSeconds(seconds));

    [Fact]
    public void AC28_Every_chapter_source_shares_the_cleaning_range_and_cap_rules()
    {
        var raw = Enumerable.Range(0, PodcastLimits.MaxChapters + 20)
            .Select(i => new PodcastRawChapter(i * 1000L, i == 0 ? -5 : (i * 1000L) + 500, i == 1 ? "a\u0000b‮" : $"T{i}", i == 2 ? "javascript:x" : null))
            .Append(new PodcastRawChapter(null, null, "no start", null))
            .Append(new PodcastRawChapter(PodcastLimits.MaxChapterMilliseconds + 1, null, "too late", null));

        var chapters = PodcastFields.NormalizeChapters(raw);

        Assert.Equal(PodcastLimits.MaxChapters, chapters.Count);
        Assert.Null(chapters[0].EndMs); // an end before the start becomes unset
        Assert.Equal("ab", chapters[1].Title);
        Assert.Null(chapters[2].Url);
        Assert.DoesNotContain(chapters, c => c.Title is "no start" or "too late");
    }

    [Fact]
    public void The_episode_hash_changes_with_an_episode_field_and_not_without_one()
    {
        var item = Parse(Rss).Items.Single(i => i.Guid == "ep-14");
        var first = PodcastFields.NormalizeEpisode(item.Podcast, item.BaseUrl)!;
        var again = PodcastFields.NormalizeEpisode(item.Podcast, item.BaseUrl)!;
        var changed = PodcastFields.NormalizeEpisode(item.Podcast! with { Duration = "61:00" }, item.BaseUrl)!;

        Assert.Equal(first.EpisodeHash, again.EpisodeHash);
        Assert.NotEqual(first.EpisodeHash, changed.EpisodeHash);
    }
}
