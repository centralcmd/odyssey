using System.Globalization;
using System.Text;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Locations;

/// <summary>
/// The pure naming rules of a location the application creates rather than a person typing it (issue #380
/// §4, §11): composing a name from address fields, truncating it so a suffix fits, allocating the next free
/// suffix, and the exact-letter name match. Shared by the one-time conversion and the address linker, and
/// covered by <c>Odyssey.Core.Tests</c>; nothing here touches a database.
/// </summary>
public static class LocationNaming
{
    /// <summary>
    /// The folding the <c>_ci</c> collation applies to <c>Locations.Name</c>'s unique index: case- and
    /// accent-insensitive, so <c>Asen</c> and <c>Åsen</c> collide there. Used only to decide which names are
    /// taken — never to decide that two places are the same (<see cref="ExactLetterKey"/> does that).
    /// The same rule <c>LocationService</c> pre-checks with.
    /// </summary>
    public static readonly StringComparer CollationComparer =
        CultureInfo.InvariantCulture.CompareInfo.GetStringComparer(CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);

    /// <summary>
    /// The address as one line: <c>Line1 Line2, PostalCode City, Region, CountryCode</c>, from the non-empty
    /// parts only, each cleaned and whitespace-collapsed. <see langword="null"/> when every part is empty.
    /// Untruncated — the caller fits it to the column.
    /// </summary>
    public static string? ComposeAddress(
        string? line1, string? line2, string? postalCode, string? city, string? region, string? countryCode)
    {
        var groups = new[]
        {
            JoinNonEmpty(" ", line1, line2),
            JoinNonEmpty(" ", postalCode, city),
            LocationAddressKey.CleanField(region),
            LocationAddressKey.CleanField(countryCode)?.ToUpperInvariant(),
        };

        return JoinNonEmpty(", ", groups);
    }

    /// <summary>
    /// <paramref name="value"/> cut to at most <paramref name="maxLength"/> characters, never splitting a
    /// surrogate pair, with trailing whitespace removed.
    /// </summary>
    public static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        var cut = maxLength > 0 && char.IsHighSurrogate(value[maxLength - 1]) ? maxLength - 1 : maxLength;
        return value[..cut].TrimEnd();
    }

    /// <summary><c>""</c> for the first name, then <c> (2)</c>, <c> (3)</c>, ….</summary>
    public static string Suffix(int ordinal) =>
        ordinal <= 1 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" ({ordinal})");

    /// <summary>
    /// The <paramref name="ordinal"/>-th candidate name for <paramref name="baseName"/>: the base truncated so
    /// that base plus suffix fits in <paramref name="maxLength"/> characters.
    /// </summary>
    public static string Candidate(string baseName, int ordinal, int maxLength = LocationRules.NameMaxLength)
    {
        var suffix = Suffix(ordinal);
        return Truncate(baseName, maxLength - suffix.Length) + suffix;
    }

    /// <summary>
    /// The first candidate for <paramref name="baseName"/> that <paramref name="isTaken"/> rejects —
    /// <paramref name="baseName"/> itself (truncated) when free, else the next free suffix. There is no retry
    /// cap: the taken set is finite, so a free ordinal always exists.
    /// </summary>
    public static string Allocate(string baseName, Func<string, bool> isTaken, int maxLength = LocationRules.NameMaxLength)
    {
        for (var ordinal = 1; ; ordinal++)
        {
            var candidate = Candidate(baseName, ordinal, maxLength);
            if (!isTaken(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// The key two names are compared on when deciding they name the same place (§7.1.9, S-R4): NFC, then
    /// <see cref="string.ToUpperInvariant"/>, compared ordinally. Case is folded; letters never are, so
    /// <c>Åsen</c> never matches <c>Asen</c>.
    /// </summary>
    public static string ExactLetterKey(string value) =>
        value.Normalize(NormalizationForm.FormC).ToUpperInvariant();

    public static bool IsExactLetterMatch(string left, string right) =>
        string.Equals(ExactLetterKey(left), ExactLetterKey(right), StringComparison.Ordinal);

    private static string? JoinNonEmpty(string separator, params string?[] parts)
    {
        var present = parts.Select(LocationAddressKey.CleanField).Where(part => part is not null).ToList();
        return present.Count == 0 ? null : string.Join(separator, present);
    }
}
