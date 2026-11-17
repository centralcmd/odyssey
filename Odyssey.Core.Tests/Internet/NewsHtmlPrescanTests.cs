using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Odyssey.Core.Internet.News.Html;
using Odyssey.Dtos;
using Xunit;

namespace Odyssey.Core.Tests.Internet;

/// <summary>
/// The HTML nesting pre-scan (issue #470 §7.10, decision B, decision 28; AC 18). The property under test is
/// one-sided: the scan must never count shallower than the tree AngleSharp builds. The differential tests generate
/// adversarial tag soup and compare the scan's count with the parsed tree's real depth.
/// </summary>
public class NewsHtmlPrescanTests
{
    private const int Cap = NewsLimits.MaxHtmlDepth;

    private static string Nest(string open, string close, int depth, string inner = "x") =>
        string.Concat(Enumerable.Repeat(open, depth)) + inner + string.Concat(Enumerable.Repeat(close, depth));

    // ── AC 18: the cap boundary ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AC18_html_nested_256_deep_passes_and_parses()
    {
        var html = Nest("<div>", "</div>", Cap);

        Assert.Equal(NewsHtmlRefusal.None, NewsHtmlPrescan.Check(html));
        var text = NewsHtmlText.ToPlainText(html, null);
        Assert.False(text.IsRefused);
        Assert.Equal("x", text.Text);
    }

    [Fact]
    public void AC18_html_nested_257_deep_is_refused_TooComplex()
    {
        var html = Nest("<div>", "</div>", Cap + 1);

        Assert.Equal(NewsHtmlRefusal.TooComplex, NewsHtmlPrescan.Check(html));
        Assert.Equal(NewsHtmlRefusal.TooComplex, NewsHtmlText.ToPlainText(html, null).Refusal);
    }

    [Fact]
    public void AC18_html_nested_100000_deep_is_refused_without_parsing()
    {
        var html = Nest("<div>", "</div>", 100_000);

        Assert.Equal(NewsHtmlRefusal.TooComplex, NewsHtmlPrescan.Check(html));
        // The scan stops as soon as it passes the cap.
        Assert.Equal(Cap + 1, NewsHtmlPrescan.Measure(html, Cap));
    }

    [Fact]
    public void AC18_a_void_element_at_the_cap_counts_as_one_level()
    {
        Assert.Equal(NewsHtmlRefusal.None, NewsHtmlPrescan.Check(Nest("<span>", "</span>", Cap - 1, "<br>")));
        Assert.Equal(NewsHtmlRefusal.TooComplex, NewsHtmlPrescan.Check(Nest("<span>", "</span>", Cap, "<br>")));
    }

    [Fact]
    public void AC18_a_fragment_of_exactly_2_MiB_passes_and_one_byte_more_is_TooLarge()
    {
        var exact = new string('a', NewsHtmlPrescan.MaxFragmentBytes);
        Assert.Equal(NewsHtmlRefusal.None, NewsHtmlPrescan.Check(exact));
        Assert.Equal(NewsHtmlRefusal.TooLarge, NewsHtmlPrescan.Check(exact + "a"));
        Assert.Equal(NewsHtmlRefusal.TooLarge, NewsHtmlText.ToPlainText(exact + "a", null).Refusal);
    }

    [Fact]
    public void The_size_cap_counts_utf8_bytes_not_characters()
    {
        // 'é' is two UTF-8 bytes: half the cap in characters is the whole cap in bytes.
        var html = new string('é', NewsHtmlPrescan.MaxFragmentBytes / 2);
        Assert.Equal(NewsHtmlRefusal.None, NewsHtmlPrescan.Check(html));
        Assert.Equal(NewsHtmlRefusal.TooLarge, NewsHtmlPrescan.Check(html + "a"));
    }

    // ── AC 18: adversarial shapes ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AC18_300_unclosed_b_tags_are_refused()
    {
        var html = string.Concat(Enumerable.Repeat("<b>", 300)) + "x";
        Assert.Equal(NewsHtmlRefusal.TooComplex, NewsHtmlPrescan.Check(html));
    }

