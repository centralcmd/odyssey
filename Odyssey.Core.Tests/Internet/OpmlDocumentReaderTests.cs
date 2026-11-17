using System.Text;
using Odyssey.Core.Internet.News.Opml;
using Xunit;

namespace Odyssey.Core.Tests.Internet;

/// <summary>
/// The OPML reader of issue #508 §6 and §8.1–§8.3. Test names carry the AC they pin; the hostile-XML cases are AC 24
/// and AC 25, the foreign-file mapping AC 8, the escape round trip the tag half of AC 6.
/// </summary>
public sealed class OpmlDocumentReaderTests
{
    private static List<OpmlFeedOutline> Read(string xml, Encoding? encoding = null, int chunkSize = OpmlTextReader.DefaultChunkSize)
    {
        var outlines = new List<OpmlFeedOutline>();
        using var stream = new MemoryStream((encoding ?? new UTF8Encoding(false)).GetBytes(xml));
        OpmlDocumentReader.Read(stream, outlines.Add, CancellationToken.None, chunkSize);
        return outlines;
    }

    private static List<OpmlFeedOutline> Read(byte[] bytes)
    {
        var outlines = new List<OpmlFeedOutline>();
        using var stream = new MemoryStream(bytes);
        OpmlDocumentReader.Read(stream, outlines.Add);
        return outlines;
    }

    private const string Odyssey = "xmlns:odyssey=\"urn:odyssey:opml:1\"";

    [Fact]
    public void AC8_A_foreign_opml_1_file_maps_folders_typeless_entries_literal_categories_and_text_as_the_custom_title()
    {
        var outlines = Read("""
            <?xml version="1.0"?>
            <opml version="1.0">
              <head><title>Old reader</title></head>
              <body>
                <outline text="Local">
                  <outline text="Harbour Times" title="Ignored" xmlUrl=" https://harbour.example/rss " category="/News/Norway,a%2Fb"/>
                  <outline title="Title only" xmlUrl="https://title.example/feed"/>
                </outline>
                <outline text="Not a feed and no children"/>
                <outline type="atom" text="Top" xmlUrl="https://top.example/atom" odyssey:customTitle="x" xmlns:odyssey="urn:odyssey:opml:1"/>
              </body>
            </opml>
            """);

        Assert.Equal(3, outlines.Count);
        Assert.Equal("https://harbour.example/rss", outlines[0].Url);
        Assert.Equal("Harbour Times", outlines[0].CustomTitle);
        Assert.Equal(["Local", "Norway", "a%2Fb"], outlines[0].TagNames);
        Assert.Equal("Title only", outlines[1].CustomTitle);
        Assert.Equal(["Local"], outlines[1].TagNames);

        // Not an Odyssey file (the root does not declare the namespace), so the extension is ignored.
        Assert.Equal("Top", outlines[2].CustomTitle);
        Assert.Empty(outlines[2].TagNames);
        Assert.All(outlines, o => Assert.True(o.DownloadImages && o.IsActive && o.FetchIntervalMinutes is null));
    }

    [Fact]
    public void An_odyssey_file_reads_the_extension_by_namespace_under_any_prefix()
    {
        var outlines = Read("""
            <opml version="2.0" xmlns:o="urn:odyssey:opml:1">
              <body>
                <outline text="Display" xmlUrl="https://a.example/rss" category="/100%25,/a%2Cb,/a%2Fb,/a%252Fb,/50%zz"
                         o:customTitle="Mine" o:fetchIntervalMinutes="60" o:downloadImages="false" o:active="false"/>
                <outline text="Publisher title" xmlUrl="https://b.example/rss" o:fetchIntervalMinutes="5" o:downloadImages="maybe"/>
              </body>
            </opml>
            """);

        Assert.Equal("Mine", outlines[0].CustomTitle);
        Assert.Equal(60, outlines[0].FetchIntervalMinutes);
        Assert.False(outlines[0].DownloadImages);
        Assert.False(outlines[0].IsActive);
        Assert.Equal(["100%", "a,b", "a/b", "a%2Fb", "50%zz"], outlines[0].TagNames);

        // In an Odyssey file the text is the display title, never frozen as custom; an out-of-range interval is null.
        Assert.Null(outlines[1].CustomTitle);
        Assert.Equal("Publisher title", outlines[1].ReportTitle);
        Assert.Null(outlines[1].FetchIntervalMinutes);
        Assert.True(outlines[1].DownloadImages);
    }

