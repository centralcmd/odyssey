using System.Globalization;
using System.Text;

namespace Odyssey.Core.Journal;

/// <summary>
/// The one text normalization for location data (issue #350 §7.9, S7), applied to user input before it
/// is stored and to provider text before it is returned: Unicode NFC, then every C0/C1 control character
/// and every bidi override (U+202A–U+202E, U+2066–U+2069) removed, then trimmed. All-whitespace becomes
/// <see langword="null"/>.
/// </summary>
/// <remarks>
/// Only a free-text note keeps line breaks (<paramref name="allowLineBreaks"/>), normalized to <c>\n</c>.
/// A name or an address is one line, and a stray tab or newline in one is the same kind of hidden text a
/// bidi override is.
/// </remarks>
internal static class LocationText
{
    public static string? Clean(string? value, bool allowLineBreaks = false)
    {
        if (value is null)
        {
            return null;
        }

        var normalized = value.Normalize(NormalizationForm.FormC).Replace("\r\n", "\n", StringComparison.Ordinal);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (ch == '\n' && allowLineBreaks)
            {
                builder.Append(ch);
            }
            else if (char.GetUnicodeCategory(ch) != UnicodeCategory.Control && !IsBidiControl(ch))
            {
                builder.Append(ch);
            }
        }

        var cleaned = builder.ToString().Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>Cleans, then truncates to <paramref name="maxLength"/> (provider text only — never user input).</summary>
    public static string? CleanAndTruncate(string? value, int maxLength)
    {
        var cleaned = Clean(value);
        if (cleaned is null || cleaned.Length <= maxLength)
        {
            return cleaned;
        }

        // Never split a surrogate pair at the cut.
        var cut = char.IsHighSurrogate(cleaned[maxLength - 1]) ? maxLength - 1 : maxLength;
        return cleaned[..cut].TrimEnd();
    }

    private static bool IsBidiControl(char ch) =>
        ch is >= '‪' and <= '‮' or >= '⁦' and <= '⁩';
}
