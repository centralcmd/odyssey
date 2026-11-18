using Odyssey.Dtos;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

/// <summary>
/// A postal address belonging to a <see cref="Contact"/> (issue #325). n:1 to the parent, with
/// a <see cref="Label"/> and an application-enforced single <see cref="IsPrimary"/> per contact.
/// </summary>
/// <remarks>
/// Since issue #380 this is a <b>link row</b>: the address itself is the linked <see cref="Location"/>'s
/// structured fields, which every contact at that address shares, and the read path projects today's
/// address shape from it. A contact lists a location at most once (unique <c>(ContactId, LocationId)</c>,
/// which also serves the <c>ContactId</c> key).
/// </remarks>
[Index(nameof(ContactId), nameof(LocationId), IsUnique = true)]
public class Address : IContactMethod
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public Guid ContactId { get; set; }

    public Contact Contact { get; set; } = null!;

    public AddressLabel Label { get; set; }

    public bool IsPrimary { get; set; }

    /// <summary>The address, <c>RESTRICT</c>. Declared in <c>OdysseyContext.ConfigureLocationReferences</c>.</summary>
    public Guid LocationId { get; set; }

    public Location Location { get; set; } = null!;
}
