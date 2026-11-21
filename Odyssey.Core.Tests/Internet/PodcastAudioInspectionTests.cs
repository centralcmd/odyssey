using System.Diagnostics;
using Odyssey.Core.Internet.News.Podcasts;
using Odyssey.Core.Internet.News.Podcasts.Audio;
using Odyssey.Testing.News;
using Xunit;
using static Odyssey.Testing.News.PodcastAudioFixtures;

namespace Odyssey.Core.Tests.Internet;

/// <summary>
/// Audio detection by magic bytes and the bounded chapter-marker reader (issue #556 §6.1, §7.7, AC 29): every format of
/// the table, the declared type never consulted, chapters from ID3 <c>CHAP</c>/<c>CTOC</c> and MP4 <c>chpl</c> wherever
/// <c>moov</c> sits, and a fuzzed corpus that yields no exception and finishes inside the deadline.
/// </summary>
public sealed class PodcastAudioInspectionTests
{
    public static TheoryData<string, byte[], string?> Bodies => new()
    {
        { "id3 + mpeg", Mp3(), "audio/mpeg" },
        { "bare mpeg", Mp3Frames(), "audio/mpeg" },
        { "adts", Adts(), "audio/aac" },
        { "m4a", M4a([], moovAtHead: true), "audio/mp4" },
        { "opus", Ogg(), "audio/ogg" },
        { "vorbis", Ogg("\u0001vorbis"), "audio/ogg" },
        { "flac", Flac(), "audio/flac" },
        { "html", "<!doctype html><html>not audio</html>"u8.ToArray(), null },
        { "ogg theora", Ogg("\u0080theora"), null },
        { "one mpeg frame", Mp3Frames(1), null },
        { "png", [0x89, .. "PNG"u8, 0x0D, 0x0A, 0x1A, 0x0A, .. new byte[64]], null },
        { "mp4 video brand", PodcastAudioFixtures.Box("ftyp", [.. "qt  "u8, 0, 0, 0, 0, .. "qt  "u8]), null },
        { "empty", [], null },
    };

    [Theory]
    [MemberData(nameof(Bodies))]
    public void Audio_is_detected_from_its_magic_bytes_alone(string name, byte[] body, string? expected)
    {
        Assert.True(expected == PodcastAudioFormat.Detect(body)?.ContentType, name);
    }

    [Fact]
    public void An_id3_tag_longer_than_the_detection_window_counts_as_mpeg()
    {
        var head = Mp3(padding: PodcastAudioFormat.HeadBytes)[..PodcastAudioFormat.HeadBytes];
        Assert.Equal("audio/mpeg", PodcastAudioFormat.Detect(head)?.ContentType);
    }

    [Fact]
    public async Task AC29_Id3_chapters_follow_the_table_of_contents_with_titles_and_links()
    {
        var file = Mp3([new(60_000, null, "Main", "https://show.example.org/main"), new(0, 60_000, "Intro")]);

        var chapters = PodcastFields.NormalizeChapters(await ReadAsync(file, "audio/mpeg"));

        Assert.Equal(
            [(0L, (long?)60_000, "Intro", (string?)null), (60_000L, null, "Main", "https://show.example.org/main")],
            chapters.Select(c => (c.StartMs, c.EndMs, c.Title, c.Url)));
    }

    [Fact]
    public async Task Id3_chapters_without_a_table_of_contents_are_read_in_file_order()
    {
        var chapters = await ReadAsync(Mp3([new(0, null, "One"), new(5_000, null, "Two")], withToc: false), "audio/mpeg");
        Assert.Equal(["One", "Two"], chapters.Select(c => c.Title));
    }

    [Fact]
    public async Task AC29_A_ctoc_cycle_ends_and_a_chapter_outside_every_table_is_left_out()
    {
        var file = Mp3WithFrames(
            ("CTOC", Ctoc("root", topLevel: true, "a", "loop")),
            ("CTOC", Ctoc("loop", topLevel: false, "root", "loop", "b")),
            ("CHAP", Chap("a", new(0, null, "A"))),
            ("CHAP", Chap("b", new(1_000, null, "B"))),
            ("CHAP", Chap("orphan", new(2_000, null, "Orphan"))));

        var chapters = await ReadAsync(file, "audio/mpeg");

        Assert.Equal(["A", "B"], chapters.Select(c => c.Title));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AC29_Mp4_chpl_chapters_are_read_with_moov_at_the_head_or_the_tail(bool moovAtHead)
    {
        var file = M4a([(TimeSpan.Zero, "Cold open"), (TimeSpan.FromSeconds(90), "Interview")], moovAtHead, mdatBytes: 256 * 1024);

        var chapters = await ReadAsync(file, "audio/mp4");

        Assert.Equal(
            [(0L, (long?)90_000, "Cold open"), (90_000L, null, "Interview")],
            chapters.Select(c => (c.StartMs!.Value, c.EndMs, c.Title!)));
    }

    [Fact]
    public async Task A_tag_above_the_window_or_an_unsynchronised_tag_yields_no_chapters()
    {
        var oversized = Id3Header(4, PodcastAudioChapters.MaxId3Bytes + 1);
        Assert.Empty(await ReadAsync([.. oversized, .. new byte[64]], "audio/mpeg"));

        var unsynchronised = Mp3([new(0, null, "x")]);
        unsynchronised[5] = 0x80;
        Assert.Empty(await ReadAsync(unsynchronised, "audio/mpeg"));
    }

    [Fact]
    public async Task AC29_A_fuzzed_corpus_yields_no_exception_and_finishes_within_the_deadline()
    {
        var seeds = new[]
        {
            (Mp3([new(0, 10, "a"), new(10, 20, "b"), new(20, null, "c")]), "audio/mpeg"),
            (M4a([(TimeSpan.Zero, "a"), (TimeSpan.FromSeconds(1), "b")], moovAtHead: true), "audio/mp4"),
            (M4a([(TimeSpan.Zero, "a")], moovAtHead: false), "audio/mp4"),
        };
        var random = new Random(556);
        var clock = Stopwatch.StartNew();
        foreach (var (seed, type) in seeds)
        {
            for (var round = 0; round < 400; round++)
            {
                var file = (byte[])seed.Clone();
                switch (round % 4)
                {
                    case 0:
                        file = file[..random.Next(file.Length)];
                        break;
                    case 1:
                        for (var k = 0; k < 8; k++)
                        {
                            file[random.Next(file.Length)] = (byte)random.Next(256);
                        }

                        break;
                    case 2:
                        // An oversized length somewhere a length lives.
                        var at = random.Next(Math.Max(1, file.Length - 4));
                        file[at] = 0x7F;
                        file[at + 1] = 0xFF;
                        break;
                    default:
                        random.NextBytes(file.AsSpan(random.Next(file.Length)));
                        break;
                }

                using var deadline = new CancellationTokenSource(PodcastAudioChapters.Deadline);
                var chapters = await PodcastAudioChapters.ReadAsync(new MemoryStream(file), type, deadline.Token);
                Assert.True(chapters.Count <= 10_000);
                _ = PodcastFields.NormalizeChapters(chapters);
            }
        }

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"The corpus took {clock.Elapsed}.");
    }

    private static Task<IReadOnlyList<PodcastRawChapter>> ReadAsync(byte[] file, string type) =>
        PodcastAudioChapters.ReadAsync(new MemoryStream(file), type, CancellationToken.None);
}