    [Fact]
    public void A_feed_outlines_children_are_read_and_take_the_nearest_folder()
    {
        var outlines = Read("""
            <opml><body>
              <outline text="Outer"><outline text="Inner">
                <outline text="Parent feed" xmlUrl="https://p.example/rss">
                  <outline text="Child feed" xmlUrl="https://c.example/rss"/>
                </outline>
              </outline></outline>
              <outline text="After" xmlUrl="https://after.example/rss"/>
            </body></opml>
            """);

        Assert.Equal(["Inner"], outlines[0].TagNames);
        Assert.Equal(["Inner"], outlines[1].TagNames);
        Assert.Empty(outlines[2].TagNames);
    }

    [Fact]
    public void AC20_An_outline_with_ten_thousand_category_segments_reads_at_most_sixty_four()
    {
        // Three characters a segment keeps the attribute under the token cap (30,000 characters).
        var category = string.Join(',', Enumerable.Range(0, 10_000).Select(i => $"/{(char)('a' + (i % 26))}"));
        var outline = Read($"<opml><body><outline xmlUrl=\"https://x.example/\" category=\"{category}\"/></body></opml>").Single();

        Assert.Equal(64, outline.TagNames.Count);
        Assert.Equal("l", outline.TagNames[^1]);
    }

    [Fact]
    public void The_version_attribute_is_never_enforced()
    {
        Assert.Single(Read("<opml version=\"9.9\"><body><outline xmlUrl=\"https://x.example/\"/></body></opml>"));
        Assert.Single(Read("<opml><body><outline xmlUrl=\"https://x.example/\"/></body></opml>"));
    }

    [Fact]
    public void The_encoding_comes_from_the_bom_then_the_declaration()
    {
        var latin = "<?xml version=\"1.0\" encoding=\"iso-8859-1\"?><opml><body><outline text=\"Café\" xmlUrl=\"https://x.example/\"/></body></opml>";
        Assert.Equal("Café", Read(latin, Encoding.Latin1).Single().CustomTitle);

        var utf16 = "<opml><body><outline text=\"Ærø\" xmlUrl=\"https://x.example/\"/></body></opml>";
        Assert.Equal("Ærø", Read([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(utf16)]).Single().CustomTitle);
        Assert.Equal("Ærø", Read([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(utf16)]).Single().CustomTitle);
    }

    [Fact]
    public void An_encoding_dotnet_cannot_decode_is_unreadable() =>
        Assert.Throws<OpmlUnreadableException>(() =>
            Read("<?xml version=\"1.0\" encoding=\"x-not-a-charset\"?><opml><body/></opml>"));

    public static TheoryData<string> Unreadable() => new()
    {
        { "<opml><head/></opml>" },
        { "<rss version=\"2.0\"><channel/></rss>" },
        { "<opml><body><outline xmlUrl=\"https://x.example/\"></body></opml>" },
        { "not xml at all" },
        { "<opml/>" },
        { "" },
    };

    [Theory]
    [MemberData(nameof(Unreadable))]
    public void AC25_No_body_a_wrong_root_or_malformed_xml_is_unreadable(string xml) =>
        Assert.Throws<OpmlUnreadableException>(() => Read(xml));

