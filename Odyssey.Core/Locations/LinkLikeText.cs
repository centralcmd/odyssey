using System.Text.RegularExpressions;

namespace Odyssey.Core.Locations;

/// <summary>
/// Whether a free-text place looks like a meeting link or carries a credential (issue #380 §11, S-R3,
/// A-R12, O1, L4). Such a value is never turned into a location: a location is readable by every
/// <c>locations.read</c> holder, Guest included (decision 1), while the text it came from was readable only
/// under the event's or entry's own claim.
/// </summary>
/// <remarks>
/// A value is link-like when it contains <c>://</c>; starts with <c>www.</c>; contains a <c>host.tld/path</c>
/// token; is a bare host (<c>whereby.com</c>); or contains a credential or dial-in keyword as a <b>whole
/// word</b>, case-insensitively — so <c>Spinneriet</c>, <c>Camping</c> and <c>Koden</c> still convert (N1).
/// </remarks>
public static partial class LinkLikeText
{
    public static bool IsLinkLike(string value) =>
        value.Contains("://", StringComparison.Ordinal)
        || value.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
        || HostWithPath().IsMatch(value)
        || BareHost().IsMatch(value)
        || CredentialKeyword().IsMatch(value);

    [GeneratedRegex(@"[\w-]+(\.[\w-]+)+/\S*", RegexOptions.CultureInvariant)]
    private static partial Regex HostWithPath();

    [GeneratedRegex(@"^[\w-]+(\.[\w-]+)*\.[a-z]{2,}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BareHost();

    // \b is Unicode-aware in .NET, so `møte-id` is bounded on its letters like any other word.
    [GeneratedRegex(
        @"\b(passcode|password|pwd|pin|token|meeting\s+id|access\s+code|conference\s+id|dial-in|passord|kode|møte-id)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CredentialKeyword();
}
