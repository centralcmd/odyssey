using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// A slim, response-only projection of the contact a real-estate property names as its homeowner
/// association (issue #217). It crosses from the contact domain into a <c>properties.read</c> response,
/// so it carries three members and nothing else — no organization number, notes, addresses, emails,
/// phones, aliases or avatar. <see cref="ExistingContact"/> is deliberately not reused.
///
/// <para>
/// Never accepted on a write: the request carries only
/// <see cref="RealEstateDetailsDto.HomeownerAssociationId"/>.
/// </para>
/// </summary>
public sealed record PropertyHomeownerAssociation
{
    public required Guid ContactId { get; set; }

    /// <summary>The contact's resolved display name.</summary>
    [StringLength(256)]
    public required string Name { get; set; }

    /// <summary>Set when the contact has been archived since it was linked, so a client can flag the stale link.</summary>
    public DateTime? Archived { get; set; }
}