    [Fact]
    public void AC18_alternating_b_i_formatting_reconstruction_is_refused_or_within_the_cap()
    {
        var html = string.Concat(Enumerable.Repeat("<b><i>", 150)) + "x";
        Assert.Equal(NewsHtmlRefusal.TooComplex, NewsHtmlPrescan.Check(html));
        AssertNeverShallower(html);
    }

    [Fact]
    public void AC18_formatting_reconstructed_across_paragraphs_is_counted()
    {
        // Each <p> closes the previous one, but the unclosed <b>s stay active and are reconstructed in the next.
        var html = string.Concat(Enumerable.Range(0, 300).Select(i => $"<p><b class=\"c{i}\">x"));
        AssertNeverShallower(html);
        Assert.Equal(NewsHtmlRefusal.TooComplex, NewsHtmlPrescan.Check(html));
    }

    [Fact]
    public void AC18_table_foster_parenting_is_refused_or_within_the_cap()
    {
        var html = string.Concat(Enumerable.Repeat("<table><b>", 200)) + "x";
        AssertNeverShallower(html);
        var html2 = string.Concat(Enumerable.Repeat("<table><tr><td><div>", 70)) + "x";
        AssertNeverShallower(html2);
        Assert.Equal(NewsHtmlRefusal.TooComplex, NewsHtmlPrescan.Check(html2));
    }

    [Fact]
    public void AC18_100000_deep_nesting_inside_a_comment_does_not_count()
    {
        var html = "<p>before<!--" + string.Concat(Enumerable.Repeat("<div>", 100_000)) + "-->after</p>";
        Assert.Equal(1, NewsHtmlPrescan.Measure(html));
        Assert.Equal(NewsHtmlRefusal.None, NewsHtmlPrescan.Check(html));
        Assert.Equal("beforeafter", NewsHtmlText.ToPlainText(html, null).Text);
    }

    [Fact]
    public void AC18_100000_deep_nesting_inside_a_textarea_does_not_count()
    {
        var html = "<textarea>" + string.Concat(Enumerable.Repeat("<div>", 100_000)) + "</textarea>";
        Assert.Equal(1, NewsHtmlPrescan.Measure(html));
        Assert.Equal(NewsHtmlRefusal.None, NewsHtmlPrescan.Check(html));
    }

    [Theory]
    [InlineData("script")]
    [InlineData("style")]
    [InlineData("title")]
    [InlineData("xmp")]
    [InlineData("iframe")]
    [InlineData("noembed")]
    [InlineData("noframes")]
    public void Raw_text_and_RCDATA_content_is_opaque(string element)
    {
        var html = $"<{element}>" + string.Concat(Enumerable.Repeat("<div>", 1_000)) + $"</{element.ToUpperInvariant()}>";
        Assert.Equal(1, NewsHtmlPrescan.Measure(html));
    }

    [Fact]
    public void Plaintext_makes_the_rest_of_the_fragment_text()
    {
        Assert.Equal(1, NewsHtmlPrescan.Measure("<plaintext>" + string.Concat(Enumerable.Repeat("<div>", 1_000))));
    }

    [Fact]
    public void Attribute_values_are_opaque()
    {
        var html = "<a title=\"" + string.Concat(Enumerable.Repeat("<div>", 1_000)) + "\">x</a>";
        Assert.Equal(1, NewsHtmlPrescan.Measure(html));
        var single = "<a title='" + string.Concat(Enumerable.Repeat("<div>", 1_000)) + "'>x</a>";
        Assert.Equal(1, NewsHtmlPrescan.Measure(single));
    }

