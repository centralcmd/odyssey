using System.Diagnostics;
using System.Globalization;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Odyssey.Core.Internet.News.Content;
using Odyssey.Dtos;

namespace Odyssey.Core.Internet.News.Html;

/// <summary>An inline <c>&lt;img&gt;</c> found while extracting text (§8.3 step 4): its sanitised source and its alt text.</summary>
public sealed record NewsHtmlImage(string SourceUrl, string? AltText, int? Width, int? Height);

/// <summary>
/// The result of turning one HTML fragment into plain text. A refused fragment (<see cref="Refusal"/> set) produced no
/// text and was never parsed (or, for the walk's own depth check, produced nothing usable).
/// </summary>
public sealed record NewsHtmlTextResult(NewsHtmlRefusal Refusal, string Text, IReadOnlyList<NewsHtmlImage> Images, bool Truncated)
{
    public bool IsRefused => Refusal != NewsHtmlRefusal.None;

    public static NewsHtmlTextResult Refused(NewsHtmlRefusal refusal) => new(refusal, string.Empty, [], false);
}

/// <summary>
/// HTML → plain text (issue #470 §8.3). The fragment passes the nesting pre-scan and the 2 MiB cap
/// (<see cref="NewsHtmlPrescan"/>) before AngleSharp sees it; AngleSharp then parses it with no loader, no scripting
/// and no CSS, and an iterative walk — capped at <see cref="MaxNodes"/> nodes, depth
/// <see cref="NewsLimits.MaxHtmlDepth"/> and a deadline — produces the text. Nothing here ever serialises the parsed
/// tree back to HTML: the output is plain text and image references, and no markup survives.
/// </summary>
public static class NewsHtmlText
{
    /// <summary>The walk's node cap (§7.10): past it the walk stops and the text is marked truncated.</summary>
    public const int MaxNodes = 100_000;

    /// <summary>The walk's default deadline (§7.10).</summary>
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(2);

    /// <summary>The marker a body cut at <see cref="NewsLimits.MaxBodyLength"/> ends with, counted inside the limit.</summary>
    public const string TruncationMarker = "\n\n[…]";

    private const int MaxImageDimensionAttribute = 100_000;

    private static readonly HashSet<string> DroppedElements = new(StringComparer.Ordinal)
    {
        "script", "style", "noscript", "template", "iframe", "object", "embed", "svg", "math", "form",
        // Fallback content of elements that are never rendered here; their raw-text children would read as markup.
        "noembed", "noframes",
    };

    private static readonly HashSet<string> BlockElements = new(StringComparer.Ordinal)
    {
        "address", "article", "aside", "blockquote", "caption", "center", "dd", "details", "dialog", "dir", "div",
        "dl", "dt", "fieldset", "figcaption", "figure", "footer", "h1", "h2", "h3", "h4", "h5", "h6", "header",
        "hgroup", "hr", "legend", "main", "menu", "nav", "ol", "p", "pre", "search", "section", "summary", "table",
        "ul",
    };

    /// <summary>
    /// Plain text for a body or summary field: paragraphs separated by one blank line, truncated on a paragraph
    /// boundary to <see cref="NewsLimits.MaxBodyLength"/> with <see cref="TruncationMarker"/>.
    /// </summary>
    /// <param name="html">The fragment, already through the entity pre-pass where it came from XML.</param>
    /// <param name="baseUrl">What relative <c>href</c> and <c>src</c> values resolve against.</param>
    /// <param name="deadline">The walk's time budget; <see cref="DefaultDeadline"/> when null.</param>
    /// <param name="truncate">
    /// False returns the whole text, for the change hash, which covers the whole body (§8.8); the caller truncates
    /// what it stores.
    /// </param>
    public static NewsHtmlTextResult ToPlainText(string html, Uri? baseUrl, TimeSpan? deadline = null, bool truncate = true)
    {
        var walked = Walk(html, baseUrl, deadline ?? DefaultDeadline, collectImages: true);
        if (walked.IsRefused)
        {
            return walked;
        }

        var text = NormalizeParagraphs(NewsText.Clean(walked.Text));
        if (!truncate)
        {
            return walked with { Text = text };
        }

        var (truncated, cut) = Truncate(text, NewsLimits.MaxBodyLength);
        return walked with { Text = truncated, Truncated = walked.Truncated || cut };
    }

