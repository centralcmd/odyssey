using System.Globalization;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// The one shape rule for a <see cref="TermValueUnit.Text"/> term's value (issue #192 §8 rule 5),
/// shared by the server's write path and the client's dialog. It lives in <c>Odyssey.Dtos</c> for the
/// same reason <see cref="TermLabel"/> does: both halves of the stack run the <em>same</em> delegate
/// rather than two implementations that can drift.
/// </summary>
public static class TermTextValue
{
    /// <summary>The maximum stored length, counted after the trim. The entity and <c>NewTerm</c> name it.</summary>
    public const int MaxLength = 256;

    /// <summary>The stored form: trimmed at both ends. A null or whitespace-only value normalizes to <c>null</c>.</summary>
    public static string? Normalize(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    /// <summary>
    /// Whether <paramref name="text"/> holds a character a one-line plain value may not: a Unicode
    /// control character (category <c>Cc</c>, so CR, LF, TAB and NUL), or a bidi embedding, override or
    /// isolate (U+202A–U+202E, U+2066–U+2069), which could make the stored text read differently from
    /// what it says.
    /// </summary>
    public static bool HasForbiddenCharacter(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        foreach (var c in text)
        {
            if (char.GetUnicodeCategory(c) == UnicodeCategory.Control
                || c is >= '‪' and <= '‮'
                || c is >= '⁦' and <= '⁩')
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="raw"/> is a storable Text value: non-empty and at most
    /// <see cref="MaxLength"/> characters after the trim, with no forbidden character.
    /// </summary>
    public static bool IsValid(string? raw) =>
        Normalize(raw) is { } text && text.Length <= MaxLength && !HasForbiddenCharacter(text);
}
