using Odyssey.Core.Internet.News;
using Odyssey.Core.Internet.News.Content;
using Odyssey.Dtos;
using Xunit;

namespace Odyssey.Core.Tests.Internet;

/// <summary>
/// The text sanitiser, the clamps, the URL sanitiser and the URL keys (issue #470 §8.2, §8.7; AC 24, AC 32).
/// </summary>
public class NewsTextTests
{
    private static string Chars(params int[] codePoints) => string.Concat(codePoints.Select(char.ConvertFromUtf32));

    // ── NewsText.Clean (§8.7) ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Clean_removes_C0_and_C1_controls_but_keeps_newline_and_maps_tab_to_space()
    {
        var input = "a" + Chars(0x00, 0x07, 0x1B, 0x7F, 0x85, 0x9F) + "b\tc\nd\re";
        Assert.Equal("ab c\nde", NewsText.Clean(input));
    }

    [Theory]
    [InlineData(0x200B)]
    [InlineData(0x200F)]
    [InlineData(0x202A)]
    [InlineData(0x202E)]
    [InlineData(0x2060)]
    [InlineData(0x2064)]
    [InlineData(0x2066)]
    [InlineData(0x2069)]
    [InlineData(0x061C)]
    [InlineData(0xFEFF)]
    [InlineData(0xFFF9)]
    [InlineData(0xFFFB)]
    [InlineData(0xE0001)]
    [InlineData(0xE007F)]
    public void Clean_removes_invisible_format_bidi_and_tag_characters(int codePoint) =>
        Assert.Equal("ab", NewsText.Clean("a" + Chars(codePoint) + "b"));

    [Fact]
    public void Clean_maps_line_and_paragraph_separators_to_newline() =>
        Assert.Equal("a\nb\nc", NewsText.Clean("a" + Chars(0x2028) + "b" + Chars(0x2029) + "c"));

    [Fact]
    public void Clean_removes_unpaired_surrogates_and_keeps_pairs()
    {
        var input = "a" + (char)0xD800 + "b" + (char)0xDC00 + "c" + Chars(0x1F600);
        Assert.Equal("abc" + Chars(0x1F600), NewsText.Clean(input));
    }

    [Fact]
    public void Clean_normalises_to_NFC()
    {
        var decomposed = "e" + Chars(0x0301); // e + combining acute
        Assert.Equal(Chars(0x00E9), NewsText.Clean(decomposed));
    }

    // ── Clamping (§8.7) ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Clamp_cuts_on_a_code_point_boundary_with_an_ellipsis_within_the_bound()
    {
        var emoji = Chars(0x1F600);
        var text = string.Concat(Enumerable.Repeat(emoji, 10));
        var clamped = NewsText.Clamp(text, 5);

        Assert.Equal(5, NewsText.CodePointLength(clamped));
        Assert.EndsWith(NewsText.Ellipsis, clamped);
        Assert.Equal(string.Concat(Enumerable.Repeat(emoji, 4)) + NewsText.Ellipsis, clamped);
        Assert.Equal("short", NewsText.Clamp("short", 5));
    }

    [Fact]
    public void Excerpt_is_cut_at_a_word_boundary_and_is_one_line()
    {
        var excerpt = NewsText.Excerpt("one two\n\nthree four five", 14);
        Assert.Equal("one two three", excerpt);
        Assert.Equal("abcdefghij", NewsText.Excerpt("abcdefghijklmnop", 10)); // a single long word is cut
        Assert.True(NewsText.CodePointLength(NewsText.Excerpt(new string('w', 5000), NewsLimits.MaxExcerptLength)) <= NewsLimits.MaxExcerptLength);
    }