    [Fact]
    public void AC25_A_png_renamed_opml_is_unreadable()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, 0x3C, 0x26, 0xFF, 0xFE];
        Assert.Throws<OpmlUnreadableException>(() => Read(png));
    }

    [Fact]
    public void AC24_An_external_entity_dtd_is_never_resolved()
    {
        // The reference is made literal text, never fetched: the reader has no resolver and ignores the DTD.
        var outline = Read("""
            <!DOCTYPE opml [<!ENTITY xxe SYSTEM "http://attacker.invalid/secret">]>
            <opml><body><outline text="&xxe;" xmlUrl="https://x.example/"/></body></opml>
            """).Single();

        Assert.Equal("&xxe;", outline.CustomTitle);
    }

    [Fact]
    public void AC24_A_billion_laughs_dtd_expands_nothing()
    {
        var entities = string.Concat(Enumerable.Range(1, 9).Select(i => $"<!ENTITY l{i} \"&l{i - 1};&l{i - 1};&l{i - 1};&l{i - 1};&l{i - 1};&l{i - 1};&l{i - 1};&l{i - 1};&l{i - 1};&l{i - 1};\">"));
        var outline = Read($"<!DOCTYPE opml [<!ENTITY l0 \"lol\">{entities}]><opml><body><outline text=\"&l9;\" xmlUrl=\"https://x.example/\"/></body></opml>").Single();

        Assert.Equal("&l9;", outline.CustomTitle);
    }

    [Fact]
    public void AC24_Nesting_deeper_than_sixty_four_is_unreadable()
    {
        var xml = "<opml><body>" + string.Concat(Enumerable.Repeat("<outline text=\"f\">", 70)) + string.Concat(Enumerable.Repeat("</outline>", 70)) + "</body></opml>";
        Assert.Throws<OpmlUnreadableException>(() => Read(xml));
    }

    public static TheoryData<string> OversizedTokens() => new()
    {
        { $"<opml><body><outline text=\"{new string('a', 100_000)}\" xmlUrl=\"https://x.example/\"/></body></opml>" },
        { $"<opml><body><outline text=\"{string.Concat(Enumerable.Repeat(new string('b', 59_999) + ">", 3))}\" xmlUrl=\"https://x.example/\"/></body></opml>" },
        { $"<opml><body><![CDATA[{new string('c', 100_000)}]]></body></opml>" },
        { $"<opml><body><!--{new string('d', 100_000)}--></body></opml>" },
        { $"<opml><!--{string.Concat(Enumerable.Repeat("<" + new string('e', 999), 100))}--><body/></opml>" },
        { $"<!DOCTYPE opml [{string.Concat(Enumerable.Repeat("<!ENTITY e 'x'>", 6000))}]><opml><body/></opml>" },
        { $"<!DOCTYPE a [ <!-- ' --> ]><opml><body><outline xmlUrl=\"https://x.example/\"/>{new string(' ', 70_000)}</body></opml>" },
    };

    [Theory]
    [MemberData(nameof(OversizedTokens))]
    public void AC24_One_token_over_the_cap_fails_the_whole_file(string xml)
    {
        var seen = new List<OpmlFeedOutline>();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        Assert.Throws<OpmlUnreadableException>(() => OpmlDocumentReader.Read(stream, seen.Add));
    }

    [Fact]
    public void AC24_A_60_MB_attribute_with_a_greater_than_every_60000_characters_is_refused_before_it_is_materialised()
    {
        // A stream that would yield 60 MB; the reader must stop after one token's worth.
        var produced = 0L;
        using var stream = new GeneratedStream("<opml><body><outline xmlUrl=\"https://x.example/\" text=\"", 60L * 1024 * 1024,
            i => i % 60_000 == 59_999 ? (byte)'>' : (byte)'a', read => produced = read);
        Assert.Throws<OpmlUnreadableException>(() => OpmlDocumentReader.Read(stream, _ => { }));
        Assert.True(produced < 1024 * 1024, $"read {produced} bytes");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8192)]
    public void A_chunk_size_never_changes_what_is_read(int chunkSize)
    {
        var xml = "<opml " + Odyssey + "><body><outline text=\"F&amp;B &nbsp;x\" xmlUrl=\"https://x.example/?a=1&amp;b=2\" category=\"/A%2CB\"/></body></opml>";
        var outline = Read(xml, chunkSize: chunkSize).Single();

        Assert.Equal("https://x.example/?a=1&b=2", outline.Url);
        Assert.Equal("F&B  x", outline.ReportTitle);
        Assert.Equal(["A,B"], outline.TagNames);
    }

    /// <summary>A prefix followed by <paramref name="length"/> generated bytes, reporting how far it was read.</summary>
    private sealed class GeneratedStream(string prefix, long length, Func<long, byte> generate, Action<long> report) : Stream
    {
        private readonly byte[] head = Encoding.UTF8.GetBytes(prefix);
        private long position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => head.Length + length;

        public override long Position { get => position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = 0;
            while (n < count && position < Length)
            {
                buffer[offset + n++] = position < head.Length ? head[position] : generate(position - head.Length);
                position++;
            }

            report(position);
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
