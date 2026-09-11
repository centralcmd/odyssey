using System.Globalization;
using System.Text.RegularExpressions;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// The alias shape rules, mirrored client-side (Odyssey Design System · components/ContactAliases:
/// <c>canonicalAlias</c> / <c>aliasEquals</c> / the dialog's <c>submit</c> checks).
///
/// <para>
/// This is a mirror of <i>shape</i> rules, not a client-side copy of a server <b>cap</b> — the 32
/// limit is a compile-time constant shared through <see cref="ContactAliasRules"/> in
/// <c>Odyssey.Dtos</c>, which both halves of the stack reference, so there is one number and no
/// literal to drift. The UI still offers <b>Add alias</b> at the cap and lets the server's <c>422</c>
/// explain (issue #48 §14): the local check only saves a round trip on an obvious rejection, and the
/// server's message wins whenever it rejects.
/// </para>
/// </summary>
internal static class ContactAliasText
{
    private static readonly Regex MultiWhitespace = new("\\s+", RegexOptions.Compiled);

    /// <summary>Trim and collapse runs of whitespace — the canonical form the server stores.</summary>
    public static string Canonical(string? value) =>
        MultiWhitespace.Replace((value ?? string.Empty).Trim(), " ");

    /// <summary>
    /// Case- <b>and accent</b>-insensitive equality, matching what the service, the unique index and
    /// the EF InMemory tier all agree on. An <c>OrdinalIgnoreCase</c> comparison here would let the
    /// user submit <c>"Renee"</c> against a stored <c>"Renée"</c> and meet a server <c>409</c> the
    /// pre-check claimed could not happen.
    /// </summary>
    public static bool Equal(string? left, string? right) =>
        string.Compare(Canonical(left), Canonical(right), CultureInfo.CurrentCulture,
            CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) == 0;

    /// <summary>
    /// The message to render on the value field, or null when the draft passes the shape rules.
    /// Uniqueness is checked by the caller, which holds the sibling list; it is on the <b>value
    /// alone</b>, so two labels cannot smuggle in a second "Hansen".
    /// </summary>
    public static string? Validate(string value, string? label)
    {
        if (string.IsNullOrEmpty(value))
            return "Enter an alias.";
        if (value.Length > ContactAliasRules.MaxValueLength)
            return $"Keep the alias to {ContactAliasRules.MaxValueLength} characters or fewer.";
        if (value.Any(char.IsControl) || (label?.Any(char.IsControl) ?? false))
            return "Remove the line breaks and control characters.";
        if (label is { Length: > ContactAliasRules.MaxLabelLength })
            return $"Keep the label to {ContactAliasRules.MaxLabelLength} characters or fewer.";
        return null;
    }
}
