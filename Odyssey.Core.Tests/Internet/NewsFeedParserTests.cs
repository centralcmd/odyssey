using System.Text;
using System.Xml.Linq;
using Odyssey.Context;
using Odyssey.Core.Internet.News.Parsing;
using Odyssey.Dtos.Internet;
using Xunit;

namespace Odyssey.Core.Tests.Internet;

/// <summary>
/// The feed parser (issue #470 §8.6, §7.10): the fixture corpus (AC 33), format detection (AC 34), the hardening
/// (AC 17, AC 18), the raw capture (AC 31) and decoding (AC 32).
/// </summary>
public class NewsFeedParserTests
{
    private static readonly Uri FeedUrl = new("https://news.example.com/feed.xml");

    private static NewsParsedFeed Parse(string body, string? charset = null)
    {
        var result = NewsFeedParser.Parse(Encoding.UTF8.GetBytes(body), FeedUrl, charset);
        Assert.Equal(NewsFeedParseOutcome.Ok, result.Outcome);
        return result.Feed!;
    }

    private static NewsFeedParseOutcome Outcome(string body) => NewsFeedParser.Parse(Encoding.UTF8.GetBytes(body), FeedUrl).Outcome;

    // ── AC 33: the fixture corpus ───────────────────────────────────────────────────────────────────────