    [Fact]
    public void A_quote_inside_an_attribute_NAME_is_not_a_value_so_the_tags_after_it_count()
    {
        // The tokenizer reads <a "x> as an attribute named "x — the '>' ends the tag, the divs are live.
        var html = "<a \"x>" + string.Concat(Enumerable.Repeat("<div>", 300)) + "\">";
        AssertNeverShallower(html);
        Assert.Equal(NewsHtmlRefusal.TooComplex, NewsHtmlPrescan.Check(html));
    }

    [Fact]
    public void Unmatched_end_tags_are_no_ops()
    {
        Assert.Equal(2, NewsHtmlPrescan.Measure("</div></span><div><p>x</p></div></b>"));
    }

    [Fact]
    public void Implied_end_tags_close_rather_than_nest()
    {
        var items = "<ul>" + string.Concat(Enumerable.Repeat("<li>item", 1_000)) + "</ul>";
        Assert.Equal(2, NewsHtmlPrescan.Measure(items));
        var paragraphs = string.Concat(Enumerable.Repeat("<p>para", 1_000));
        Assert.Equal(1, NewsHtmlPrescan.Measure(paragraphs));
        var rows = "<table>" + string.Concat(Enumerable.Repeat("<tr><td>a<td>b", 500)) + "</table>";
        Assert.Equal(4, NewsHtmlPrescan.Measure(rows));
        var definitions = "<dl>" + string.Concat(Enumerable.Repeat("<dt>t<dd>d", 500)) + "</dl>";
        Assert.Equal(2, NewsHtmlPrescan.Measure(definitions));
        var options = "<select>" + string.Concat(Enumerable.Repeat("<option>o", 500)) + "</select>";
        Assert.Equal(2, NewsHtmlPrescan.Measure(options));
        foreach (var html in new[] { items, paragraphs, rows, definitions, options })
        {
            AssertNeverShallower(html);
        }
    }

    [Fact]
    public void An_li_inside_a_nested_list_nests()
    {
        var html = string.Concat(Enumerable.Repeat("<li><ul>", 140)) + "x";
        AssertNeverShallower(html);
        Assert.Equal(NewsHtmlRefusal.TooComplex, NewsHtmlPrescan.Check(html));
    }

    [Fact]
    public void A_title_inside_a_select_is_not_opaque()
    {
        // In a select the builder ignores <title>, so </select> after it is live and the b's nest.
        var html = "<select><title></select>" + string.Concat(Enumerable.Repeat("<b>", 300)) + "q</title>";
        AssertNeverShallower(html);
        Assert.Equal(NewsHtmlRefusal.TooComplex, NewsHtmlPrescan.Check(html));
    }

    [Fact]
    public void Raw_text_names_inside_svg_hold_elements()
    {
        var html = "<svg><style>" + string.Concat(Enumerable.Repeat("<g>", 300)) + "</style></svg>";
        AssertNeverShallower(html);
        Assert.Equal(NewsHtmlRefusal.TooComplex, NewsHtmlPrescan.Check(html));
    }

    [Fact]
    public void Breaking_out_of_svg_makes_self_closing_tags_nest()
    {
        var html = "<svg><b>" + string.Concat(Enumerable.Repeat("<path/>", 300));
        AssertNeverShallower(html);
        Assert.Equal(NewsHtmlRefusal.TooComplex, NewsHtmlPrescan.Check(html));
    }

    [Fact]
    public void Self_closing_svg_children_do_not_nest()
    {
        var html = "<svg>" + string.Concat(Enumerable.Repeat("<path d=\"M0 0\"/>", 1_000)) + "</svg>";
        Assert.Equal(2, NewsHtmlPrescan.Measure(html));
        AssertNeverShallower(html);
    }

