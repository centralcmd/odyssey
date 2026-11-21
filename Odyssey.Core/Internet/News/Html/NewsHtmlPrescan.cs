using System.Text;
using Odyssey.Dtos;

namespace Odyssey.Core.Internet.News.Html;

/// <summary>Why an HTML fragment was refused before parsing (issue #470 §7.10).</summary>
public enum NewsHtmlRefusal
{
    None = 0,

    /// <summary>The fragment is over <see cref="NewsHtmlPrescan.MaxFragmentBytes"/>.</summary>
    TooLarge = 1,

    /// <summary>Its nesting would exceed <see cref="NewsLimits.MaxHtmlDepth"/>.</summary>
    TooComplex = 2,
}

/// <summary>
/// The linear nesting pre-scan (issue #470 §7.10, decision B and decision 28). It runs before AngleSharp parses ANY HTML
/// fragment and refuses one that is over 2 MiB or whose nesting would exceed <see cref="NewsLimits.MaxHtmlDepth"/>.
///
/// <para>
/// <strong>The one rule it must never break: it never counts shallower than the tree AngleSharp builds.</strong>
/// Over-counting refuses a fragment early, which is safe; under-counting lets one past the cap. So the scan is a
/// conservative model of the HTML tokenizer and tree builder, built around that asymmetry:
/// </para>
/// <list type="bullet">
/// <item>The <em>tokenizer</em> is followed exactly where it decides what is markup: tag and attribute states (quoted
/// values are opaque, a quote inside an attribute name is not), comments ending exactly where the tokenizer ends them
/// (<c>--&gt;</c>, <c>--!&gt;</c>, <c>&lt;!--&gt;</c>, <c>&lt;!---&gt;</c>), bogus comments and doctypes ending at the
/// first <c>&gt;</c>, CDATA only in foreign content, and raw-text/RCDATA content ending at the first appropriate end tag
/// (a script's double-escaped region can only end later, which counts more, never less).</item>
/// <item>A raw-text element is opaque only where the tree builder certainly inserts it: never inside foreign content
/// (where <c>&lt;svg&gt;&lt;style&gt;</c> holds elements) and never inside a <c>select</c> (which ignores
/// <c>&lt;title&gt;</c>, so <c>&lt;/select&gt;</c> after it is live markup).</item>
/// <item>An element is popped only where the tree builder certainly pops it too: a matching end tag inside its scope,
/// the implied-end set (<c>p</c>, <c>li</c>, <c>dt</c>, <c>dd</c>, <c>option</c>, <c>optgroup</c>, <c>tr</c>,
/// <c>td</c>, <c>th</c>, <c>rp</c>, <c>rt</c>) under the builder's own conditions, and foreign-content break-out.
/// Every rule the scan does not model (the adoption agency's re-parenting, foster parenting, clearing to a table
/// context, Noah's ark) leaves the element on the scan's stack, so the scan stays deeper.</item>
/// <item>A formatting element popped by anything other than its own end tag stays counted (it can be reconstructed),
/// and implied <c>tbody</c>/<c>tr</c>/<c>colgroup</c> elements are counted.</item>
/// </list>
/// <para>
/// The DOM walk after parsing applies the same cap again (<see cref="NewsHtmlText"/>), so a refused outcome would hold
/// even if this model ever under-counted; the differential tests compare it with AngleSharp's own tree.
/// </para>
/// </summary>
public static partial class NewsHtmlPrescan
{
    /// <summary>The HTML input cap: 2 MiB of UTF-8, measured before parsing (§6.1, §7.10).</summary>
    public const int MaxFragmentBytes = 2 * 1024 * 1024;

    /// <summary>Checks a fragment against the size cap and the nesting cap.</summary>
    public static NewsHtmlRefusal Check(string html, int maxDepth = NewsLimits.MaxHtmlDepth)
    {
        ArgumentNullException.ThrowIfNull(html);
        if (html.Length > MaxFragmentBytes || Encoding.UTF8.GetByteCount(html) > MaxFragmentBytes)
        {
            return NewsHtmlRefusal.TooLarge;
        }

        return Measure(html, maxDepth) > maxDepth ? NewsHtmlRefusal.TooComplex : NewsHtmlRefusal.None;
    }

    /// <summary>
    /// The deepest element nesting the scan counts, with an element directly in the fragment at depth 1. The scan stops
    /// as soon as the count passes <paramref name="stopAbove"/>, so a 100,000-deep fragment costs no more than a
    /// 257-deep one.
    /// </summary>
    public static int Measure(string html, int stopAbove = int.MaxValue) => new Scanner(html, stopAbove).Run();

    private enum Ns : byte
    {
        Html,
        Svg,
        MathMl,
    }

