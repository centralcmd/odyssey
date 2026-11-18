using System.Globalization;
using System.Text;
using AngleSharp.Html;

namespace Odyssey.Core.Internet.News.Parsing;

/// <summary>
/// The entity pre-pass (issue #470 §8.6), run on an XML feed's text before the reader sees it. Feeds routinely use
/// HTML named entities (<c>&amp;nbsp;</c>, <c>&amp;mdash;</c>) that XML does not define, and the reader has no DTD to
/// learn them from — DTD processing is ignored, never trusted. So every named reference is rewritten:
/// <list type="bullet">
/// <item>the five XML entities are kept;</item>
/// <item>an HTML5 named entity becomes numeric character references, from AngleSharp's table;</item>
/// <item>anything else — an unknown name, a name a DOCTYPE declares, a bare <c>&amp;</c> — has its ampersand escaped,
/// so it reads as literal text. This is what makes a billion-laughs or XXE reference inert: it is never expanded and
/// never resolved, only shown.</item>
/// </list>
/// Comments, CDATA sections, processing instructions and the DOCTYPE are copied verbatim (an entity there is not a
/// reference). The pass is linear and table-driven.
/// </summary>
public static class NewsEntityPrepass
{
    private const int MaxNameLength = 40;

    public static string Apply(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        if (xml.IndexOf('&') < 0)
        {
            return xml;
        }

        var builder = new StringBuilder(xml.Length + 64);
        var i = 0;
        while (i < xml.Length)
        {
            var c = xml[i];
            if (c == '<')
            {
                var end = VerbatimEnd(xml, i);
                if (end > i)
                {
                    builder.Append(xml, i, end - i);
                    i = end;
                    continue;
                }

                builder.Append(c);
                i++;
                continue;
            }

            if (c != '&')
            {
                builder.Append(c);
                i++;
                continue;
            }

            i = Reference(xml, i, builder);
        }

        return builder.ToString();
    }

    /// <summary>The end of a comment, CDATA section, PI or DOCTYPE starting at <paramref name="start"/>; else <paramref name="start"/>.</summary>
    private static int VerbatimEnd(string xml, int start)
    {
        static int After(string xml, int from, string terminator)
        {
            var at = xml.IndexOf(terminator, from, StringComparison.Ordinal);
            return at < 0 ? xml.Length : at + terminator.Length;
        }

        if (string.CompareOrdinal(xml, start, "<!--", 0, 4) == 0)
        {
            return After(xml, start + 4, "-->");
        }

        if (string.CompareOrdinal(xml, start, "<![CDATA[", 0, 9) == 0)
        {
            return After(xml, start + 9, "]]>");
        }

        if (string.CompareOrdinal(xml, start, "<?", 0, 2) == 0)
        {
            return After(xml, start + 2, "?>");
        }

        if (string.CompareOrdinal(xml, start, "<!DOCTYPE", 0, 9) == 0)
        {
            // To the '>' that closes it, past an internal subset and any quoted literal.
            var depth = 0;
            char? quote = null;
            for (var k = start + 9; k < xml.Length; k++)
            {
                var ch = xml[k];
                if (quote is { } q)
                {
                    if (ch == q)
                    {
                        quote = null;
                    }
                }
                else if (ch is '"' or '\'')
                {
                    quote = ch;
                }
                else if (ch == '[')
                {
                    depth++;
                }
                else if (ch == ']')
                {
                    depth = Math.Max(0, depth - 1);
                }
                else if (ch == '>' && depth == 0)
                {
                    return k + 1;
                }
            }

            return xml.Length;
        }

        return start;
    }

    private static int Reference(string xml, int amp, StringBuilder builder)
    {
        var i = amp + 1;

        // A numeric character reference is kept as written.
        if (i < xml.Length && xml[i] == '#')
        {
            var k = i + 1;
            var hex = k < xml.Length && xml[k] is 'x' or 'X';
            if (hex)
            {
                k++;
            }

            var digitsStart = k;
            while (k < xml.Length && (hex ? char.IsAsciiHexDigit(xml[k]) : char.IsAsciiDigit(xml[k])))
            {
                k++;
            }

            if (k > digitsStart && k < xml.Length && xml[k] == ';' && k - digitsStart <= 8)
            {
                builder.Append(xml, amp, k + 1 - amp);
                return k + 1;
            }

            builder.Append("&amp;");
            return i;
        }

        var nameEnd = i;
        while (nameEnd < xml.Length && nameEnd - i < MaxNameLength && char.IsAsciiLetterOrDigit(xml[nameEnd]))
        {
            nameEnd++;
        }

        if (nameEnd == i || nameEnd >= xml.Length || xml[nameEnd] != ';' || !char.IsAsciiLetter(xml[i]))
        {
            builder.Append("&amp;");
            return i;
        }

        var name = xml[i..nameEnd];
        if (name is "amp" or "lt" or "gt" or "quot" or "apos")
        {
            builder.Append(xml, amp, nameEnd + 1 - amp);
            return nameEnd + 1;
        }

        var symbol = HtmlEntityProvider.Resolver.GetSymbol(name + ";");
        if (string.IsNullOrEmpty(symbol))
        {
            builder.Append("&amp;");
            return i;
        }

        foreach (var rune in symbol.EnumerateRunes())
        {
            builder.Append("&#x").Append(rune.Value.ToString("X", CultureInfo.InvariantCulture)).Append(';');
        }

        return nameEnd + 1;
    }
}