    [Fact]
    public void Cdata_is_opaque_in_foreign_content_and_a_bogus_comment_in_html()
    {
        var inSvg = "<svg><![CDATA[" + string.Concat(Enumerable.Repeat("<g>", 1_000)) + "]]></svg>";
        Assert.Equal(1, NewsHtmlPrescan.Measure(inSvg));

        // In HTML content <![CDATA[ is a bogus comment that ends at the first '>', so the rest is live markup.
        var inHtml = "<![CDATA[ <b>" + string.Concat(Enumerable.Repeat("<div>", 300)) + "]]>";
        AssertNeverShallower(inHtml);
        Assert.Equal(NewsHtmlRefusal.TooComplex, NewsHtmlPrescan.Check(inHtml));

        // In a select AngleSharp drops <math>, so a CDATA opener after it is a bogus comment again.
        var inSelect = "<select><math><![CDATA[<x>" + string.Concat(Enumerable.Repeat("<button>", 300)) + "]]>";
        AssertNeverShallower(inSelect);
    }

    [Theory]
    [InlineData("<!-->")]
    [InlineData("<!--->")]
    [InlineData("<!-- a --!>")]
    [InlineData("<!-- a --->")]
    public void Comments_end_exactly_where_the_tokenizer_ends_them(string comment)
    {
        var html = comment + string.Concat(Enumerable.Repeat("<div>", 300)) + "-->";
        AssertNeverShallower(html);
        Assert.Equal(NewsHtmlRefusal.TooComplex, NewsHtmlPrescan.Check(html));
    }

    [Fact]
    public void A_kelvin_sign_is_not_lower_cased_into_a_void_element_name()
    {
        // "linK" is not "link": the tokenizer lower-cases ASCII only, so it is an ordinary element that nests.
        var html = string.Concat(Enumerable.Repeat("<linK>", 300));
        AssertNeverShallower(html);
        Assert.Equal(NewsHtmlRefusal.TooComplex, NewsHtmlPrescan.Check(html));
    }

    [Fact]
    public void Annotation_xml_with_an_html_encoding_is_an_integration_point()
    {
        var html = "<math><annotation-xml encoding=\"text/html\">" + string.Concat(Enumerable.Repeat("<div>", 300));
        AssertNeverShallower(html);
        Assert.Equal(NewsHtmlRefusal.TooComplex, NewsHtmlPrescan.Check(html));
    }

    // ── Differential: the scan against AngleSharp's own tree ─────────────────────────────────────────────

    /// <summary>
    /// The scan alone, over feed-shaped markup — nested blocks, lists, tables, definition lists, formatting, links,
    /// images, comments and raw text, with the end tags HTML lets an author omit (<c>p</c>, <c>li</c>, <c>dt</c>,
    /// <c>dd</c>, <c>tr</c>, <c>td</c>, <c>th</c>, <c>option</c>) left out at random — is never shallower than the
    /// tree, and is exact or close. Random tag soup, misnesting included, is the composite property's job below.
    /// </summary>
    [Fact]
    public void Differential_feed_shaped_markup_is_never_counted_shallower_than_the_parsed_tree()
    {
        var random = new Random(470);
        for (var round = 0; round < 2_000; round++)
        {
            var builder = new StringBuilder();
            FeedShaped(random, builder, random.Next(1, 60));
            AssertNeverShallower(builder.ToString());
        }
    }

    private static readonly string[] Blocks = ["div", "p", "section", "article", "blockquote", "figure", "header", "footer", "aside", "pre", "h2", "h3"];

    private static readonly string[] Inline = ["span", "a", "b", "i", "em", "strong", "code", "small", "sub", "sup", "u", "s"];