    /// <param name="ReconstructedAbove">
    /// Text or a start tag came while this was the current node and a formatting element was detached, so the builder
    /// may have reconstructed elements above it that the scan does not hold: back at this entry, the scan does not
    /// know the builder's current node and applies no rule that depends on it.
    /// </param>
    private readonly record struct Entry(string Name, Ns Ns, bool HtmlIntegrationPoint, bool MathTextIntegrationPoint, bool ReconstructedAbove = false);

    private sealed partial class Scanner(string s, int stopAbove)
    {
        private readonly List<Entry> stack = new(64);
        private int detachedCount;
        private int max;
        private bool acceptsCdata;
        private bool baseReconstructedAbove;
        private bool formPointerSet;
        private string? encodingAttribute;
        private bool fontBreaksOut;

        private int Depth => stack.Count + detachedCount;

        private Entry? Top => stack.Count == 0 ? null : stack[^1];

        /// <summary>The current node when the scan is sure the builder's is the same, else null.</summary>
        private Entry? CertainTop => TopUncertain ? null : Top;

        /// <summary>
        /// The builder pops its current node when it matches. Where the scan is not sure its top IS the builder's
        /// current node, it neither pops nor leaves the entry matchable (a later end tag could then pop through what the
        /// builder kept): the entry stays counted and becomes unmatchable. Returns true only for a real pop.
        /// </summary>
        /// <summary>The entry made unmatchable: its namespace and integration-point flags stay, so tokenizing does not change.</summary>
        private static Entry Unmatched(Entry entry, bool reconstructedAbove) =>
            entry with { Name = UnmatchedName, ReconstructedAbove = reconstructedAbove };

        private bool PopTopIf(Func<string, bool> matches)
        {
            if (Top is not { Ns: Ns.Html } top || !matches(top.Name))
            {
                return false;
            }

            if (TopUncertain)
            {
                stack[^1] = Unmatched(stack[^1], true);
                return false;
            }

            Pop();
            return true;
        }

        private bool TopUncertain => stack.Count == 0 ? baseReconstructedAbove : stack[^1].ReconstructedAbove;

        /// <summary>Character data and most start tags reconstruct the detached formatting elements first.</summary>
        private void MayReconstruct()
        {
            if (detachedCount == 0)
            {
                return;
            }

            if (stack.Count == 0)
            {
                baseReconstructedAbove = true;
            }
            else if (!stack[^1].ReconstructedAbove)
            {
                stack[^1] = stack[^1] with { ReconstructedAbove = true };
            }
        }

        public int Run()
        {
            var n = s.Length;
            var i = 0;
            while (i < n && max <= stopAbove)
            {
                var lt = s.IndexOf('<', i);
                if (lt != i)
                {
                    MayReconstruct(); // character data
                }

                if (lt < 0 || lt + 1 >= n)
                {
                    break;
                }

                i = lt;
                var c = s[i + 1];
                if (c == '!')
                {
                    if (string.CompareOrdinal(s, i, "<!--", 0, 4) == 0)
                    {
                        i = SkipComment(i + 4);
                    }
                    else if (string.CompareOrdinal(s, i, "<![CDATA[", 0, 9) == 0)
                    {
                        i = SkipCdataOpener(i + 9);
                    }
                    else
                    {
                        i = SkipToGreaterThan(i + 2);
                    }
                }
                else if (c == '/')
                {
                    if (i + 2 < n && char.IsAsciiLetter(s[i + 2]))
                    {
                        var end = ReadTag(i + 2, out var name, out _);
                        if (end < 0)
                        {
                            break; // EOF inside a tag: the tokenizer drops it.
                        }

                        EndTag(name);
                        i = end;
                    }
                    else if (i + 2 < n && s[i + 2] == '>')
                    {
                        i += 3;
                    }
                    else
                    {
                        i = SkipToGreaterThan(i + 2);
                    }
                }
                else if (c == '?')
                {
                    i = SkipToGreaterThan(i + 2);
                }
                else if (char.IsAsciiLetter(c))
                {
                    var end = ReadTag(i + 1, out var name, out var selfClosing);
                    if (end < 0)
                    {
                        break;
                    }

                    i = StartTag(name, selfClosing, end);
                }
                else
                {
                    i++;
                }
            }

            return max;
        }
    }

    /// <summary>
    /// An entry the scan counts but never matches: an implied table parent, or what the adoption agency rearranged.
    /// It is special and a scope boundary, so every walk stops at it and nothing below it is popped through it.
    /// </summary>
    private const string UnmatchedName = "#unmatched";

    // ── Vocabulary ──────────────────────────────────────────────────────────────────────────────────────

    // Only elements the builder certainly inserts and pops at once (AngleSharp's "breakrow" and "closed" sets, the
    // head elements, input, hr and img/image). A name missing here is pushed, which over-counts.
    private static readonly HashSet<string> VoidNames = new(StringComparer.Ordinal)
    {
        "area", "base", "basefont", "bgsound", "br", "embed", "hr", "image", "img", "input", "keygen", "link",
        "menuitem", "meta", "param", "source", "track", "wbr",
    };

