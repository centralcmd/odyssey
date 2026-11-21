namespace Odyssey.Core.Internet.News.Html;

public static partial class NewsHtmlPrescan
{
    private sealed partial class Scanner
    {
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
    }
}