    private static void FeedShaped(Random random, StringBuilder html, int budget)
    {
        for (var k = 0; k < random.Next(1, 4) && budget > 0; k++, budget--)
        {
            switch (random.Next(9))
            {
                case 0 or 1:
                    var block = Blocks[random.Next(Blocks.Length)];
                    html.Append('<').Append(block).Append(" class=\"x\">");
                    FeedShaped(random, html, budget - 1);
                    if (block != "p" || random.Next(2) == 0)
                    {
                        html.Append("</").Append(block).Append('>');
                    }

                    break;
                case 2:
                    var inline = Inline[random.Next(Inline.Length)];
                    html.Append('<').Append(inline).Append(inline == "a" ? " href=\"/x?a=1&amp;b=2\">" : ">");
                    html.Append("text ");
                    if (random.Next(3) == 0)
                    {
                        FeedShaped(random, html, budget - 1);
                    }

                    html.Append("</").Append(inline).Append('>');
                    break;
                case 3:
                    var list = random.Next(2) == 0 ? "ul" : "ol";
                    html.Append('<').Append(list).Append('>');
                    for (var item = 0; item < random.Next(1, 4); item++)
                    {
                        html.Append("<li>item ");
                        if (random.Next(3) == 0)
                        {
                            FeedShaped(random, html, budget - 2);
                        }

                        if (random.Next(2) == 0)
                        {
                            html.Append("</li>");
                        }
                    }

                    html.Append("</").Append(list).Append('>');
                    break;
                case 4:
                    html.Append("<table>");
                    if (random.Next(2) == 0)
                    {
                        html.Append("<tbody>");
                    }

                    for (var row = 0; row < random.Next(1, 3); row++)
                    {
                        html.Append("<tr>");
                        for (var cell = 0; cell < random.Next(1, 3); cell++)
                        {
                            html.Append(random.Next(4) == 0 ? "<th>" : "<td>").Append("cell ");
                            if (random.Next(3) == 0)
                            {
                                FeedShaped(random, html, budget - 3);
                            }
                        }
                    }

                    html.Append("</table>");
                    break;
                case 5:
                    html.Append("<dl>");
                    for (var term = 0; term < random.Next(1, 3); term++)
                    {
                        html.Append("<dt>term<dd>definition ");
                        if (random.Next(3) == 0)
                        {
                            FeedShaped(random, html, budget - 2);
                        }
                    }

                    html.Append("</dl>");
                    break;
                case 6:
                    html.Append(random.Next(4) switch
                    {
                        0 => "<img src=\"/i.png\" alt=\"a\">",
                        1 => "<br>",
                        2 => "<hr>",
                        _ => "<select><option>one<option>two</select>",
                    });
                    break;
                case 7:
                    html.Append(random.Next(3) switch
                    {
                        0 => "<!-- <div><div> -->",
                        1 => "<script>if (a < b) { x = '<div>'; }</script>",
                        _ => "<style>p > b { color: red }</style>",
                    });
                    break;
                default:
                    html.Append("Some text &amp; more. ");
                    break;
            }
        }
    }

    /// <summary>
    /// The composite property AC 18 rests on, over the full vocabulary: a fragment whose parsed tree is deeper than the
    /// cap is never accepted. The pre-scan refuses it, or — where the scan's model of the tree builder is not exact —
    /// the walk's own depth check does, because AngleSharp's depth limit sits above the cap.
    /// </summary>
    [Fact]
    public void Composite_no_fragment_deeper_than_the_cap_is_ever_accepted()
    {
        var random = new Random(4_700);
        var deep = 0;
        for (var round = 0; round < 1_500; round++)
        {
            var soup = RandomSoup(random, random.Next(5, 60), Vocabulary, cdata: true);
            var html = string.Concat(Enumerable.Repeat(soup, random.Next(10, 120)));
            if (ParsedDepth(html) <= Cap)
            {
                continue;
            }

            deep++;
            Assert.True(NewsHtmlText.ToPlainText(html, null).IsRefused, $"A fragment deeper than {Cap} was accepted: {html[..Math.Min(html.Length, 400)]}");
        }

        Assert.True(deep > 100, $"Only {deep} generated fragments were deeper than the cap: the property was barely exercised.");
    }

