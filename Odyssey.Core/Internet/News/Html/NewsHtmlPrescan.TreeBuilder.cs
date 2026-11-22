namespace Odyssey.Core.Internet.News.Html;

public static partial class NewsHtmlPrescan
{
    private sealed partial class Scanner
    {
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
}
