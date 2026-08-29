using Odyssey.Dtos;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

/// <summary>
/// A postal address belonging to a <see cref="Contact"/> (issue #325). n:1 to the parent, with
/// a <see cref="Label"/> and an application-enforced single <see cref="IsPrimary"/> per contact.
/// </summary>
[Index(nameof(ContactId))]
public class Address
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public Guid ContactId { get; set; }

    public Contact Contact { get; set; } = null!;

    public AddressLabel Label { get; set; }

    public bool IsPrimary { get; set; }

    [Required]
    [StringLength(256)]
    public required string Line1 { get; set; }

    [StringLength(256)]
    public string? Line2 { get; set; }

    [Required]
    [StringLength(128)]
    public required string City { get; set; }

    [StringLength(32)]
    public string? PostalCode { get; set; }

    /// <summary>State / province / county.</summary>
    [StringLength(128)]
    public string? Region { get; set; }

    /// <summary>Two-letter uppercase country code (not validated against a full ISO table in v1).</summary>
    [Required]
    [StringLength(2)]
    public required string CountryCode { get; set; }
}
