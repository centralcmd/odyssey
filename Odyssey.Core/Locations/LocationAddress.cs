using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Locations;

/// <summary>
/// The six structured address fields of a location, in their stored form: each cleaned and
/// whitespace-collapsed (<see cref="LocationAddressKey.CleanField"/>), the country upper-cased. Build one
/// with <see cref="Of"/> so the stored fields, the key and the composed name all read the same values.
/// </summary>
public sealed record LocationAddress(
    string? Line1, string? Line2, string? PostalCode, string? City, string? Region, string? CountryCode)
{
    public static LocationAddress Of(
        string? line1, string? line2, string? postalCode, string? city, string? region, string? countryCode) =>
        new(
            LocationAddressKey.CleanField(line1),
            LocationAddressKey.CleanField(line2),
            LocationAddressKey.CleanField(postalCode),
            LocationAddressKey.CleanField(city),
            LocationAddressKey.CleanField(region),
            LocationAddressKey.CleanField(countryCode)?.ToUpperInvariant());

    /// <summary>The six stored fields of <paramref name="location"/>, read back through the same cleaning.</summary>
    public static LocationAddress FromLocation(Odyssey.Context.Location location) =>
        Of(location.Line1, location.Line2, location.PostalCode, location.City, location.Region, location.CountryCode);

    /// <summary>
    /// The first of <c>line1</c>, <c>city</c>, <c>countryCode</c> that is empty — the wire key of the
    /// completeness <c>422</c> (issue #380 §5.3, V7-2) — or <see langword="null"/> when the address is
    /// complete enough to back a contact or property link.
    /// </summary>
    public string? FirstMissingRequiredField =>
        Line1 is null ? "line1" : City is null ? "city" : CountryCode is null ? "countryCode" : null;

    public bool IsEmpty =>
        Line1 is null && Line2 is null && PostalCode is null && City is null && Region is null && CountryCode is null;

    /// <summary>The address key, or <see langword="null"/> when <see cref="Line1"/> and <see cref="City"/> are both empty.</summary>
    public string? Key => LocationAddressKey.Compute(CountryCode, PostalCode, City, Line1, Line2, Region);

    /// <summary>The composed one-line address (<see cref="LocationNaming.ComposeAddress"/>), untruncated.</summary>
    public string? Composed => LocationNaming.ComposeAddress(Line1, Line2, PostalCode, City, Region, CountryCode);

    /// <summary>
    /// Redacted: an address is personal data, and a record prints its members wherever it is logged or
    /// lands in an exception message.
    /// </summary>
    public override string ToString() => nameof(LocationAddress) + " { (redacted) }";
}
