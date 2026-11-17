using Odyssey.Core.Internet.News.Html;
using Odyssey.Dtos;
using Xunit;

namespace Odyssey.Core.Tests.Internet;

/// <summary>
/// HTML → plain text (issue #470 §8.3; AC 24): the block, list, table and link rules, the dropped elements, the image
/// references, entity decoding, the sanitiser, paragraph normalisation and truncation.
/// </summary>
public class NewsHtmlTextTests
{
    private static readonly Uri Base = new("https://example.com/posts/1/");

    private static string Text(string html) => NewsHtmlText.ToPlainText(html, Base).Text;

    [Fact]
    public void AC24_blocks_become_paragraphs_and_br_a_line_break()
    {
        Assert.Equal("First para.\n\nSecond\nline.", Text("<p>First   para.</p><div>Second<br>line.</div>"));
    }

    [Fact]
    public void AC24_list_items_are_bulleted_and_table_cells_separated()
    {
        Assert.Equal("• one\n• two", Text("<ul><li>one</li><li>two</ul>"));
        Assert.Equal("a | b\nc | d", Text("<table><tr><td>a<td>b<tr><th>c</th><td>d</td></table>"));
    }

    [Fact]
    public void AC24_a_link_shows_its_url_only_when_it_is_absolute_http_and_differs_from_its_text()
    {
        Assert.Equal("Read this (https://example.com/a)", Text("<a href=\"https://example.com/a\">Read this</a>"));
        Assert.Equal("https://example.com/a", Text("<a href=\"https://example.com/a\">https://example.com/a</a>"));
        Assert.Equal("relative (https://example.com/posts/1/x)", Text("<a href=\"x\">relative</a>"));
    }

    [Theory]
    [InlineData("<a href=\"javascript:alert(1)\">click</a>")]
    [InlineData("<a href=\"data:text/html,&lt;script&gt;\">click</a>")]
    [InlineData("<a href=\"JAVASCRIPT:alert(1)\" onclick=\"alert(2)\">click</a>")]
    [InlineData("<a href=\"vbscript:x\">click</a>")]
    public void AC24_a_javascript_or_data_link_keeps_its_text_only(string html) => Assert.Equal("click", Text(html));

    [Fact]
    public void AC24_script_style_and_other_dropped_elements_leave_no_text()
    {
        var html = "<p>kept</p><script>alert('x')</script><style>p{}</style><noscript>ns</noscript><template>t</template>"
            + "<iframe src=\"https://evil.example\">if</iframe><object>o</object><embed src=x><svg><text>s</text></svg>"
            + "<math><mi>m</mi></math><form><input value=f>form text</form><p onmouseover=\"alert(1)\">also kept</p>";
        Assert.Equal("kept\n\nalso kept", Text(html));
    }

    [Fact]
    public void AC24_no_markup_survives_into_the_text()
    {
        var text = Text("<p>&lt;script&gt;alert(1)&lt;/script&gt;</p><img src=x onerror=alert(1)>");
        Assert.Equal("<script>alert(1)</script>", text); // escaped markup is just text, and stays text
    }

    [Fact]
    public void AC24_entities_decode_and_the_sanitiser_runs()
    {
        Assert.Equal("café — a b", Text("caf&eacute; &mdash; a&nbsp;b&#x200B;"));
        Assert.Equal("ab", Text("a‮b"));
    }

    [Fact]
    public void Images_produce_no_text_and_are_listed_with_their_alt_text()
    {
        var result = NewsHtmlText.ToPlainText("<p>x<img src=\"/a.png\" alt=\" An  image \" width=\"640\" height=\"bad\"><img src=\"javascript:x\"><img src=\"https://cdn.example/b.jpg\"></p>", Base);

        Assert.Equal("x", result.Text);
        Assert.Collection(
            result.Images,
            first =>
            {
                Assert.Equal("https://example.com/a.png", first.SourceUrl);
                Assert.Equal("An image", first.AltText);
                Assert.Equal(640, first.Width);
                Assert.Null(first.Height);
            },
            second => Assert.Equal("https://cdn.example/b.jpg", second.SourceUrl));
    }

    [Fact]
    public void Paragraph_runs_collapse_to_one_blank_line()
    {
        Assert.Equal("a\n\nb", Text("<p>a</p><p></p><p> </p><div><div>b</div></div>"));
        Assert.Equal("a\n\nb", NewsHtmlText.NormalizeParagraphs("  a  \n\n\n\n   b  "));
    }

    [Fact]
    public void Pre_keeps_its_line_breaks()
    {
        Assert.Equal("line one\nline two", Text("<pre>line one\nline two</pre>"));
    }

    [Fact]
    public void A_long_body_is_cut_on_a_paragraph_boundary_within_the_limit_with_the_marker()
    {
        var paragraph = new string('w', 100_000);
        var result = NewsHtmlText.ToPlainText($"<p>{paragraph}</p><p>{paragraph}</p><p>{paragraph}</p>", null);

        Assert.True(result.Truncated);
        Assert.EndsWith(NewsHtmlText.TruncationMarker, result.Text);
        Assert.True(result.Text.Length <= NewsLimits.MaxBodyLength);
        Assert.Equal(paragraph + "\n\n" + paragraph + NewsHtmlText.TruncationMarker, result.Text);
    }

    [Fact]
    public void The_walk_stops_at_its_node_cap_and_marks_the_text_truncated()
    {
        var html = string.Concat(Enumerable.Repeat("<span>x</span>", NewsHtmlText.MaxNodes));
        var result = NewsHtmlText.ToPlainText(html, null);

        Assert.True(result.Truncated);
        Assert.False(result.IsRefused);
    }

    [Fact]
    public void ToLine_flattens_an_html_title()
    {
        Assert.Equal("Breaking: a & b", NewsHtmlText.ToLine("<b>Breaking</b>:<br> a &amp; b", null).Text);
    }

    [Fact]
    public void A_refused_fragment_produces_no_text()
    {
        var deep = string.Concat(Enumerable.Repeat("<div>", 300));
        var result = NewsHtmlText.ToPlainText(deep + "secret", null);
        Assert.Equal(NewsHtmlRefusal.TooComplex, result.Refusal);
        Assert.Empty(result.Text);
        Assert.Empty(result.Images);
    }
}
