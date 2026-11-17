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
public static class NewsHtmlPrescan
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

    private sealed class Scanner(string s, int stopAbove)
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

        // ── Tokenizer ───────────────────────────────────────────────────────────────────────────────────

        private int SkipToGreaterThan(int from)
        {
            var gt = s.IndexOf('>', from);
            return gt < 0 ? s.Length : gt + 1;
        }

        /// <summary>
        /// <c>&lt;![CDATA[</c> opens a CDATA section (to <c>]]&gt;</c>) while AngleSharp's tokenizer accepts character
        /// data, and a bogus comment (to the first <c>&gt;</c>) otherwise. That flag follows the last element pushed and
        /// the new current node after a pop, and a self-closed <c>&lt;svg/&gt;</c> or <c>&lt;math/&gt;</c> leaves it set:
        /// <see cref="acceptsCdata"/> tracks it the same way.
        /// </summary>
        private int SkipCdataOpener(int from)
        {
            if (acceptsCdata)
            {
                var close = s.IndexOf("]]>", from, StringComparison.Ordinal);
                return close < 0 ? s.Length : close + 3;
            }

            return SkipToGreaterThan(from);
        }

        private int SkipComment(int from)
        {
            if (from < s.Length && s[from] == '>')
            {
                return from + 1; // <!-->
            }

            if (from + 1 < s.Length && s[from] == '-' && s[from + 1] == '>')
            {
                return from + 2; // <!--->
            }

            var a = s.IndexOf("-->", from, StringComparison.Ordinal);
            var b = s.IndexOf("--!>", from, StringComparison.Ordinal);
            if (a < 0 && b < 0)
            {
                return s.Length;
            }

            return a >= 0 && (b < 0 || a <= b) ? a + 3 : b + 4;
        }

        /// <summary>
        /// Reads a tag from its name to past its <c>&gt;</c>, following the tokenizer's tag and attribute states.
        /// Returns -1 at EOF. Captures the two attribute facts the tree builder's dispatch depends on.
        /// </summary>
        private int ReadTag(int from, out string name, out bool selfClosing)
        {
            var n = s.Length;
            var i = from;
            while (i < n && !IsTagWhitespace(s[i]) && s[i] != '/' && s[i] != '>')
            {
                i++;
            }

            name = AsciiLower(s, from, i);
            selfClosing = false;
            encodingAttribute = null;
            fontBreaksOut = false;

            while (true)
            {
                // Before attribute name.
                while (i < n && IsTagWhitespace(s[i]))
                {
                    i++;
                }

                if (i >= n)
                {
                    return -1;
                }

                if (s[i] == '>')
                {
                    return i + 1;
                }

                if (s[i] == '/')
                {
                    i++;
                    if (i >= n)
                    {
                        return -1;
                    }

                    if (s[i] == '>')
                    {
                        selfClosing = true;
                        return i + 1;
                    }

                    continue; // reconsume in "before attribute name"
                }

                // Attribute name: the first character may be '='; '"', '\'' and '<' are part of the name.
                var nameStart = i;
                i++;
                while (i < n && !IsTagWhitespace(s[i]) && s[i] != '/' && s[i] != '>' && s[i] != '=')
                {
                    i++;
                }

                var attributeName = s[nameStart..i];

                // After attribute name.
                while (i < n && IsTagWhitespace(s[i]))
                {
                    i++;
                }

                if (i >= n)
                {
                    return -1;
                }

                if (s[i] != '=')
                {
                    NoteAttribute(attributeName, string.Empty);
                    continue; // '/', '>' or a new attribute name
                }

                i++;
                while (i < n && IsTagWhitespace(s[i]))
                {
                    i++;
                }

                if (i >= n)
                {
                    return -1;
                }

                string value;
                if (s[i] is '"' or '\'')
                {
                    var quote = s[i];
                    var close = s.IndexOf(quote, i + 1);
                    if (close < 0)
                    {
                        return -1;
                    }

                    value = s[(i + 1)..close];
                    i = close + 1; // after quoted value: anything but whitespace, '/' or '>' reconsumes as a new name
                }
                else if (s[i] == '>')
                {
                    value = string.Empty;
                }
                else
                {
                    var valueStart = i;
                    while (i < n && !IsTagWhitespace(s[i]) && s[i] != '>')
                    {
                        i++;
                    }

                    value = s[valueStart..i];
                }

                NoteAttribute(attributeName, value);
            }
        }

        private void NoteAttribute(string attributeName, string value)
        {
            if (AsciiEqualsIgnoreCase(attributeName, 0, "encoding") && attributeName.Length == 8)
            {
                encodingAttribute ??= value;
            }
            else if (AsciiLower(attributeName, 0, attributeName.Length) is "color" or "face" or "size")
            {
                fontBreaksOut = true;
            }
        }

        private static bool IsTagWhitespace(char c) => c is ' ' or '\t' or '\n' or '\f' or '\r';

        /// <summary>The index of the raw-text element's appropriate end tag, which the main loop then pops; else EOF.</summary>
        private int SkipRawText(string name, int from)
        {
            var i = from;
            while (true)
            {
                var candidate = s.IndexOf("</", i, StringComparison.Ordinal);
                if (candidate < 0)
                {
                    return s.Length;
                }

                var after = candidate + 2 + name.Length;
                if (after < s.Length
                    && AsciiEqualsIgnoreCase(s, candidate + 2, name)
                    && (IsTagWhitespace(s[after]) || s[after] is '/' or '>'))
                {
                    return candidate;
                }

                i = candidate + 2;
            }
        }

        /// <summary>
        /// Script data, with the tokenizer's escape states: inside <c>&lt;!--</c> a nested <c>&lt;script&gt;</c>
        /// double-escapes, and its <c>&lt;/script&gt;</c> does not end the element. Returns the index of the end tag.
        /// </summary>
        private int SkipScriptData(int from)
        {
            const int Data = 0, Escaped = 1, DoubleEscaped = 2;
            var state = Data;
            var i = from;
            while (i < s.Length)
            {
                if (state != Data && string.CompareOrdinal(s, i, "-->", 0, 3) == 0)
                {
                    state = Data;
                    i += 3;
                    continue;
                }

                if (s[i] != '<')
                {
                    i++;
                    continue;
                }

                if (state == Data && string.CompareOrdinal(s, i, "<!--", 0, 4) == 0)
                {
                    state = Escaped;
                    i += 2; // the "--" also counts towards a closing "-->", as in "<!-->"
                    continue;
                }

                if (IsScriptTag(i + 2) && s[i + 1] == '/')
                {
                    if (state != DoubleEscaped)
                    {
                        return i;
                    }

                    state = Escaped;
                    i += 8;
                    continue;
                }

                if (state == Escaped && IsScriptTag(i + 1))
                {
                    state = DoubleEscaped;
                    i += 7;
                    continue;
                }

                i++;
            }

            return s.Length;
        }

        private bool IsScriptTag(int at) =>
            at + 6 < s.Length && AsciiEqualsIgnoreCase(s, at, "script") && (IsTagWhitespace(s[at + 6]) || s[at + 6] is '/' or '>');

        // ── Tree builder model ──────────────────────────────────────────────────────────────────────────

        private bool ProcessesStartTagAsHtml(string name)
        {
            if (Top is not { } top || top.Ns == Ns.Html || top.HtmlIntegrationPoint)
            {
                return true;
            }

            if (top.MathTextIntegrationPoint && name is not ("mglyph" or "malignmark"))
            {
                return true;
            }

            return top.Ns == Ns.MathMl && top.Name == "annotation-xml" && name == "svg";
        }

        private int StartTag(string name, bool selfClosing, int end)
        {
            if (!ProcessesStartTagAsHtml(name))
            {
                if (!BreaksOutOfForeignContent(name))
                {
                    var ns = Top!.Value.Ns;
                    Count(1);
                    if (!selfClosing)
                    {
                        Push(ForeignEntry(name, ns));
                    }

                    return end;
                }

                // pop until the current node is an HTML element or an integration point, then reprocess as HTML.
                while (Top is { } top && top.Ns != Ns.Html && !top.HtmlIntegrationPoint && !top.MathTextIntegrationPoint)
                {
                    Pop();
                }
            }

            return HtmlStartTag(name, selfClosing, end);
        }

        private int HtmlStartTag(string name, bool selfClosing, int end)
        {
            MayReconstruct();

            if (InSelectMode())
            {
                return SelectStartTag(name, end);
            }

            if (IgnoredStartNames.Contains(name))
            {
                return end; // the builder ignores these here, and so their end tags find nothing to close
            }

            if (name is "svg" or "math")
            {
                Count(1);
                if (!selfClosing)
                {
                    Push(ForeignEntry(name, name == "svg" ? Ns.Svg : Ns.MathMl));
                }

                acceptsCdata = true; // AngleSharp closes a self-closed one without resetting the flag
                return end;
            }

            if (TableStartTag(name))
            {
                return end;
            }

            if (name == "form")
            {
                if (formPointerSet)
                {
                    return end; // a nested form is ignored outright, p-closing included
                }

                formPointerSet = true;
            }

            if (name is "a" or "nobr" && InScope(name, DefaultScope))
            {
                // An open a (or nobr) is closed by the adoption agency before the new one is inserted.
                FormattingEndTag(name);
            }

            ApplyImpliedEnds(name);

            if (IsVoid(name))
            {
                Count(1);
                return end;
            }

            PushHtml(name);
            if (name == "plaintext")
            {
                return s.Length;
            }

            return name == "script" ? SkipScriptData(end) : IsRawText(name) ? SkipRawText(name, end) : end;
        }

        private void PushHtml(string name)
        {
            Count(1);
            Push(new Entry(name, Ns.Html, false, false));
        }

        /// <summary>
        /// AngleSharp's "in select" mode, which is permissive: below a non-select current node it inserts ANY start tag as
        /// an ordinary element (a void name, <c>title</c> and <c>svg</c> included), and only directly under the select
        /// does it ignore one. Only <c>script</c> and <c>xmp</c> switch the tokenizer there.
        /// </summary>
        private int SelectStartTag(string name, int end)
        {
            switch (name)
            {
                case "option":
                    PopIfTop("option");
                    PushHtml(name);
                    return end;
                case "optgroup":
                    PopIfTop("option");
                    PopIfTop("optgroup");
                    PushHtml(name);
                    return end;
                case "html":
                    return end;
                case "button" or "template":
                    PushHtml(name);
                    return end;
                case "select":
                    PopThrough("select");
                    return end;
                case "input" or "keygen" or "textarea":
                    if (InSelectScope())
                    {
                        PopThrough("select");
                        return HtmlStartTag(name, false, end);
                    }

                    return end;
                case "script":
                    PushHtml(name);
                    return SkipScriptData(end);
                case "xmp":
                    PushHtml(name);
                    return SkipRawText(name, end);
                case "caption" or "table" or "tbody" or "tfoot" or "thead" or "tr" or "td" or "th" when SelectIsInTable():
                    // "In select in table": the select closes and the part is reprocessed in the table's mode.
                    PopThrough("select");
                    return HtmlStartTag(name, false, end);
            }

            if (CertainTop is { Name: "select", Ns: Ns.Html })
            {
                return end;
            }

            PushHtml(name);
            return end;
        }

        /// <summary>A select is open and no template stands above it (template contents have their own modes).</summary>
        private bool InSelectMode()
        {
            for (var k = stack.Count - 1; k >= 0; k--)
            {
                if (stack[k] is { Ns: Ns.Html, Name: "template" })
                {
                    return false;
                }

                if (stack[k] is { Ns: Ns.Html, Name: "select" })
                {
                    return true;
                }
            }

            return false;
        }

        private bool SelectIsInTable()
        {
            for (var k = stack.Count - 1; k >= 0; k--)
            {
                if (stack[k] is { Ns: Ns.Html, Name: "select" })
                {
                    return Enumerable.Range(0, k).Any(j => stack[j] is { Ns: Ns.Html, Name: "table" });
                }
            }

            return false;
        }

        private bool InSelectScope()
        {
            for (var k = stack.Count - 1; k >= 0; k--)
            {
                var node = stack[k];
                if (node is { Ns: Ns.Html, Name: "select" })
                {
                    return true;
                }

                if (node is not { Ns: Ns.Html, Name: "option" or "optgroup" })
                {
                    PoisonIfUnmatched(k);
                    return false;
                }
            }

            return false;
        }

        private void PopIfTop(string name) => PopTopIf(n => n == name);

        // ── Table modes ─────────────────────────────────────────────────────────────────────────────────

        private enum TableMode
        {
            None,
            Table,
            Body,
            Row,
            Cell,
            Caption,
            ColumnGroup,
            Template,

            /// <summary>An unmatchable entry stands where the mode would be read: anything could be open there.</summary>
            Unknown,
        }

        /// <summary>
        /// The table insertion mode the builder is in, read the way its reset does: the nearest table part from the top.
        /// Foster-parented elements above it leave the mode unchanged, which is why they are walked past.
        /// </summary>
        private (TableMode Mode, int Index) CurrentTableMode()
        {
            for (var k = stack.Count - 1; k >= 0; k--)
            {
                var node = stack[k];
                if (node.Ns != Ns.Html)
                {
                    return (TableMode.None, -1);
                }

                switch (node.Name)
                {
                    case UnmatchedName:
                        return (TableMode.Unknown, -1); // it could be a table part: no mode is read through it
                    case "table":
                        return (TableMode.Table, k);
                    case "tbody" or "thead" or "tfoot":
                        return (TableMode.Body, k);
                    case "tr":
                        return (TableMode.Row, k);
                    case "td" or "th":
                        return (TableMode.Cell, k);
                    case "caption":
                        return (TableMode.Caption, k);
                    case "colgroup":
                        return (TableMode.ColumnGroup, k);
                    case "template":
                        return (TableMode.Template, k);
                    case "select":
                        return (TableMode.None, -1);
                }
            }

            return (TableMode.None, -1);
        }

        private bool HasOpenTable() => HasOpenHtml("table");

        /// <summary>
        /// The table modes' start-tag rules for table parts, and the column group mode's "anything else". Returns false
        /// when the tag is not a table matter and the body rules apply.
        /// </summary>
        private bool TableStartTag(string name)
        {
            if (name == "form" && CurrentTableMode().Mode is TableMode.Table or TableMode.Body or TableMode.Row)
            {
                // In a table a form is inserted and popped at once, and nothing is closed for it.
                if (!formPointerSet && !HasOpenHtml("template"))
                {
                    formPointerSet = true;
                    Count(1);
                }

                return true;
            }

            var isPart = name is "table" or "caption" or "colgroup" or "col" or "tbody" or "thead" or "tfoot" or "tr" or "td" or "th";
            if (HasOpenHtml("template"))
            {
                // Template contents keep their own stack of modes; inside one the scan never pops for a table part, and
                // counts each with the parents it could imply.
                if (isPart)
                {
                    CountImplied(name);
                }

                return isPart;
            }

            for (var guard = 0; guard < 8; guard++)
            {
                var (mode, index) = CurrentTableMode();
                if (mode == TableMode.ColumnGroup && index == stack.Count - 1)
                {
                    if (name == "col")
                    {
                        Count(1);
                        return true;
                    }

                    if (name != "template")
                    {
                        Pop(); // the colgroup closes, and the tag is reprocessed in the table mode
                        continue;
                    }
                }

                if (!isPart)
                {
                    return false;
                }

                switch (mode)
                {
                    case TableMode.Unknown:
                        CountImplied(name);
                        return true;
                    case TableMode.None:
                        if (!HasOpenTable())
                        {
                            // AngleSharp ignores table parts in body ("nested"); a table itself is an ordinary push.
                            if (name == "table")
                            {
                                PushHtml(name);
                            }

                            return true;
                        }

                        // A table below something the scan does not read as a table mode: count the implied parents.
                        CountImplied(name);
                        return true;
                    case TableMode.Table:
                        if (name == "table")
                        {
                            PopThrough(index);
                            continue;
                        }

                        PopThrough(index + 1); // clear the stack back to a table context
                        switch (name)
                        {
                            case "caption" or "colgroup" or "tbody" or "thead" or "tfoot":
                                PushHtml(name);
                                return true;
                            case "col":
                                PushHtml("colgroup");
                                Count(1);
                                return true;
                            default:
                                PushHtml("tbody");
                                continue; // tr, td, th reprocessed in the table body mode
                        }

                    case TableMode.Body:
                        if (name is "tr" or "td" or "th")
                        {
                            PopThrough(index + 1); // clear back to a table body context
                            if (name == "tr")
                            {
                                PushHtml(name);
                                return true;
                            }

                            PushHtml("tr");
                            continue;
                        }

                        PopThrough(index); // the section closes; reprocess in the table mode
                        continue;
                    case TableMode.Row:
                        if (name is "td" or "th")
                        {
                            PopThrough(index + 1); // clear back to a table row context
                            PushHtml(name);
                            return true;
                        }

                        PopThrough(index); // the row closes; reprocess in the table body mode
                        continue;
                    case TableMode.Cell:
                        if (name == "table")
                        {
                            PushHtml(name); // a table nested in a cell (quirks: no p is closed)
                            return true;
                        }

                        PopThrough(index); // the cell closes; reprocess in the row mode
                        continue;
                    case TableMode.Caption:
                        if (name == "table")
                        {
                            PushHtml(name);
                            return true;
                        }

                        PopThrough(index); // the caption closes; reprocess in the table mode
                        continue;
                    case TableMode.ColumnGroup:
                        // The colgroup is not the current node: AngleSharp ignores the tag.
                        return true;
                    case TableMode.Template:
                        // Template contents take table parts directly, with no implied parents.
                        if (name == "col")
                        {
                            Count(1);
                        }
                        else
                        {
                            PushHtml(name);
                        }

                        return true;
                }
            }

            CountImplied(name);
            return true;
        }

        /// <summary>
        /// A table part where the scan cannot read the mode: counted with every parent it could imply, each kept open as
        /// an <see cref="UnmatchedName"/> entry that nothing matches and every scope walk stops at.
        /// </summary>
        private void CountImplied(string name)
        {
            var parents = name switch
            {
                "td" or "th" => 2,
                "tr" or "col" => 1,
                _ => 0,
            };
            for (var k = 0; k < parents; k++)
            {
                PushHtml(UnmatchedName);
            }

            if (name == "col")
            {
                Count(1);
            }
            else
            {
                PushHtml(name);
            }
        }

        /// <summary>The closings the tree builder certainly performs for a start tag (never inside a select).</summary>
        private void ApplyImpliedEnds(string name)
        {
            switch (name)
            {
                case "li":
                    CloseListItem(static n => n == "li");
                    break;
                case "dd" or "dt":
                    CloseListItem(static n => n is "dd" or "dt");
                    break;
                case "option" or "optgroup":
                    PopIfTop("option");
                    return;
                case "rp" or "rt" or "rb" or "rtc":
                    // Generate implied end tags (except rtc for rp and rt), exactly: popping less here leaves an element
                    // the builder closed, which a later scope walk could then match and pop through what it kept.
                    if (InScope("ruby", DefaultScope))
                    {
                        var exceptRtc = name is "rp" or "rt";
                        while (PopTopIf(n => IsImpliedEnd(n) && !(exceptRtc && n == "rtc")))
                        {
                        }
                    }

                    return;
                case "button":
                    if (InScope("button", DefaultScope))
                    {
                        PopThrough("button");
                    }

                    return;
            }

            if (ClosesP(name) && InScope("p", ButtonScope))
            {
                PopThrough("p");
            }

            if (IsHeading(name))
            {
                PopTopIf(IsHeading);
            }
        }

        /// <summary>The <c>li</c> / <c>dd</c>/<c>dt</c> loop, then closing a <c>p</c> in button scope.</summary>
        private void CloseListItem(Func<string, bool> matches)
        {
            for (var k = stack.Count - 1; k >= 0; k--)
            {
                var node = stack[k];
                if (node.Ns == Ns.Html && matches(node.Name))
                {
                    PopThrough(k);
                    break;
                }

                if (IsSpecial(node) && !(node.Ns == Ns.Html && node.Name is "address" or "div" or "p"))
                {
                    PoisonIfUnmatched(k);
                    break;
                }
            }

            if (InScope("p", ButtonScope))
            {
                PopThrough("p");
            }
        }

        private void EndTag(string name)
        {
            // Foreign content: walk down while the nodes are foreign; a match pops to it, an HTML node hands over.
            if (Top is { Ns: not Ns.Html })
            {
                if (name is "br" or "p")
                {
                    // Popped to an HTML node or an integration point, then handled by the HTML rule.
                    while (Top is { } t && t.Ns != Ns.Html && !t.HtmlIntegrationPoint && !t.MathTextIntegrationPoint)
                    {
                        Pop();
                    }
                }
                else
                {
                    var handedOver = false;
                    for (var k = stack.Count - 1; k >= 0; k--)
                    {
                        var node = stack[k];
                        if (node.Ns == Ns.Html)
                        {
                            handedOver = true;
                            break;
                        }

                        if (node.Name == name)
                        {
                            PopThrough(k);
                            return;
                        }
                    }

                    if (!handedOver)
                    {
                        return;
                    }
                }
            }

            if (InSelectMode())
            {
                SelectEndTag(name);
                return;
            }

            if (TableEndTag(name))
            {
                return;
            }

            switch (name)
            {
                case "p":
                    if (InScope("p", ButtonScope))
                    {
                        PopThrough("p");
                    }
                    else
                    {
                        Count(1); // an empty p is inserted
                    }

                    return;
                case "br":
                    Count(1);
                    return;
                case "li":
                    if (InScope("li", ListItemScope))
                    {
                        PopThrough("li");
                    }

                    return;
                case "dd" or "dt":
                    if (InScope(name, DefaultScope))
                    {
                        PopThrough(name);
                    }

                    return;
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    for (var k = stack.Count - 1; k >= 0; k--)
                    {
                        var node = stack[k];
                        if (node.Ns == Ns.Html && IsHeading(node.Name))
                        {
                            PopThrough(k);
                            return;
                        }

                        if (DefaultScope(node))
                        {
                            PoisonIfUnmatched(k);
                            return;
                        }
                    }

                    return;
                case "body" or "html":
                    return;
                case "template":
                    PopThrough("template"); // the builder pops through it whatever is open inside
                    return;
                case "form":
                    // Implied end tags, then the builder takes the form element itself out of the stack and leaves
                    // what is open inside it, still nested as deep: the scan keeps the entry, unmatchable.
                    formPointerSet = false;
                    var formIndex = IndexInScope("form", DefaultScope);
                    if (formIndex >= 0)
                    {
                        while (PopTopIf(IsImpliedEnd))
                        {
                        }

                        stack[formIndex] = Unmatched(stack[formIndex], stack[formIndex].ReconstructedAbove);
                    }

                    return;
            }

            if (IsFormatting(name))
            {
                FormattingEndTag(name);
                return;
            }

            if (BlockEndNames.Contains(name))
            {
                if (InScope(name, DefaultScope))
                {
                    PopThrough(name);
                }

                return;
            }

            // Any other end tag: the nearest match pops, unless a special element comes first.
            for (var k = stack.Count - 1; k >= 0; k--)
            {
                var node = stack[k];
                if (node.Ns == Ns.Html && node.Name == name)
                {
                    PopThrough(k);
                    return;
                }

                if (IsSpecial(node))
                {
                    PoisonIfUnmatched(k);
                    return;
                }
            }
        }

        /// <summary>AngleSharp's "in select" end tags: below a non-select current node a matching current node closes.</summary>
        private void SelectEndTag(string name)
        {
            if (name is "caption" or "table" or "tbody" or "tfoot" or "thead" or "tr" or "td" or "th" && SelectIsInTable())
            {
                if (InTableScope(name))
                {
                    PopThrough("select");
                    EndTag(name);
                }

                return;
            }

            switch (name)
            {
                case "template":
                    PopThrough("template");
                    return;
                case "option":
                    PopIfTop("option");
                    return;
                case "optgroup":
                    if (stack.Count > 1 && stack[^2] is { Name: "optgroup", Ns: Ns.Html })
                    {
                        PopIfTop("option");
                    }

                    PopIfTop("optgroup");
                    return;
                case "select":
                    if (InSelectScope())
                    {
                        PopThrough("select");
                    }

                    return;
            }

            PopTopIf(n => n != "select" && n == name);
        }

        /// <summary>The table modes' end-tag rules. Returns false when the body rules apply.</summary>
        private bool TableEndTag(string name)
        {
            var isPart = name is "table" or "caption" or "colgroup" or "col" or "tbody" or "thead" or "tfoot" or "tr" or "td" or "th";
            if (HasOpenHtml("template"))
            {
                return isPart;
            }

            for (var guard = 0; guard < 8; guard++)
            {
                var (mode, index) = CurrentTableMode();
                switch (mode)
                {
                    case TableMode.None or TableMode.Template or TableMode.Unknown:
                        return isPart; // outside a table mode a table part's end tag closes nothing
                    case TableMode.ColumnGroup:
                        if (index != stack.Count - 1)
                        {
                            return isPart;
                        }

                        if (name == "colgroup")
                        {
                            Pop();
                            return true;
                        }

                        if (name == "col")
                        {
                            return true;
                        }

                        Pop(); // anything else closes the colgroup and is reprocessed
                        continue;
                    case TableMode.Table:
                        if (name == "table")
                        {
                            PopThrough(index);
                            return true;
                        }

                        return isPart || name is "body" or "html";
                    case TableMode.Body:
                        if (name is "tbody" or "thead" or "tfoot")
                        {
                            if (stack[index].Name == name)
                            {
                                PopThrough(index);
                            }

                            return true;
                        }

                        if (name == "table")
                        {
                            PopThrough(index);
                            continue;
                        }

                        return isPart || name is "body" or "html";
                    case TableMode.Row:
                        if (name == "tr")
                        {
                            PopThrough(index);
                            return true;
                        }

                        if (name == "table" || (name is "tbody" or "thead" or "tfoot" && InTableScope(name)))
                        {
                            PopThrough(index);
                            continue;
                        }

                        return isPart || name is "body" or "html";
                    case TableMode.Cell:
                        if (name is "td" or "th")
                        {
                            if (stack[index].Name == name)
                            {
                                PopThrough(index);
                            }

                            return true;
                        }

                        if (name is "table" or "tbody" or "thead" or "tfoot" or "tr")
                        {
                            if (InTableScope(name))
                            {
                                PopThrough(index);
                                continue;
                            }

                            return true;
                        }

                        return name is "caption" or "col" or "colgroup" or "body" or "html" ? true : false;
                    case TableMode.Caption:
                        if (name == "caption")
                        {
                            PopThrough(index);
                            return true;
                        }

                        if (name == "table")
                        {
                            PopThrough(index);
                            continue;
                        }

                        return isPart || name is "body" or "html";
                }
            }

            return true;
        }

        private bool InTableScope(string name)
        {
            for (var k = stack.Count - 1; k >= 0; k--)
            {
                var node = stack[k];
                if (node.Ns == Ns.Html && node.Name == name)
                {
                    return true;
                }

                if (node is { Ns: Ns.Html, Name: "table" or "template" or "html" or UnmatchedName })
                {
                    PoisonIfUnmatched(k);
                    return false;
                }
            }

            return false;
        }

        private void FormattingEndTag(string name)
        {
            if (CertainTop is { Ns: Ns.Html } top && top.Name == name)
            {
                stack.RemoveAt(stack.Count - 1); // closed by its own end tag: it is not reconstructed
                acceptsCdata = Top is { Ns: not Ns.Html };
                return;
            }

            var index = IndexInScope(name, DefaultScope);
            if (index < 0)
            {
                // Out of scope, or only detached: the builder ignores the tag, or runs the adoption agency on a
                // reconstructed copy the scan does not hold, popping whatever was opened above that copy. The scan
                // keeps counting all of it, but none of it can be matched any more.
                if (!HasOpenHtml(name) && detachedCount > 0)
                {
                    PoisonAboveReconstruction();
                }

                return;
            }

            if (!TopUncertain && !Enumerable.Range(index + 1, stack.Count - index - 1).Any(k => IsSpecial(stack[k])))
            {
                // No furthest block: the builder pops everything above the element, then the element itself.
                PopThrough(index + 1);
                stack.RemoveAt(index);
                acceptsCdata = Top is { Ns: not Ns.Html };
                return;
            }

            // A furthest block: the adoption agency re-parents and can move a block down (foster parenting). The scan
            // keeps every entry from the formatting element up, still counted, but none can be matched any more.
            for (var k = index; k < stack.Count; k++)
            {
                stack[k] = Unmatched(stack[k], true);
            }

            // AngleSharp leaves the furthest block's side current: its flag reads an HTML node there.
            acceptsCdata = false;
        }

        // ── Stack ───────────────────────────────────────────────────────────────────────────────────────

        private void Count(int added) => max = Math.Max(max, Depth + added);

        private void Push(Entry entry)
        {
            stack.Add(entry);
            acceptsCdata = entry.Ns != Ns.Html;
        }

        private void Pop()
        {
            var entry = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            acceptsCdata = Top is { Ns: not Ns.Html };
            if (entry.Ns == Ns.Html && IsFormatting(entry.Name))
            {
                // Closed by something other than its own end tag: the builder may reconstruct it, so it stays counted.
                detachedCount++;
            }
        }

        private void PopThrough(int index)
        {
            while (stack.Count > index)
            {
                Pop();
            }
        }

        private void PopThrough(string htmlName)
        {
            for (var k = stack.Count - 1; k >= 0; k--)
            {
                if (stack[k].Ns == Ns.Html && stack[k].Name == htmlName)
                {
                    PopThrough(k);
                    return;
                }
            }
        }

        /// <summary>
        /// A walk stopped at an unmatched entry, which may be the very element it looked for: whatever is above it may
        /// have been popped by the builder, so it all becomes unmatchable too (still counted).
        /// </summary>
        private void PoisonIfUnmatched(int k)
        {
            if (stack[k].Name != UnmatchedName)
            {
                return;
            }

            for (var j = k + 1; j < stack.Count; j++)
            {
                if (stack[j].Name != UnmatchedName)
                {
                    stack[j] = Unmatched(stack[j], stack[j].ReconstructedAbove);
                }
            }
        }

        private void PoisonAboveReconstruction()
        {
            var lowest = baseReconstructedAbove ? -1 : stack.FindIndex(e => e.ReconstructedAbove);
            if (lowest < -1 || (lowest == -1 && !baseReconstructedAbove))
            {
                return;
            }

            for (var j = lowest + 1; j < stack.Count; j++)
            {
                if (stack[j].Name != UnmatchedName)
                {
                    stack[j] = Unmatched(stack[j], stack[j].ReconstructedAbove);
                }
            }
        }

        private bool HasOpenHtml(string name)
        {
            for (var k = stack.Count - 1; k >= 0; k--)
            {
                if (stack[k].Ns == Ns.Html && stack[k].Name == name)
                {
                    return true;
                }
            }

            return false;
        }

        private bool InScope(string name, Func<Entry, bool> boundary) => IndexInScope(name, boundary) >= 0;

        private int IndexInScope(string name, Func<Entry, bool> boundary)
        {
            for (var k = stack.Count - 1; k >= 0; k--)
            {
                var node = stack[k];
                if (node.Ns == Ns.Html && node.Name == name)
                {
                    return k;
                }

                if (boundary(node))
                {
                    PoisonIfUnmatched(k);
                    return -1;
                }
            }

            return -1;
        }

        private Entry ForeignEntry(string name, Ns ns)
        {
            if (ns == Ns.Svg)
            {
                return new Entry(name, ns, name is "foreignobject" or "desc" or "title", false);
            }

            var htmlPoint = name == "annotation-xml" && encodingAttribute is { } encoding
                && AsciiLower(encoding, 0, encoding.Length) is "text/html" or "application/xhtml+xml";
            return new Entry(name, ns, htmlPoint, name is "mi" or "mo" or "mn" or "ms" or "mtext");
        }

        private bool BreaksOutOfForeignContent(string name) =>
            name == "font" ? fontBreaksOut : BreakOutNames.Contains(name);

        private static bool DefaultScope(Entry node) =>
            node.Name == UnmatchedName || node.Ns switch
            {
                Ns.Html => node.Name is "applet" or "caption" or "html" or "table" or "td" or "th" or "marquee" or "object" or "template" or UnmatchedName,
                Ns.MathMl => node.Name is "mi" or "mo" or "mn" or "ms" or "mtext" or "annotation-xml",
                _ => node.Name is "foreignobject" or "desc" or "title",
            };

        private static bool ButtonScope(Entry node) => DefaultScope(node) || (node.Ns == Ns.Html && node.Name == "button");

        private static bool ListItemScope(Entry node) => DefaultScope(node) || (node.Ns == Ns.Html && node.Name is "ol" or "ul");

        private static bool IsSpecial(Entry node) =>
            node.Name == UnmatchedName || (node.Ns == Ns.Html ? IsSpecialName(node.Name) : DefaultScope(node));
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
