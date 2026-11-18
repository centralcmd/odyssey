namespace Odyssey.Dtos.Journal;

/// <summary>
/// Column widths of a contact's addresses, email addresses and phone numbers, declared once (issue #287
/// M7). The entity's <c>[StringLength]</c>, the request DTO's <c>[StringLength]</c> and the service's
/// defence-in-depth check for non-HTTP callers all name these, so the three cannot drift apart.
/// </summary>
public static class ContactMethodLimits
{
    // A contact address is a link to a location since issue #380, so its widths are the location's.
    public const int AddressLineMaxLength = LocationRules.AddressLineMaxLength;
    public const int CityMaxLength = LocationRules.CityMaxLength;
    public const int PostalCodeMaxLength = LocationRules.PostalCodeMaxLength;
    public const int RegionMaxLength = LocationRules.RegionMaxLength;
    public const int CountryCodeLength = LocationRules.CountryCodeLength;
    public const int EmailMaxLength = 256;
    public const int PhoneMaxLength = 32;
}
