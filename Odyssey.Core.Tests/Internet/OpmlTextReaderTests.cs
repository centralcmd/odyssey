using System.Text;
using Odyssey.Core.Internet.News.Opml;
using Odyssey.Core.Internet.News.Parsing;
using Xunit;

namespace Odyssey.Core.Tests.Internet;

/// <summary>
/// The chunked entity rule and token cap of issue #508 §7.5. AC 37 is the differential test: at every chunk size the
/// reader's output equals <see cref="NewsEntityPrepass.Apply"/> over the same input, references and construct
/// delimiters split across every boundary included. The token cap tests are AC 24's reader half.
/// </summary>
public sealed class OpmlTextReaderTests
{
    private static readonly int[] ChunkSizes = [1, 2, 3, 7, 64, 1024, 8192];

    /// <summary>A corpus of hostile and ordinary shapes; each is also concatenated so references fall on every offset.</summary>
    private static readonly string[] Corpus =
    [
        "",
        "plain text with no references at all",
        "<opml><body><outline text=\"Caf&eacute; &amp; Bar &nbsp;&mdash;\"/></body></opml>",
        "&amp;&lt;&gt;&quot;&apos;&#169;&#xA9;&#x1F600;&#123456789;&#;&#x;&#xZZ;",
        "& bare ampersand &; &1abc; &abc &abcdefghijklmnopqrstuvwxyzabcdefghijklmnopq; &unknownentity;",
        "<!DOCTYPE opml [<!ENTITY lol \"lol\"><!ENTITY lol2 \"&lol;&lol;&lol;\">]><opml>&lol2;</opml>",
        "<!DOCTYPE opml SYSTEM \"http://attacker.example/x.dtd\"><opml>&xxe;</opml>",
        "<!DOCTYPE a [ <!-- ' --> ]><opml>&nbsp;&custom;</opml>",
        "<!-- a comment with &nbsp; and &bogus; --><x>&nbsp;</x>",
        "<!-- unterminated comment &nbsp;",
        "<![CDATA[ &nbsp; inside cdata ]]>&nbsp;<![CDATA[ unterminated &nbsp;",
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><?pi &nbsp; ?>&nbsp;",
        "<!DOCTYPE x [ <!ENTITY a '&nbsp;'> ] >&nbsp;<!DOCTYPE unterminated [ &nbsp;",
        "<!-->&nbsp;-->&nbsp;<?>&nbsp;?>",
        "&NotANumber;&notin;&nvlt;&ThickSpace;&amp",
        "<a b='&nbsp;' c=\"&lt;&#x26;\">&#38;&#x00026;</a>",
    ];

    public static TheoryData<int> Sizes()
    {
        var data = new TheoryData<int>();
        foreach (var size in ChunkSizes)
        {
            data.Add(size);
        }

        return data;
    }

    private static string ReadAll(string input, int chunkSize)
    {
        using var reader = new OpmlTextReader(new StringReader(input), chunkSize);
        return reader.ReadToEnd();
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void AC37_the_output_equals_the_prepass_at_every_chunk_size(int chunkSize)
    {
        foreach (var input in Corpus)
        {
            Assert.Equal(NewsEntityPrepass.Apply(input), ReadAll(input, chunkSize));
        }

        // Every reference and delimiter at every offset relative to every chunk boundary.
        var joined = string.Join("x", Corpus);
        for (var shift = 0; shift < 9; shift++)
        {
            var shifted = new string('y', shift) + joined;
            Assert.Equal(NewsEntityPrepass.Apply(shifted), ReadAll(shifted, chunkSize));
        }
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void AC37_a_random_mix_of_fragments_matches_the_prepass(int chunkSize)
    {
        string[] fragments = ["&", "#", "x", "1", "9", ";", "<", ">", "!", "-", "[", "]", "?", "'", "\"", "amp", "nbsp", "DOCTYPE", "CDATA", "a", " "];
        var random = new Random(508 + chunkSize);
        for (var round = 0; round < 40; round++)
        {
            var builder = new StringBuilder();
            for (var i = 0; i < 400; i++)
            {
                builder.Append(fragments[random.Next(fragments.Length)]);
            }

            var input = builder.ToString();
            Assert.Equal(NewsEntityPrepass.Apply(input), ReadAll(input, chunkSize));
        }
    }

    [Fact]
    public void A_run_at_the_cap_passes_and_one_past_it_is_refused()
    {
        Assert.Equal(1 + 100, ReadAll("<" + new string('a', 100), 7).Length);
        using var atCap = new OpmlTextReader(new StringReader("<" + new string('a', 100)), 7, maxTokenLength: 100);
        Assert.Equal(101, atCap.ReadToEnd().Length);

        using var over = new OpmlTextReader(new StringReader("<" + new string('a', 101)), 7, maxTokenLength: 100);
        Assert.Throws<OpmlTokenLimitException>(() => over.ReadToEnd());
    }

    [Fact]
    public void AC24_The_counter_resets_only_on_a_less_than_never_on_quotes_or_greater_than()
    {
        // R2-1: '>' and quotes every 60 characters, but no '<' for 300.
        var hostile = "<a b=\"" + string.Concat(Enumerable.Repeat(new string('x', 59) + ">", 5)) + "\"/>";
        using var reader = new OpmlTextReader(new StringReader(hostile), 64, maxTokenLength: 200);
        Assert.Throws<OpmlTokenLimitException>(() => reader.ReadToEnd());
    }

    [Theory]
    [InlineData("<!--", "-->")]
    [InlineData("<![CDATA[", "]]>")]
    [InlineData("<?pi ", "?>")]
    [InlineData("<!DOCTYPE a [", "]>")]
    public void AC24_A_construct_where_less_than_is_legal_is_capped_as_a_whole(string open, string close)
    {
        // Each 50-character piece starts with '<', so the run counter never trips; the construct's total does.
        var body = string.Concat(Enumerable.Repeat("<" + new string('z', 49), 10));
        using var reader = new OpmlTextReader(new StringReader(open + body + close), 16, maxTokenLength: 300);
        Assert.Throws<OpmlTokenLimitException>(() => reader.ReadToEnd());

        using var small = new OpmlTextReader(new StringReader(open + body[..200] + close + "<x/>"), 16, maxTokenLength: 300);
        Assert.EndsWith("<x/>", small.ReadToEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void Implementation_note_1_A_desynchronised_doctype_cannot_hide_a_long_run()
    {
        // The pre-pass's quote tracking believes this DOCTYPE never ends; the run counter does not care.
        var input = "<!DOCTYPE a [ <!-- ' --> ]><opml>" + new string('q', 500) + "</opml>";
        using var reader = new OpmlTextReader(new StringReader(input), 64, maxTokenLength: 300);
        Assert.Throws<OpmlTokenLimitException>(() => reader.ReadToEnd());
    }

    [Fact]
    public void Reading_is_bounded_by_the_chunk_not_the_input()
    {
        // 4 MB of short elements through a 1 KiB chunk: the internal buffers never grow past a few chunks.
        var input = string.Concat(Enumerable.Repeat("<a>&nbsp;</a>", 300_000));
        using var reader = new OpmlTextReader(new StringReader(input), 1024);
        var buffer = new char[1000];
        long total = 0;
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
        }

        Assert.Equal(NewsEntityPrepass.Apply(input).Length, total);
    }
}
