using System.Globalization;
using System.Text;

namespace Odyssey.Dtos.Journal;

/// <summary>
/// The one text normalization for location data (issue #350 §7.9, S7), applied to user input before it
/// is stored and to provider text before it is returned: Unicode NFC, then every C0/C1 control character
/// (U+001F, the address-key separator, included) and every bidi override (U+202A–U+202E, U+2066–U+2069)
/// removed, then trimmed. All-whitespace becomes <see langword="null"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the <b>single</b> implementation (issue #380 §4, V7-1). It lives here, beside
/// <see cref="LocationRules.NormalizeQuery"/>, because <see cref="LocationAddressKey"/> needs it and the
/// WASM client computes the same key; <c>Odyssey.Core</c>'s <c>LocationText</c> delegates to it. The name is
/// distinct on purpose: two types called <c>LocationText</c> in nested namespaces would shadow silently.
/// A source-lint in <c>Odyssey.Core.Tests</c> fails on a second copy.
/// </para>
/// <para>
/// Only a free-text note keeps line breaks (<paramref name="allowLineBreaks"/>), normalized to <c>\n</c>.
/// A name or an address is one line, and a stray tab or newline in one is the same kind of hidden text a
/// bidi override is.
/// </para>
/// </remarks>
public static class LocationTextCleaner
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

    private static bool IsBidiControl(char ch) =>
        ch is >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069';
}