    /// <summary>Plain text on one line, for an HTML-typed title or author. Images are not collected.</summary>
    public static NewsHtmlTextResult ToLine(string html, Uri? baseUrl, TimeSpan? deadline = null)
    {
        var walked = Walk(html, baseUrl, deadline ?? DefaultDeadline, collectImages: false);
        return walked.IsRefused ? walked : walked with { Text = NewsText.CleanLine(walked.Text) };
    }

    /// <summary>
    /// Paragraph normalisation (§8.3 step 6): whitespace inside a line collapses to one space, lines are trimmed,
    /// paragraphs are separated by exactly one blank line, and the text is trimmed. Line ends are LF.
    /// </summary>
    public static string NormalizeParagraphs(string text)
    {
        var builder = new StringBuilder(text.Length);
        var newlines = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = NewsText.CollapseToLine(rawLine);
            if (line.Length == 0)
            {
                newlines++;
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(newlines >= 2 ? "\n\n" : "\n");
            }

            builder.Append(line);
            newlines = 1;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Cuts text to at most <paramref name="maxCodePoints"/>, the marker included, on the last paragraph boundary that
    /// fits; a single paragraph longer than that is cut at a word boundary instead.
    /// </summary>
    public static (string Text, bool Truncated) Truncate(string text, int maxCodePoints)
    {
        if (NewsText.CodePointLength(text) <= maxCodePoints)
        {
            return (text, false);
        }

        var budget = maxCodePoints - NewsText.CodePointLength(TruncationMarker);
        var head = NewsText.TakeCodePoints(text, budget);
        var boundary = head.LastIndexOf("\n\n", StringComparison.Ordinal);
        if (boundary <= 0 && head.Length < text.Length && text[head.Length] == '\n' && text.AsSpan(head.Length).StartsWith("\n\n"))
        {
            boundary = head.Length;
        }

        var kept = boundary > 0 ? head[..boundary] : NewsText.Excerpt(head, budget);
        return (kept.TrimEnd() + TruncationMarker, true);
    }

    private static NewsHtmlTextResult Walk(string html, Uri? baseUrl, TimeSpan deadline, bool collectImages)
    {
        var refusal = NewsHtmlPrescan.Check(html);
        if (refusal != NewsHtmlRefusal.None)
        {
            return NewsHtmlTextResult.Refused(refusal);
        }

        var parser = new HtmlParser(new HtmlParserOptions
        {
            IsScripting = false,
            IsNotSupportingFrames = true,
            IsSupportingProcessingInstructions = false,
            IsKeepingSourceReferences = false,
            DisableElementPositionTracking = true,
            SkipComments = true,
            SkipProcessingInstructions = true,
            // AngleSharp's own tree cap, a third line behind the pre-scan and the walk's check: set just above the
            // walk's cap (plus the html/body around the context) so an over-deep tree is still seen and refused.
            MaximumDepth = NewsLimits.MaxHtmlDepth + 8,
        });

        var context = parser.ParseDocument(string.Empty).Body!;
        var nodes = parser.ParseFragment(html, context);

        var output = new StringBuilder();
        var images = collectImages ? new List<NewsHtmlImage>() : null;
        var clock = Stopwatch.StartNew();
        var visited = 0;
        var truncated = false;
        var preDepth = 0;

        var frames = new Stack<Frame>();
        for (var k = nodes.Length - 1; k >= 0; k--)
        {
            frames.Push(new Frame(nodes[k], 1, false, 0));
        }

        while (frames.Count > 0)
        {
            var frame = frames.Pop();
            if (frame.Exiting)
            {
                Exit(frame.Node, frame.Start, output, baseUrl, ref preDepth);
                continue;
            }

            if (++visited > MaxNodes || ((visited & 0xFF) == 0 && clock.Elapsed > deadline))
            {
                truncated = true;
                break;
            }

            switch (frame.Node)
            {
                case IText textNode:
                    AppendText(output, textNode.Data, preDepth > 0);
                    continue;
                case IElement element:
                    if (frame.Depth > NewsLimits.MaxHtmlDepth)
                    {
                        return NewsHtmlTextResult.Refused(NewsHtmlRefusal.TooComplex);
                    }

                    var name = element.LocalName;
                    if (DroppedElements.Contains(name))
                    {
                        continue;
                    }

                    Enter(element, name, output, images, baseUrl, ref preDepth);
                    frames.Push(frame with { Exiting = true, Start = output.Length });
                    for (var child = element.LastChild; child is not null; child = child.PreviousSibling)
                    {
                        frames.Push(new Frame(child, frame.Depth + 1, false, 0));
                    }

                    continue;
            }
        }

        return new NewsHtmlTextResult(NewsHtmlRefusal.None, output.ToString(), (IReadOnlyList<NewsHtmlImage>?)images ?? [], truncated);
    }

    private readonly record struct Frame(INode Node, int Depth, bool Exiting, int Start);

    private static void Enter(IElement element, string name, StringBuilder output, List<NewsHtmlImage>? images, Uri? baseUrl, ref int preDepth)
    {
        if (BlockElements.Contains(name))
        {
            ParagraphBreak(output);
        }

        switch (name)
        {
            case "br":
                output.Append('\n');
                break;
            case "li":
                LineBreak(output);
                output.Append("• ");
                break;
            case "tr":
                LineBreak(output);
                break;
            case "td" or "th":
                if (element.PreviousElementSibling?.LocalName is "td" or "th")
                {
                    output.Append(" | ");
                }

                break;
            case "pre":
                preDepth++;
                break;
            case "img":
                if (images is not null && NewsUrl.Sanitize(element.GetAttribute("src"), baseUrl) is { } source)
                {
                    var alt = NewsText.CleanLine(element.GetAttribute("alt"));
                    images.Add(new NewsHtmlImage(
                        source,
                        alt.Length == 0 ? null : NewsText.Clamp(alt, NewsLimits.MaxImageAltTextLength),
                        Dimension(element.GetAttribute("width")),
                        Dimension(element.GetAttribute("height"))));
                }

                break;
        }
    }

    private static void Exit(INode node, int start, StringBuilder output, Uri? baseUrl, ref int preDepth)
    {
        if (node is not IElement element)
        {
            return;
        }

        var name = element.LocalName;
        if (name == "pre")
        {
            preDepth--;
        }

        // The anchor's own text runs from where its exit frame was pushed, so the URL can be compared with it.
        if (name == "a" && NewsUrl.Sanitize(element.GetAttribute("href"), baseUrl) is { } url)
        {
            var text = NewsText.CollapseToLine(output.ToString(start, output.Length - start));
            if (text.Length > 0 && !string.Equals(text, url, StringComparison.Ordinal)
                && !string.Equals(text, url.TrimEnd('/'), StringComparison.Ordinal))
            {
                output.Append(" (").Append(url).Append(')');
            }
            else if (text.Length == 0)
            {
                // An empty anchor (an icon link) still names its destination.
                output.Append(url);
            }
        }

        if (BlockElements.Contains(name))
        {
            ParagraphBreak(output);
        }
    }

    private static void AppendText(StringBuilder output, string data, bool preserveLines)
    {
        foreach (var c in data)
        {
            if (c == '\n' && preserveLines)
            {
                output.Append('\n');
            }
            else
            {
                output.Append(char.IsWhiteSpace(c) ? ' ' : c);
            }
        }
    }

    private static void ParagraphBreak(StringBuilder output)
    {
        if (output.Length > 0)
        {
            output.Append("\n\n");
        }
    }

    private static void LineBreak(StringBuilder output)
    {
        if (output.Length > 0)
        {
            output.Append('\n');
        }
    }

    private static int? Dimension(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
        && number is > 0 and <= MaxImageDimensionAttribute
            ? number
            : null;
}
