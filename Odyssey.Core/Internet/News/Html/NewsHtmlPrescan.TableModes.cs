namespace Odyssey.Core.Internet.News.Html;

public static partial class NewsHtmlPrescan
{
    private sealed partial class Scanner
    {
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
    }
}