    private static readonly string[] Vocabulary =
    [
        "a", "b", "i", "u", "em", "strong", "font", "nobr", "code", "s", "small", "big", "tt", "strike",
        "p", "div", "span", "section", "article", "blockquote", "pre", "h1", "h2", "h3", "address", "center",
        "ul", "ol", "li", "dl", "dt", "dd", "menu", "dir",
        "table", "caption", "colgroup", "col", "thead", "tbody", "tfoot", "tr", "td", "th",
        "select", "option", "optgroup", "button", "form", "fieldset", "label", "input", "textarea", "keygen",
        "ruby", "rb", "rp", "rt", "rtc", "sub", "sup", "var",
        "svg", "math", "g", "path", "foreignObject", "desc", "mi", "mo", "mtext", "mglyph", "annotation-xml",
        "script", "style", "title", "xmp", "iframe", "noembed", "noframes", "noscript", "plaintext", "template",
        "br", "img", "image", "hr", "wbr", "area", "embed", "param", "source", "track", "meta", "link", "base",
        "frame", "frameset", "body", "html", "head", "object", "applet", "marquee", "menuitem", "listing", "details",
        "summary", "dialog", "figure", "main", "nav", "header", "footer", "search", "x-custom",
    ];

    private static string RandomSoup(Random random, int tokens, string[] vocabulary, bool cdata)
    {
        var builder = new StringBuilder();
        for (var k = 0; k < tokens; k++)
        {
            var name = vocabulary[random.Next(vocabulary.Length)];
            if (random.Next(3) == 0)
            {
                name = name.ToUpperInvariant();
            }

            switch (random.Next(12))
            {
                case 0 or 1 or 2 or 3 or 4:
                    builder.Append('<').Append(name).Append(RandomAttributes(random)).Append(random.Next(8) == 0 ? "/>" : ">");
                    break;
                case 5 or 6 or 7:
                    builder.Append("</").Append(name).Append('>');
                    break;
                case 8:
                    builder.Append("text ");
                    break;
                case 9:
                    builder.Append(random.Next(4) switch
                    {
                        0 => "<!--",
                        1 => "-->",
                        2 => "<!-->",
                        _ => "--!>",
                    });
                    break;
                case 10:
                    builder.Append(random.Next(3) switch
                    {
                        0 => cdata ? "<![CDATA[" : "<!x>",
                        1 => "]]>",
                        _ => "<!DOCTYPE x>",
                    });
                    break;
                default:
                    builder.Append(random.Next(4) switch
                    {
                        0 => "<a \"q>",
                        1 => "<?pi>",
                        2 => "</ x>",
                        _ => "<",
                    });
                    break;
            }
        }

        return builder.ToString();
    }

    private static string RandomAttributes(Random random) =>
        random.Next(6) switch
        {
            0 => " encoding=\"text/html\"",
            1 => " color=red",
            2 => $" class=\"c{random.Next(4)}\"",
            3 => " title='<div>'",
            _ => string.Empty,
        };

    private static void AssertNeverShallower(string html)
    {
        var parsed = ParsedDepth(html);
        var scanned = NewsHtmlPrescan.Measure(html);
        Assert.True(scanned >= parsed, $"The scan counted {scanned} but the tree is {parsed} deep for: {html}");
    }

    /// <summary>The deepest element in the fragment AngleSharp builds, an element directly in the fragment at depth 1.</summary>
    internal static int ParsedDepth(string html)
    {
        var parser = new HtmlParser(new HtmlParserOptions { IsScripting = false, MaximumDepth = 1_000_000 });
        var context = parser.ParseDocument(string.Empty).Body!;
        var nodes = parser.ParseFragment(html, context);
        var deepest = 0;
        var stack = new Stack<(INode Node, int Depth)>();
        foreach (var node in nodes)
        {
            stack.Push((node, 1));
        }

        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            if (node is not IElement element)
            {
                continue;
            }

            deepest = Math.Max(deepest, depth);
            foreach (var child in element.ChildNodes)
            {
                stack.Push((child, depth + 1));
            }

            if (element is AngleSharp.Html.Dom.IHtmlTemplateElement template)
            {
                foreach (var child in template.Content.ChildNodes)
                {
                    stack.Push((child, depth + 1));
                }
            }
        }

        return deepest;
    }
}
