using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

/// <summary>
/// A postal address belonging to a <see cref="Contact"/> (issue #325). n:1 to the parent, with
/// a <see cref="Label"/> and an application-enforced single <see cref="IsPrimary"/> per contact.
/// </summary>
[Index(nameof(ContactId))]
public class Address : IContactMethod
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public Guid ContactId { get; set; }

    public Contact Contact { get; set; } = null!;

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

    /// <summary>State / province / county.</summary>
    [StringLength(ContactMethodLimits.RegionMaxLength)]
    public string? Region { get; set; }

    /// <summary>Two-letter uppercase country code (not validated against a full ISO table in v1).</summary>
    [Required]
    [StringLength(ContactMethodLimits.CountryCodeLength)]
    public required string CountryCode { get; set; }
}
