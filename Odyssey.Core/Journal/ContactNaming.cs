using Odyssey.Core.Finance;
using Odyssey.Dtos;
using System.Text.RegularExpressions;
using Odyssey.Context;
using Odyssey.Dtos.Finance;

namespace Odyssey.Core.Journal;

/// <summary>
/// Resolves a contact's display name (issue #325): the explicit <c>DisplayName</c> override,
/// else the type-appropriate fallback (<c>FirstName LastName</c> for a Person, <c>LegalName</c> for an
/// Organization), collapsed/trimmed and truncated to <see cref="MaxLength"/>. Used both to build the
/// stored <c>NormalizedName</c> and to project a display name for other read paths (custodian,
/// subscription/insurer/contract references, transaction search/sort).
/// </summary>
public static class ContactNaming
{
    public const int MaxLength = 256;

    private static readonly Regex MultiWhitespaceRegex = new("\\s+", RegexOptions.Compiled);

    /// <summary>
    /// The resolved display value for an entity whose <see cref="Contact.PersonDetails"/>/
    /// <see cref="Contact.OrganizationDetails"/> navigations are loaded.
    /// </summary>
    public static string Resolve(Contact contact)
    {
        var raw = !string.IsNullOrWhiteSpace(contact.DisplayName)
            ? contact.DisplayName!
            : contact.Type == ContactType.Person
                ? $"{contact.PersonDetails?.FirstName} {contact.PersonDetails?.LastName}"
                : contact.OrganizationDetails?.LegalName ?? string.Empty;

        var collapsed = MultiWhitespaceRegex.Replace(raw.Trim(), " ");
        return collapsed.Length > MaxLength ? collapsed[..MaxLength] : collapsed;
    }

    /// <summary>The uppercased, whitespace-collapsed search/sort key derived from a resolved value.</summary>
    public static string Normalize(string resolved) =>
        MultiWhitespaceRegex.Replace(resolved.Trim(), " ").ToUpperInvariant();
}
