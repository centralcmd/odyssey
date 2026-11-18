using System.Text;

namespace Odyssey.Core.Internet.News.Content;

/// <summary>
/// The news reader's one text sanitiser and its clamping helpers (issue #470 §8.7). <see cref="Clean"/> runs on every
/// string derived from a feed, before it is hashed, stored or measured. Every length here is in Unicode scalar values
/// (code points), the unit a MariaDB <c>utf8mb4</c> column counts, so a clamp never splits a surrogate pair and never
/// disagrees with the column it fits.
/// </summary>
public static class NewsText
{
    /// <summary>The marker a cut title or other short text ends with.</summary>
    public const string Ellipsis = "…";

    /// <summary>
    /// The text sanitiser: unpaired surrogates, C0 and C1 controls (except <c>\n</c>) and the invisible formatting and
    /// bidi controls listed in §8.7 are removed, <c>\t</c> becomes a space, U+2028/U+2029 become <c>\n</c>, and the
    /// result is NFC-normalised. Removal runs before normalisation because <see cref="string.Normalize()"/> throws on an
    /// unpaired surrogate.
    /// </summary>
    public static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    var scalar = char.ConvertToUtf32(c, value[i + 1]);
                    if (!IsTagCharacter(scalar))
                    {
                        builder.Append(c).Append(value[i + 1]);
                    }

                    i++;
                }

                continue;
            }

            if (char.IsLowSurrogate(c))
            {
                continue;
            }

            switch (c)
            {
                case '\n':
                    builder.Append('\n');
                    continue;
                case '\t':
                    builder.Append(' ');
                    continue;
                case '\u2028':
                case '\u2029':
                    builder.Append('\n');
                    continue;
            }

            if (c < 0x20 || c is >= '\u007F' and <= '\u009F' || IsRemovedFormatCharacter(c))
            {
                continue;
            }

            builder.Append(c);
        }

        var cleaned = builder.ToString();
        return cleaned.IsNormalized(NormalizationForm.FormC) ? cleaned : cleaned.Normalize(NormalizationForm.FormC);
    }

    /// <summary><see cref="Clean"/>, then every run of whitespace (line breaks included) collapsed to one space and trimmed.</summary>
    public static string CleanLine(string? value) => CollapseToLine(Clean(value));

    /// <summary>Every whitespace run collapsed to a single space, trimmed. For titles, authors and the excerpt.</summary>
    public static string CollapseToLine(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>The length of <paramref name="value"/> in Unicode scalar values.</summary>
    public static int CodePointLength(string value)
    {
        var count = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }

            count++;
        }

        return count;
    }

    /// <summary>The first <paramref name="codePoints"/> scalar values of <paramref name="value"/>, never splitting a pair.</summary>
    public static string TakeCodePoints(string value, int codePoints)
    {
        if (codePoints <= 0)
        {
            return string.Empty;
        }

        var taken = 0;
        var i = 0;
        while (i < value.Length && taken < codePoints)
        {
            i += char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]) ? 2 : 1;
            taken++;
        }

        return i >= value.Length ? value : value[..i];
    }

    /// <summary>
    /// Clamps short text to <paramref name="maxCodePoints"/>, cut on a code-point boundary and ending with
    /// <see cref="Ellipsis"/> when anything was removed (§8.7). The result is never longer than the bound.
    /// </summary>
    public static string Clamp(string value, int maxCodePoints)
    {
        if (CodePointLength(value) <= maxCodePoints)
        {
            return value;
        }

        return TakeCodePoints(value, maxCodePoints - 1).TrimEnd() + Ellipsis;
    }

    /// <summary>
    /// The excerpt rule (§4.7, §8.7): the text as one line, cut at a word boundary to at most
    /// <paramref name="maxCodePoints"/>, with no ellipsis (the client marks an excerpt itself). A first word longer
    /// than the bound is cut on a code-point boundary instead.
    /// </summary>
    public static string Excerpt(string text, int maxCodePoints)
    {
        var line = CollapseToLine(text);
        if (CodePointLength(line) <= maxCodePoints)
        {
            return line;
        }

        var head = TakeCodePoints(line, maxCodePoints);
        // The cut lands inside a word unless the next character is a space.
        if (head.Length < line.Length && line[head.Length] == ' ')
        {
            return head.TrimEnd();
        }

        var lastSpace = head.LastIndexOf(' ');
        return lastSpace > 0 ? head[..lastSpace].TrimEnd() : head;
    }

    private static bool IsRemovedFormatCharacter(char c) =>
        c is >= '\u200B' and <= '\u200F'
            or >= '\u202A' and <= '\u202E'
            or >= '\u2060' and <= '\u2064'
            or >= '\u2066' and <= '\u2069'
            or '\u061C'
            or '\uFEFF'
            or >= '\uFFF9' and <= '\uFFFB';

    private static bool IsTagCharacter(int scalar) => scalar is >= 0xE0000 and <= 0xE007F;
}