    private const string Rss20ContentEncoded = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:content="http://purl.org/rss/1.0/modules/content/" xmlns:dc="http://purl.org/dc/elements/1.1/">
          <channel>
            <title>Example News</title>
            <link>https://www.example.com/</link>
            <description>All the news</description>
            <language>en-gb</language>
            <ttl>45</ttl>
            <item>
              <title>First story</title>
              <link>https://www.example.com/1</link>
              <guid isPermaLink="false">story-1</guid>
              <pubDate>Mon, 16 Nov 2026 09:30:00 GMT</pubDate>
              <dc:creator>Ada Writer</dc:creator>
              <description>A short summary.</description>
              <content:encoded><![CDATA[<p>The <b>full</b> body.</p>]]></content:encoded>
            </item>
          </channel>
        </rss>
        """;

    [Fact]
    public void AC33_rss20_with_content_encoded()
    {
        var feed = Parse(Rss20ContentEncoded);

        Assert.Equal(NewsFeedFormat.Rss, feed.Format);
        Assert.Equal("Example News", feed.Title);
        Assert.Equal("https://www.example.com/", feed.SiteUrl);
        Assert.Equal("All the news", feed.Description);
        Assert.Equal("en-gb", feed.Language);
        Assert.Equal(45, feed.TtlMinutes);
        var item = Assert.Single(feed.Items);
        Assert.Equal("story-1", item.Guid);
        Assert.Equal("https://www.example.com/1", item.Link);
        Assert.Equal("First story", item.Title!.Value);
        Assert.Equal("Ada Writer", item.Author);
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 9, 30, 0, TimeSpan.Zero), item.PublishedAt);
        Assert.Equal(new NewsTextField("<p>The <b>full</b> body.</p>", NewsTextKind.Html), item.Content);
        Assert.Equal(new NewsTextField("A short summary.", NewsTextKind.Html), item.Summary);
        Assert.Equal(NewsFeedParser.XmlSourceContentType, item.SourceContentType);
    }

    [Fact]
    public void AC33_rss20_with_every_item_sharing_one_link()
    {
        var items = string.Concat(Enumerable.Range(1, 3).Select(i =>
            $"<item><title>Item {i}</title><link>https://example.com/live</link><guid>live-{i}</guid></item>"));
        var feed = Parse($"<rss version=\"2.0\"><channel><title>Live</title>{items}</channel></rss>");

        Assert.Equal(3, feed.Items.Count);
        Assert.All(feed.Items, i => Assert.Equal("https://example.com/live", i.Link));
        Assert.Equal(["live-1", "live-2", "live-3"], feed.Items.Select(i => i.Guid));
    }

    [Fact]
    public void AC33_rss091_with_a_netscape_doctype()
    {
        var feed = Parse("""
            <?xml version="1.0" encoding="ISO-8859-1"?>
            <!DOCTYPE rss PUBLIC "-//Netscape Communications//DTD RSS 0.91//EN" "http://my.netscape.com/publish/formats/rss-0.91.dtd">
            <rss version="0.91"><channel><title>Old</title><link>http://old.example/</link>
            <item><title>Legacy &amp; proud</title><link>http://old.example/a</link><description>Text</description></item>
            </channel></rss>
            """);

        Assert.Equal(NewsFeedFormat.Rss, feed.Format);
        var item = Assert.Single(feed.Items);
        Assert.Equal("Legacy & proud", item.Title!.Value);
        Assert.Null(item.Guid);
    }

    [Fact]
    public void AC33_atom_with_xml_base_resolves_relative_links_and_content()
    {
        var feed = Parse("""
            <feed xmlns="http://www.w3.org/2005/Atom" xml:base="https://blog.example.org/" xml:lang="nb">
              <title type="html">Blog &amp;lt;3</title>
              <link rel="alternate" href="/"/>
              <author><name>Feed Author</name></author>
              <entry xml:base="posts/">
                <id>tag:blog.example.org,2026:1</id>
                <title type="text">Hello</title>
                <link href="hello.html"/>
                <link rel="enclosure" type="image/png" href="hello.png"/>
                <updated>2026-11-01T10:00:00+01:00</updated>
                <summary>Short</summary>
                <content type="xhtml"><div xmlns="http://www.w3.org/1999/xhtml"><p>Body <a href="more.html">more</a></p></div></content>
              </entry>
            </feed>
            """);

        Assert.Equal(NewsFeedFormat.Atom, feed.Format);
        Assert.Equal("Blog <3", feed.Title);
        Assert.Equal("https://blog.example.org/", feed.SiteUrl);
        Assert.Equal("nb", feed.Language);
        var entry = Assert.Single(feed.Items);
        Assert.Equal("https://blog.example.org/posts/hello.html", entry.Link);
        Assert.Equal("Feed Author", entry.Author);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero), entry.PublishedAt!.Value.ToUniversalTime());
        Assert.Equal(NewsTextKind.Html, entry.Content!.Kind);
        Assert.Contains("<p", entry.Content.Value, StringComparison.Ordinal);
        Assert.Equal(new Uri("https://blog.example.org/posts/"), entry.BaseUrl);
        var image = Assert.Single(entry.Images);
        Assert.Equal(NewsArticleImageOrigin.Enclosure, image.Origin);
        Assert.Equal("https://blog.example.org/posts/hello.png", image.SourceUrl);
    }

    [Fact]
    public void AC33_AC16_a_malformed_pubDate_costs_the_item_its_date_not_its_place()
    {
        var feed = Parse("<rss version=\"2.0\"><channel><item><title>A</title><pubDate>sometime last week</pubDate></item>"
            + "<item><title>B</title><pubDate>32 Foo 2026 25:61:00 XYZ</pubDate></item></channel></rss>");

        Assert.Equal(2, feed.Items.Count);
        Assert.All(feed.Items, i => Assert.Null(i.PublishedAt));
    }

    [Fact]
    public void AC33_media_content_and_thumbnail_and_enclosures_in_section_6_2_order()
    {
        var feed = Parse("""
            <rss version="2.0" xmlns:media="http://search.yahoo.com/mrss/"><channel><item>
              <title>Pictures</title><link>https://pics.example/1</link>
              <media:thumbnail url="https://pics.example/thumb.jpg" width="120" height="90"/>
              <media:content url="https://pics.example/full.jpg" medium="image" width="1200" height="900"><media:description>A view</media:description></media:content>
              <media:content url="https://pics.example/clip.mp4" medium="video"/>
              <enclosure url="https://pics.example/enc.jpg" type="image/jpeg" length="1"/>
              <enclosure url="https://pics.example/episode.mp3" type="audio/mpeg" length="1"/>
              <media:group><media:content url="https://pics.example/grouped.png" type="image/png"/></media:group>
            </item></channel></rss>
            """);

        var images = Assert.Single(feed.Items).Images;
        Assert.Equal(
            [
                (NewsArticleImageOrigin.Enclosure, "https://pics.example/enc.jpg"),
                (NewsArticleImageOrigin.MediaContent, "https://pics.example/full.jpg"),
                (NewsArticleImageOrigin.MediaContent, "https://pics.example/grouped.png"),
                (NewsArticleImageOrigin.MediaThumbnail, "https://pics.example/thumb.jpg"),
            ],
            images.Select(i => (i.Origin, i.SourceUrl)));
        Assert.Equal("A view", images[1].AltText);
        Assert.Equal((1200, 900), (images[1].Width!.Value, images[1].Height!.Value));
    }

    [Fact]
    public void AC33_html_entities_including_nbsp_parse_through_the_prepass()
    {
        var feed = Parse("<rss version=\"2.0\"><channel><title>A&nbsp;B &mdash; C &copy; &bogus; &amp; &#169; &#x2014;</title>"
            + "<item><title>Caf&eacute; &hellip;</title><description>x &nbsp; y</description></item></channel></rss>");

        // A no-break space is whitespace to the one-line rule, so the title shows a plain space.
        Assert.Equal("A B \u2014 C \u00a9 &bogus; & \u00a9 \u2014", feed.Title);
        Assert.Equal("Café …", feed.Items[0].Title!.Value);
    }

    [Fact]
    public void AC33_a_feed_with_no_guids()
    {
        var feed = Parse("<rss version=\"2.0\"><channel><item><title>A</title><link>https://x.example/a</link></item>"
            + "<item><title>B</title><description>only text</description></item></channel></rss>");

        Assert.All(feed.Items, i => Assert.Null(i.Guid));
        Assert.Equal(2, feed.Items.Count);
    }

    [Fact]
    public void AC33_rss10_rdf_with_dc_and_sy()
    {
        var feed = Parse("""
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns="http://purl.org/rss/1.0/"
                     xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:sy="http://purl.org/rss/1.0/modules/syndication/">
              <channel rdf:about="https://rdf.example/">
                <title>RDF Feed</title><link>https://rdf.example/</link><description>d</description>
                <dc:language>de</dc:language>
                <sy:updatePeriod>hourly</sy:updatePeriod><sy:updateFrequency>2</sy:updateFrequency>
                <items><rdf:Seq><rdf:li rdf:resource="file:///etc/passwd"/></rdf:Seq></items>
              </channel>
              <item rdf:about="https://rdf.example/1">
                <title>One</title><link>https://rdf.example/1</link><description>First</description>
                <dc:creator>Dee</dc:creator><dc:date>2026-10-01T12:00:00Z</dc:date>
              </item>
            </rdf:RDF>
            """);

        Assert.Equal(NewsFeedFormat.RssRdf, feed.Format);
        Assert.Equal("RDF Feed", feed.Title);
        Assert.Equal("de", feed.Language);
        Assert.Equal(30, feed.TtlMinutes);
        var item = Assert.Single(feed.Items);
        Assert.Equal("https://rdf.example/1", item.Guid);
        Assert.Equal("Dee", item.Author);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), item.PublishedAt);
    }

    [Fact]
    public void AC33_rss090()
    {
        var feed = Parse("""
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns="http://my.netscape.com/rdf/simple/0.9/">
              <channel><title>Ninety</title><link>http://ninety.example/</link></channel>
              <item><title>Old item</title><link>http://ninety.example/a</link></item>
            </rdf:RDF>
            """);

        Assert.Equal(NewsFeedFormat.RssRdf, feed.Format);
        Assert.Equal("Old item", Assert.Single(feed.Items).Title!.Value);
    }

    [Fact]
    public void AC33_json_feed_10()
    {
        var feed = Parse("""
            {"version":"https://jsonfeed.org/version/1","title":"JSON One","home_page_url":"https://json.example/",
             "author":{"name":"Feed Author"},
             "items":[{"id":"a1","url":"https://json.example/a1","title":"A1","content_html":"<p>Hi</p>","summary":"S",
                       "image":"/img.png","date_published":"2026-11-10T08:00:00Z","date_modified":"2026-11-11T08:00:00Z"}]}
            """);

        Assert.Equal(NewsFeedFormat.JsonFeed, feed.Format);
        var item = Assert.Single(feed.Items);
        Assert.Equal("a1", item.Guid);
        Assert.Equal("Feed Author", item.Author);
        Assert.Equal(new NewsTextField("<p>Hi</p>", NewsTextKind.Html), item.Content);
        Assert.Equal(new NewsTextField("S", NewsTextKind.Text), item.Summary);
        Assert.Equal(new DateTimeOffset(2026, 11, 11, 8, 0, 0, TimeSpan.Zero), item.UpdatedAt);
        Assert.Equal("https://json.example/img.png", Assert.Single(item.Images).SourceUrl);
        Assert.Equal(NewsFeedParser.JsonSourceContentType, item.SourceContentType);
    }

    [Fact]
    public void AC33_json_feed_11_with_authors_and_attachments()
    {
        var feed = Parse("""
            {"version":"https://jsonfeed.org/version/1.1","title":"JSON 1.1","language":"en","home_page_url":"https://j.example/",
             "items":[{"id":"x","external_url":"https://elsewhere.example/x","authors":[{"name":"First"},{"name":"Second"}],
                       "content_text":"Plain","attachments":[{"url":"https://j.example/a.webp","mime_type":"image/webp"},
                                                             {"url":"https://j.example/a.mp3","mime_type":"audio/mpeg"}]}]}
            """);

        var item = Assert.Single(feed.Items);
        Assert.Equal("en", feed.Language);
        Assert.Equal("First", item.Author);
        Assert.Equal("https://elsewhere.example/x", item.Link);
        var image = Assert.Single(item.Images);
        Assert.Equal(NewsArticleImageOrigin.JsonAttachment, image.Origin);
    }

    [Fact]
    public void AC33_a_numeric_json_id_becomes_a_string()
    {
        var feed = Parse("""{"version":"https://jsonfeed.org/version/1.1","items":[{"id":12345,"title":"n"},{"id":1.5},{"id":true}]}""");
        Assert.Equal(["12345", "1.5", null], feed.Items.Select(i => i.Guid));
    }

    [Fact]
    public void AC33_json_content_text_only_is_a_plain_text_body()
    {
        var feed = Parse("""{"version":"https://jsonfeed.org/version/1","items":[{"id":"t","content_text":"Line one\nLine <b>two</b>"}]}""");
        Assert.Equal(new NewsTextField("Line one\nLine <b>two</b>", NewsTextKind.Text), Assert.Single(feed.Items).Content);
    }

    // ── AC 34: detection ignores Content-Type ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("<!DOCTYPE html><html><body>Not a feed</body></html>")]
    [InlineData("<feed xmlns=\"http://purl.org/atom/ns#\"><title>Atom 0.3</title></feed>")]
    [InlineData("<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><x/></rdf:RDF>")]
    [InlineData("{\"version\":\"https://example.com/not-json-feed\",\"items\":[]}")]
    [InlineData("[1,2,3]")]
    [InlineData("plain text")]
    [InlineData("")]
    public void AC34_a_body_that_is_not_a_feed_is_NotAFeed(string body) => Assert.Equal(NewsFeedParseOutcome.NotAFeed, Outcome(body));

    [Fact]
    public void AC34_the_format_comes_from_the_body_whatever_the_charset_parameter_says()
    {
        var feed = Parse(Rss20ContentEncoded, charset: "utf-16");
        Assert.Equal(NewsFeedFormat.Rss, feed.Format);
    }

    [Theory]
    [InlineData("<rss><channel><item><title>unclosed</channel></rss>")]
    [InlineData("<rss><channel><item><title>a</title></item>")]
    [InlineData("{\"version\":\"https://jsonfeed.org/version/1\",\"items\":[")]
    public void Malformed_documents_are_ParseError(string body) => Assert.Equal(NewsFeedParseOutcome.ParseError, Outcome(body));

    // ── AC 17: XXE and billion laughs ───────────────────────────────────────────────────────────────────

    [Fact]
    public void AC17_an_xxe_reference_is_inert_text_and_reads_no_file()
    {
        var secret = Path.Combine(Path.GetTempPath(), $"odyssey-xxe-{Guid.NewGuid():N}.txt");
        File.WriteAllText(secret, "TOP-SECRET-CONTENT");
        try
        {
            var body = $"""
                <?xml version="1.0"?>
                <!DOCTYPE rss [<!ENTITY xxe SYSTEM "file://{secret}"><!ENTITY % remote SYSTEM "http://127.0.0.1:1/x.dtd"> %remote;]>
                <rss version="2.0"><channel><title>&xxe;</title><item><title>t &xxe;</title></item></channel></rss>
                """;
            var feed = Parse(body);

            Assert.DoesNotContain("TOP-SECRET", feed.Title, StringComparison.Ordinal);
            Assert.Equal("&xxe;", feed.Title);
            Assert.Equal("t &xxe;", feed.Items[0].Title!.Value);
            Assert.DoesNotContain("TOP-SECRET", Encoding.UTF8.GetString(feed.Items[0].Source!), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(secret);
        }
    }

    [Fact]
    public void AC17_billion_laughs_is_never_expanded_and_stays_under_64_MB()
    {
        var entities = new StringBuilder("<!ENTITY lol0 \"lol\">");
        for (var k = 1; k <= 9; k++)
        {
            entities.Append($"<!ENTITY lol{k} \"").Append(string.Concat(Enumerable.Repeat($"&lol{k - 1};", 10))).Append("\">");
        }

        var body = $"<?xml version=\"1.0\"?><!DOCTYPE rss [{entities}]><rss version=\"2.0\"><channel><title>&lol9;</title></channel></rss>";

        var before = GC.GetAllocatedBytesForCurrentThread();
        var feed = Parse(body);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal("&lol9;", feed.Title);
        Assert.True(allocated < 64L * 1024 * 1024, $"Parsing allocated {allocated} bytes.");
    }

    // ── AC 18: XML and JSON nested 100,000 deep ─────────────────────────────────────────────────────────

    [Fact]
    public void AC18_xml_nested_100000_deep_fails_at_the_depth_capped_reader()
    {
        var deep = string.Concat(Enumerable.Repeat("<x>", 100_000)) + string.Concat(Enumerable.Repeat("</x>", 100_000));
        Assert.Equal(NewsFeedParseOutcome.ParseError, Outcome($"<rss version=\"2.0\"><channel><item>{deep}</item></channel></rss>"));
    }

    [Fact]
    public void AC18_xml_inside_the_depth_cap_parses_and_past_it_fails_the_raw_capture_too()
    {
        // The item is at depth 2; its descendants reach depth 64 (allowed) or 65 (refused) inside XElement.ReadFrom.
        static string Item(int extra) => "<item>" + string.Concat(Enumerable.Repeat("<x>", extra)) + string.Concat(Enumerable.Repeat("</x>", extra)) + "</item>";
        Assert.Equal(NewsFeedParseOutcome.Ok, Outcome($"<rss><channel>{Item(DepthCappedXmlReader.MaxDepth - 2)}</channel></rss>"));
        Assert.Equal(NewsFeedParseOutcome.ParseError, Outcome($"<rss><channel>{Item(DepthCappedXmlReader.MaxDepth - 1)}</channel></rss>"));
    }

    [Fact]
    public void AC18_json_nested_100000_deep_fails_at_the_depth_capped_reader()
    {
        var deep = string.Concat(Enumerable.Repeat("[", 100_000)) + string.Concat(Enumerable.Repeat("]", 100_000));
        Assert.Equal(NewsFeedParseOutcome.ParseError, Outcome($"{{\"version\":\"https://jsonfeed.org/version/1\",\"items\":[{{\"id\":\"a\",\"x\":{deep}}}]}}"));
    }

    // ── AC 31: the raw item re-parses on its own ────────────────────────────────────────────────────────

    [Fact]
    public void AC31_a_raw_xml_item_using_root_namespaces_reparses_on_its_own()
    {
        var item = Parse(Rss20ContentEncoded).Items[0];
        var raw = Encoding.UTF8.GetString(item.Source!);

        var reparsed = XElement.Parse(raw);
        Assert.Equal("item", reparsed.Name.LocalName);
        Assert.Equal("Ada Writer", reparsed.Element(XNamespace.Get("http://purl.org/dc/elements/1.1/") + "creator")!.Value);
        Assert.Contains("xmlns:content=\"http://purl.org/rss/1.0/modules/content/\"", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("<?xml", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void A_raw_json_item_is_the_reserialised_object()
    {
        var item = Parse("""{"version":"https://jsonfeed.org/version/1","items":[ { "id" : "a",  "title":"t" } ]}""").Items[0];
        Assert.Equal("""{"id":"a","title":"t"}""", Encoding.UTF8.GetString(item.Source!));
    }

    [Fact]
    public void An_item_over_the_source_cap_keeps_no_source()
    {
        var big = new string('x', NewsFeedParser.MaxSourceBytes);
        var item = Parse($"<rss><channel><item><title>big</title><description>{big}</description></item></channel></rss>").Items[0];

        Assert.True(item.SourceTooLarge);
        Assert.Null(item.Source);
        Assert.Equal(big, item.Summary!.Value);
    }

    // ── AC 32: decoding ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AC32_a_windows_1252_feed_decodes()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var body = Encoding.GetEncoding(1252).GetBytes("<?xml version=\"1.0\" encoding=\"windows-1252\"?><rss><channel><title>Café – €5</title></channel></rss>");

        var feed = NewsFeedParser.Parse(body, FeedUrl).Feed!;
        Assert.Equal("Café – €5", feed.Title);
    }

    [Fact]
    public void Byte_order_marks_decide_the_encoding()
    {
        var utf16 = new UnicodeEncoding(false, true).GetPreamble().Concat(Encoding.Unicode.GetBytes("<rss><channel><title>æøå</title></channel></rss>")).ToArray();
        Assert.Equal("æøå", NewsFeedParser.Parse(utf16, FeedUrl).Feed!.Title);

        var utf8 = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{\"version\":\"https://jsonfeed.org/version/1.1\",\"title\":\"bom\"}")).ToArray();
        Assert.Equal("bom", NewsFeedParser.Parse(utf8, FeedUrl).Feed!.Title);
    }

    // ── Item limits (§3.4 step 4) ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void At_most_500_items_are_read_and_the_200_newest_by_date_are_kept()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var items = string.Concat(Enumerable.Range(0, 600).Select(i =>
            $"<item><guid>g{i}</guid><pubDate>{start.AddHours(i % 2 == 0 ? i : -i):R}</pubDate></item>"));
        var feed = Parse($"<rss><channel>{items}</channel></rss>");

        Assert.Equal(NewsFeedParser.MaxItemsRead, feed.ItemsRead);
        Assert.True(feed.ItemLimitReached);
        Assert.Equal(NewsFeedParser.MaxItemsKept, feed.Items.Count);
        Assert.Equal("g498", feed.Items[0].Guid);
        Assert.True(feed.Items.Zip(feed.Items.Skip(1)).All(p => p.First.PublishedAt >= p.Second.PublishedAt));
    }

    [Fact]
    public void Undated_items_rank_after_dated_ones_in_document_order()
    {
        var feed = Parse("<rss><channel><item><guid>u1</guid></item><item><guid>d</guid><pubDate>Mon, 02 Nov 2026 00:00:00 GMT</pubDate></item><item><guid>u2</guid></item></channel></rss>");
        Assert.Equal(["d", "u1", "u2"], feed.Items.Select(i => i.Guid));
    }

    // ── Dates ────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Mon, 16 Nov 2026 09:30:00 GMT", "2026-11-16T09:30:00Z")]
    [InlineData("16 Nov 2026 09:30 +0100", "2026-11-16T08:30:00Z")]
    [InlineData("Monday, 16 November 2026 09:30:00 EST", "2026-11-16T14:30:00Z")]
    [InlineData("Mon, 16 Nov 26 09:30:00 PDT", "2026-11-16T16:30:00Z")]
    [InlineData("Mon, 16 Nov 2026 09:30:00 +01:00", "2026-11-16T08:30:00Z")]
    [InlineData("2026-11-16T09:30:00Z", "2026-11-16T09:30:00Z")]
    [InlineData("2026-11-16T10:30:00.123+01:00", "2026-11-16T09:30:00.123Z")]
    [InlineData("2026-11-16", "2026-11-16T00:00:00Z")]
    public void Dates_parse_tolerantly(string input, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), NewsDates.Parse(input));

    [Theory]
    [InlineData("yesterday")]
    [InlineData("Mon, 31 Feb 2026 09:30:00 GMT")]
    [InlineData("")]
    [InlineData(null)]
    public void Unparseable_dates_are_null(string? input) => Assert.Null(NewsDates.Parse(input));

    // ── The entity pre-pass ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_prepass_keeps_xml_entities_and_numeric_references_and_escapes_the_rest()
    {
        Assert.Equal("&amp;&lt;&gt;&quot;&apos;&#169;&#xA9;&#xA0;&amp;nope;&amp; x", NewsEntityPrepass.Apply("&amp;&lt;&gt;&quot;&apos;&#169;&#xA9;&nbsp;&nope;& x"));
    }

    [Fact]
    public void The_prepass_leaves_comments_cdata_pis_and_the_doctype_verbatim()
    {
        const string xml = "<!DOCTYPE x [<!ENTITY a \"&b;\">]><!-- &nbsp; --><![CDATA[&nbsp;]]><?pi &nbsp;?><x a=\"&nbsp;\">&nbsp;</x>";
        Assert.Equal("<!DOCTYPE x [<!ENTITY a \"&b;\">]><!-- &nbsp; --><![CDATA[&nbsp;]]><?pi &nbsp;?><x a=\"&#xA0;\">&#xA0;</x>", NewsEntityPrepass.Apply(xml));
    }
}