    // ── URL sanitiser (§8.7, AC 24) ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("vbscript:msgbox")]
    [InlineData("mailto:a@example.com")]
    [InlineData("ftp://example.com/file")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://user:pass@example.com/")]
    [InlineData("https://user@example.com/")]
    [InlineData("//example.com/protocol-relative-without-base")]
    [InlineData("https://exa mple.com/")]
    [InlineData("")]
    public void AC24_a_url_that_is_not_plain_http_or_https_is_null(string url) => Assert.Null(NewsUrl.Sanitize(url));

    [Fact]
    public void A_url_over_2048_characters_is_null() =>
        Assert.Null(NewsUrl.Sanitize("https://example.com/" + new string('a', NewsLimits.MaxUrlLength)));

    [Fact]
    public void A_relative_url_resolves_against_the_base()
    {
        var baseUrl = new Uri("https://example.com/feed/item/");
        Assert.Equal("https://example.com/feed/item/img.png", NewsUrl.Sanitize("img.png", baseUrl));
        Assert.Equal("https://example.com/root.png", NewsUrl.Sanitize("/root.png", baseUrl));
        Assert.Equal("https://cdn.example.net/x.png", NewsUrl.Sanitize("//cdn.example.net/x.png", baseUrl));
        Assert.Null(NewsUrl.Sanitize("javascript:alert(1)", baseUrl));
    }

    // ── URL keys (§8.2, AC 32) ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void UrlKey_lowercases_drops_default_port_fragment_and_tracking_parameters_keeping_order()
    {
        var key = NewsUrlKey.UrlKey("  HTTPS://Example.COM:443/Path/To?b=2&utm_source=x&a=1&fbclid=y&gclid=z&mc_cid=1&mc_eid=2&_hsenc=3#frag  ");
        Assert.Equal("https://example.com/Path/To?b=2&a=1", key);
        Assert.Equal("http://example.com:8080/", NewsUrlKey.UrlKey("http://example.com:8080"));
        Assert.Equal("https://example.com/", NewsUrlKey.UrlKey("https://example.com/?utm_campaign=1"));
    }

    [Fact]
    public void LinkKey_drops_the_scheme_and_a_leading_www_so_an_http_to_https_move_keeps_it()
    {
        Assert.Equal(NewsUrlKey.LinkKey("http://www.example.com/a"), NewsUrlKey.LinkKey("https://example.com/a"));
        Assert.Equal("example.com/a?x=1", NewsUrlKey.LinkKey("https://www.example.com/a?x=1"));
    }

    [Fact]
    public void AC32_an_idn_host_maps_to_punycode_and_nfc_and_nfd_spellings_agree()
    {
        var nfc = "https://bücher.example/" + Chars(0x00E9);
        var nfd = "https://bu" + Chars(0x0308) + "cher.example/" + "e" + Chars(0x0301);

        Assert.Equal("https://xn--bcher-kva.example/%C3%A9", NewsUrlKey.UrlKey(nfc));
        Assert.Equal(NewsUrlKey.UrlKey(NewsText.Clean(nfc)), NewsUrlKey.UrlKey(NewsText.Clean(nfd)));
    }

    [Theory]
    [InlineData("ftp://example.com/")]
    [InlineData("https://user:pw@example.com/")]
    [InlineData("not a url")]
    [InlineData(null)]
    public void UrlKey_is_null_for_anything_but_a_plain_web_url(string? url)
    {
        Assert.Null(NewsUrlKey.UrlKey(url));
        Assert.Null(NewsUrlKey.LinkKey(url));
    }

    [Fact]
    public void Key_hashes_are_32_byte_sha256()
    {
        var hash = NewsUrlKey.Hash("example.com/a");
        Assert.Equal(NewsLimits.HashLength, hash.Length);
        Assert.Equal(hash, NewsUrlKey.Hash("example.com/a"));
    }

    [Fact]
    public void Composite_hash_frames_its_fields()
    {
        static byte[] Hash(Action<NewsHashing.Composite> fill)
        {
            using var composite = new NewsHashing.Composite();
            fill(composite);
            return composite.Finish();
        }

        Assert.NotEqual(Hash(c => c.Text("ab").Text("c")), Hash(c => c.Text("a").Text("bc")));
        Assert.NotEqual(Hash(c => c.Text(null)), Hash(c => c.Text(string.Empty)));
        Assert.NotEqual(Hash(c => c.Text("x")), Hash(c => c.Raw("x")));
        Assert.Equal(Hash(c => c.Text("x").Raw("y")), Hash(c => c.Text("x").Raw("y")));
    }
}