    private static readonly HashSet<string> RawTextNames = new(StringComparer.Ordinal)
    {
        "script", "style", "textarea", "title", "xmp", "iframe", "noembed", "noframes",
    };

    private static readonly HashSet<string> FormattingNames = new(StringComparer.Ordinal)
    {
        "a", "b", "big", "code", "em", "font", "i", "nobr", "s", "small", "strike", "strong", "tt", "u",
    };

    private static readonly HashSet<string> PClosingNames = new(StringComparer.Ordinal)
    {
        "address", "article", "aside", "blockquote", "center", "details", "dialog", "dir", "div", "dl", "fieldset",
        "figcaption", "figure", "footer", "header", "hgroup", "main", "menu", "nav", "ol", "p", "section",
        "summary", "ul", "h1", "h2", "h3", "h4", "h5", "h6", "pre", "listing", "form", "plaintext", "xmp", "hr",
    };

    private static readonly HashSet<string> SpecialNames = new(StringComparer.Ordinal)
    {
        "address", "applet", "area", "article", "aside", "base", "basefont", "bgsound", "blockquote", "body", "br",
        "button", "caption", "center", "col", "colgroup", "dd", "details", "dir", "div", "dl", "dt", "embed", "fieldset",
        "figcaption", "figure", "footer", "form", "frame", "frameset", "h1", "h2", "h3", "h4", "h5", "h6", "head",
        "header", "hgroup", "hr", "html", "iframe", "img", "input", "keygen", "li", "link", "listing", "main", "marquee",
        "menu", "meta", "nav", "noembed", "noframes", "noscript", "object", "ol", "p", "param", "plaintext", "pre",
        "script", "section", "select", "source", "style", "summary", "table", "tbody", "td", "template",
        "textarea", "tfoot", "th", "thead", "title", "tr", "track", "ul", "wbr", "xmp", UnmatchedName,
    };

    // End tags the builder closes by scope ("in body": the block list plus applet, marquee and object). AngleSharp
    // predates <search>, so it is in neither this list nor the p-closing one; listing it in either would over-pop.
    private static readonly HashSet<string> BlockEndNames = new(StringComparer.Ordinal)
    {
        "address", "article", "aside", "blockquote", "button", "center", "details", "dialog", "dir", "div", "dl",
        "fieldset", "figcaption", "figure", "footer", "header", "hgroup", "listing", "main", "menu", "nav", "ol", "pre",
        "section", "summary", "ul", "applet", "marquee", "object",
    };

    // Start tags the builder ignores in a body fragment (html and body only merge their attributes).
    private static readonly HashSet<string> IgnoredStartNames = new(StringComparer.Ordinal)
    {
        "frame", "frameset", "head", "body", "html",
    };

    private static readonly HashSet<string> BreakOutNames = new(StringComparer.Ordinal)
    {
        "b", "big", "blockquote", "body", "br", "center", "code", "dd", "div", "dl", "dt", "em", "embed", "h1", "h2",
        "h3", "h4", "h5", "h6", "head", "hr", "i", "img", "li", "listing", "menu", "meta", "nobr", "ol", "p", "pre",
        "ruby", "s", "small", "span", "strong", "strike", "sub", "sup", "table", "tt", "u", "ul", "var",
    };

    // The tokenizer lower-cases ASCII only: "\u212A" (KELVIN SIGN) must not become "k", or "<lin\u212A>" would read as void.
    private static string AsciiLower(string text, int from, int to)
    {
        var chars = new char[to - from];
        for (var k = from; k < to; k++)
        {
            var c = text[k];
            chars[k - from] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
        }

        return new string(chars);
    }

    private static bool AsciiEqualsIgnoreCase(string text, int at, string lowerName)
    {
        if (at + lowerName.Length > text.Length)
        {
            return false;
        }

        for (var k = 0; k < lowerName.Length; k++)
        {
            var c = text[at + k];
            if ((c is >= 'A' and <= 'Z' ? (char)(c + 32) : c) != lowerName[k])
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsVoid(string name) => VoidNames.Contains(name);

    private static bool IsRawText(string name) => RawTextNames.Contains(name);

    private static bool IsFormatting(string name) => FormattingNames.Contains(name);

    private static bool ClosesP(string name) => PClosingNames.Contains(name);

    private static bool IsSpecialName(string name) => SpecialNames.Contains(name);

    private static bool IsImpliedEnd(string name) =>
        name is "dd" or "dt" or "li" or "optgroup" or "option" or "p" or "rb" or "rp" or "rt" or "rtc";

    private static bool IsHeading(string name) => name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6";
}
