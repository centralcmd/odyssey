using Odyssey.Dtos;
using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// Minimal, data-minimised projection of a contact linked to an insurance policy — as an insurer, an
/// insured contact or a beneficiary (issue #27 §10 #2; generalised from the single-insurer
/// <c>InsurerReference</c> of issue #175 §10 #4). Deliberately drops <c>OrganizationNumber</c>,
/// free-text notes, addresses, emails, phones and the person/organization sub-records, so a caller
/// holding only <c>insurance.read</c> cannot read the richer record gated by <c>contacts.read</c>.
/// </summary>
/// <remarks>
/// <see cref="Name"/> is <b>null</b> unless <see cref="Availability"/> is
/// <see cref="LinkAvailability.Available"/>: an archived or unresolvable link keeps its row and loses
/// its name (§9). <see cref="Type"/> is nullable because an <see cref="LinkAvailability.Unresolvable"/>
/// link has no contact row to read a type from, and <c>ContactType</c> has no zero member
/// (<c>Person = 1</c>, <c>Organization = 2</c>) — a non-nullable field would serialize as <c>0</c> and
/// map to no member at all.
/// </remarks>
public sealed record PolicyContactReference
{
    public required Guid ContactId { get; set; }

    /// <summary>The resolved display name — null for an archived or unresolvable link.</summary>
    [StringLength(128)]
    public string? Name { get; set; }

    /// <summary>The contact's type — null only when the link is unresolvable.</summary>
    public ContactType? Type { get; set; }

    public LinkAvailability Availability { get; set; }
}
