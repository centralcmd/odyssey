using Odyssey.Dtos;
using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>Create/replace payload for a contact address (issue #325 §7).</summary>
public sealed record NewAddress
{
    [Required]
    [EnumDataType(typeof(AddressLabel))]
    public AddressLabel Label { get; set; }

    public bool IsPrimary { get; set; }

    [Required]
    [StringLength(ContactMethodLimits.AddressLineMaxLength)]
    public required string Line1 { get; set; }

    [StringLength(ContactMethodLimits.AddressLineMaxLength)]
    public string? Line2 { get; set; }

    [Required]
    [StringLength(ContactMethodLimits.CityMaxLength)]
    public required string City { get; set; }

    [StringLength(ContactMethodLimits.PostalCodeMaxLength)]
    public string? PostalCode { get; set; }

    [StringLength(ContactMethodLimits.RegionMaxLength)]
    public string? Region { get; set; }

    [Required]
    [StringLength(ContactMethodLimits.CountryCodeLength, MinimumLength = ContactMethodLimits.CountryCodeLength)]
    public required string CountryCode { get; set; }
}

/// <summary>Read projection of a contact address (issue #325 §7).</summary>
public sealed record ExistingAddress
{
    public required Guid Id { get; set; }
    public required Guid ContactId { get; set; }
    public AddressLabel Label { get; set; }
    public bool IsPrimary { get; set; }

    [StringLength(ContactMethodLimits.AddressLineMaxLength)]
    public required string Line1 { get; set; }

    [StringLength(ContactMethodLimits.AddressLineMaxLength)]
    public string? Line2 { get; set; }

    [StringLength(ContactMethodLimits.CityMaxLength)]
    public required string City { get; set; }

    [StringLength(ContactMethodLimits.PostalCodeMaxLength)]
    public string? PostalCode { get; set; }

    [StringLength(ContactMethodLimits.RegionMaxLength)]
    public string? Region { get; set; }

    [StringLength(ContactMethodLimits.CountryCodeLength)]
    public required string CountryCode { get; set; }
}
