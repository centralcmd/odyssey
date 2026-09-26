using Microsoft.EntityFrameworkCore;
using Odyssey.Dtos.Finance;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Odyssey.Context;

/// <summary>
/// A thing the household owns that holds value but generates no transactions — a house, a cabin, a car,
/// a boat (issue #167). Carries a <see cref="Type"/> and exactly one 1:1 detail sub-record sharing its
/// primary key, the <see cref="Contact"/>/<see cref="PersonDetails"/>/<see cref="OrganizationDetails"/>
/// shape.
///
/// <para>
/// Standalone in v1: no account type is retired and no data moves, so a property and an account of type
/// <c>Property</c>/<c>Vehicle</c> live side by side. Contract parties may name a property (issue #208),
/// and those links cascade with it — each removal evented on its contract — so a delete still has no
/// blocker; its detail row, estimates, smart-tag links and document links (issue #210) cascade too.
/// </para>
///
/// <para>
/// No <c>CreatedByUserId</c>/<c>UpdatedByUserId</c>: a property is shared household data like an
/// account, and accounts carry no attribution either. Adding one later is the FK-with-<c>SET NULL</c>
/// pattern <c>UserAttributionForeignKeyTests</c> pins.
/// </para>
/// </summary>
[Index(nameof(Archived))]
[Index(nameof(Type), nameof(Archived))]
public class Property
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid PropertyId { get; set; }

    [Required]
    [StringLength(256)]
    public required string Name { get; set; }

    [Required]
    [StringLength(256)]
    public required string Description { get; set; }

    /// <summary>Fixed at creation; <c>PropertyService.Update</c> refuses a change with a <c>422</c>.</summary>
    [Required]
    public PropertyType Type { get; set; }

    /// <summary>
    /// FK-free and validated against <c>Currencies</c> exactly as <c>Account.CurrencyCode</c> is. A
    /// property needs its own currency because its estimates are recorded in it and there is no parent
    /// account to inherit from.
    /// </summary>
    [Required]
    [StringLength(3)]
    public required string CurrencyCode { get; set; }

    public DateTime? AcquiredDate { get; set; }

    /// <summary>Sold, written off or scrapped. Must not precede <see cref="AcquiredDate"/>.</summary>
    public DateTime? DisposedDate { get; set; }

    [StringLength(1024)]
    public string? Notes { get; set; }

    public DateTime? Archived { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public RealEstateDetails? RealEstateDetails { get; set; }

    public VehicleDetails? VehicleDetails { get; set; }

    /// <summary>
    /// Load-bearing beyond navigation: <c>PropertyService.Delete</c> includes it so the cascade also
    /// happens under the EF InMemory provider, which enforces no foreign keys at all.
    /// </summary>
    public ICollection<PropertyEstimate> Estimates { get; set; } = new List<PropertyEstimate>();

    /// <summary>Included by <c>PropertyService.Delete</c> for the same reason as <see cref="Estimates"/>.</summary>
    public ICollection<PropertySmartTag> SmartTags { get; set; } = new List<PropertySmartTag>();

    /// <summary>
    /// The property's document links (issue #210). Included by <c>PropertyService.Delete</c> for the same
    /// reason as <see cref="Estimates"/>; the files themselves survive the delete.
    /// </summary>
    public ICollection<PropertyFile> Files { get; set; } = new List<PropertyFile>();
}
