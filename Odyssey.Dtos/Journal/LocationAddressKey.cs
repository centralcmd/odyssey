using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Odyssey.Dtos.Journal;

/// <summary>
/// The address identity of a location (issue #380 §4): a lowercase hex SHA-256 over a positional string of
/// the six structured fields. Two addresses with the same key are the same place; the unique index on
/// <c>Locations.AddressKey</c> holds that, and the resolver and the one-time conversion both find a place
/// by it.
/// </summary>
/// <remarks>
/// <para>
/// The fields go in a fixed order — <c>CountryCode</c>, <c>PostalCode</c>, <c>City</c>, <c>Line1</c>,
/// <c>Line2</c>, <c>Region</c> — and each is (1) cleaned with <see cref="LocationTextCleaner.Clean"/>,
/// which removes U+001F so no value can forge a separator, (2) whitespace-collapsed to single spaces,
/// (3) empty → <c>""</c>, (4) upper-cased with <see cref="string.ToUpperInvariant"/>, and (5) joined with
/// U+001F. Case variants therefore share a key while letters are never folded: <c>Å</c>, <c>Ø</c> and
/// <c>Æ</c> stay distinct from <c>A</c>, <c>O</c> and <c>AE</c>. A null country is part of the key.
/// </para>
/// <para>
/// <see cref="Normalize"/> is pure BCL code so the WASM client can name the same helper to recognise a
/// case- or whitespace-only edit (v7); it must never grow a dependency.
/// </para>
/// </remarks>
public static partial class LocationAddressKey
{
    /// <summary>U+001F (INFORMATION SEPARATOR ONE / unit separator), the field separator.</summary>
    public const char Separator = '\u001F';

    /// <summary>The key is 64 lowercase hex characters (SHA-256).</summary>
    public const int Length = 64;

    /// <summary>
    /// Steps 1–2 for one field: cleaned and whitespace-collapsed, or <see langword="null"/> when nothing is
    /// left. This is also the comparison the edit-mode "what counts as changed" rule applies (§5.3, D5), and
    /// the form the structured fields are stored in.
    /// </summary>
    public static string? CleanField(string? value)
    {
        var cleaned = LocationTextCleaner.Clean(value);
        return cleaned is null ? null : Whitespace().Replace(cleaned, " ");
    }

    /// <summary>Steps 1–5: the positional, upper-cased, U+001F-joined string that is hashed.</summary>
    public static string Normalize(
        string? countryCode, string? postalCode, string? city, string? line1, string? line2, string? region) =>
        string.Join(
            Separator,
            Part(countryCode), Part(postalCode), Part(city), Part(line1), Part(line2), Part(region));

    /// <summary>
    /// The stored key, or <see langword="null"/> when <paramref name="line1"/> and <paramref name="city"/>
    /// are both empty after cleaning — such a partial address has no identity to deduplicate on.
    /// </summary>
    public static string? Compute(
        string? countryCode, string? postalCode, string? city, string? line1, string? line2, string? region)
    {
        if (CleanField(line1) is null && CleanField(city) is null)
        {
            return null;
        }

        return Hash(Normalize(countryCode, postalCode, city, line1, line2, region));
    }

    /// <summary>Lowercase hex SHA-256 of the UTF-8 bytes of <paramref name="normalized"/>.</summary>
    public static string Hash(string normalized) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));

    private static string Part(string? value) => CleanField(value)?.ToUpperInvariant() ?? string.Empty;

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
