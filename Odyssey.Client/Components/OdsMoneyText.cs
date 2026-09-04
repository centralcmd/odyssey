using System.Globalization;

namespace Odyssey.Client.Components;

/// <summary>
/// The two halves of editing a money value as text: what a keystroke is allowed to do to it, and how
/// the result becomes a number on submit.
/// </summary>
/// <remarks>
/// <para>
/// These live together, and in ONE place, because they are two ends of a single contract and a
/// disagreement between them is silent. <see cref="Sanitize"/> accepts a lone comma as a decimal
/// separator (a Norwegian user types <c>1234,56</c>), so a parser that strips commas as thousands
/// separators reads that as <c>123456</c> — a hundredfold error that fires no validation and shows no
/// error. Every money field in the client parses through <see cref="Parse"/> for exactly that reason.
/// </para>
/// <para>
/// Both are consumed by <c>OdsMoneyField</c> and by the file-analysis review grid, whose amount cell
/// is a bare input for width reasons but follows the same rules.
/// </para>
/// </remarks>
public static class OdsMoneyText
{
    /// <summary>
    /// Applies a keystroke to an amount. Characters that are never part of one are dropped; a
    /// keystroke that would make the value ambiguous is REJECTED outright (<c>null</c>) so the caller
    /// can put back what was there, rather than being rewritten into something the user didn't type.
    /// </summary>
    /// <param name="raw">The input's current text, straight from the DOM.</param>
    /// <param name="allowNegative">Whether a leading minus is meaningful for this field.</param>
    /// <returns>The text the field should now hold, or <c>null</c> to reject the keystroke.</returns>
    public static string? Sanitize(string? raw, bool allowNegative)
    {
        var kept = new string([.. (raw ?? string.Empty).Where(ch =>
            char.IsAsciiDigit(ch) || ch is '.' or ',' || char.IsWhiteSpace(ch) || (allowNegative && ch == '-'))]);

        // A second decimal separator is ambiguous.
        if (kept.Count(ch => ch is '.' or ',') > 1)
            return null;

        // A minus is only ever leading, and only ever one: testing the FIRST index alone would let a
        // second one through at index 0 ("--5"), which is how it reaches the field on an ordinary
        // double keypress.
        var minus = kept.IndexOf('-');
        if (minus > 0 || kept.LastIndexOf('-') != minus)
            return null;

        return kept;
    }

    /// <summary>
    /// Parses a sanitized amount on submit. A space is a group separator, a comma is a decimal point,
    /// and a trailing separator is a half-finished number rather than a malformed one — so
    /// <c>"1 250,"</c> reads as <c>1250</c> instead of falling to <c>null</c> and silently clearing
    /// the figure.
    /// </summary>
    /// <returns>The value, or <c>null</c> when there isn't one to read.</returns>
    public static decimal? Parse(string? text)
    {
        // Every kind of space goes, not just U+0020: a pasted figure can carry a non-breaking or a
        // narrow no-break space, which is what several locales group thousands with.
        var body = new string([.. (text ?? string.Empty)
            .Where(ch => !char.IsWhiteSpace(ch))
            .Select(ch => ch == ',' ? '.' : ch)])
            .TrimEnd('.');

        // Float, not Number: Number allows thousands separators, which would put the comma ambiguity
        // straight back by reading a decimal comma as a group separator.
        return decimal.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
