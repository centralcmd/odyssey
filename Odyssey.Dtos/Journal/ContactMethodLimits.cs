namespace Odyssey.Dtos.Journal;

/// <summary>
/// Column widths of a contact's addresses, email addresses and phone numbers, declared once (issue #287
/// M7). The entity's <c>[StringLength]</c>, the request DTO's <c>[StringLength]</c> and the service's
/// defence-in-depth check for non-HTTP callers all name these, so the three cannot drift apart.
/// </summary>
public static class ContactMethodLimits
{
    public const int AddressLineMaxLength = 256;
    public const int CityMaxLength = 128;
    public const int PostalCodeMaxLength = 32;
    public const int RegionMaxLength = 128;
    public const int CountryCodeLength = 2;
    public const int EmailMaxLength = 256;
    public const int PhoneMaxLength = 32;
}
