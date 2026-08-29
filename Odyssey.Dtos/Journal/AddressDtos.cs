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
    [StringLength(256)]
    public required string Line1 { get; set; }

    [StringLength(256)]
    public string? Line2 { get; set; }

    [Required]
    [StringLength(128)]
    public required string City { get; set; }

    [StringLength(32)]
    public string? PostalCode { get; set; }

    [StringLength(128)]
    public string? Region { get; set; }

    [Required]
    [StringLength(2, MinimumLength = 2)]
    public required string CountryCode { get; set; }
}

/// <summary>Read projection of a contact address (issue #325 §7).</summary>
public sealed record ExistingAddress
{
    public required Guid Id { get; set; }
    public required Guid ContactId { get; set; }
    public AddressLabel Label { get; set; }
    public bool IsPrimary { get; set; }

    [StringLength(256)]
    public required string Line1 { get; set; }

    [StringLength(256)]
    public string? Line2 { get; set; }

    [StringLength(128)]
    public required string City { get; set; }

    [StringLength(32)]
    public string? PostalCode { get; set; }

    [StringLength(128)]
    public string? Region { get; set; }

    [StringLength(2)]
    public required string CountryCode { get; set; }
}
