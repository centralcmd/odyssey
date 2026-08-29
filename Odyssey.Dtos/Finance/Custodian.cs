using Odyssey.Dtos;
using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// A slim, response-only projection of the contact that holds an account (its custodian).
/// Carries the identifying/display fields the custodian chip needs, but deliberately omits the
/// free-text <c>Description</c> the full <see cref="ExistingContact"/> exposes — that field stays
/// reachable only through <c>GET /api/contacts</c> (gated by <c>contacts.read</c>), so an
/// <c>accounts.read</c>-only principal cannot read it through the account path (data minimisation).
///
/// This is a computed read-only projection (like <see cref="ExistingAccount.Balance"/>), never
/// persisted and never accepted on a write — the account write DTO carries only the scalar
/// <see cref="ExistingAccount.CustodianId"/>.
/// </summary>
public sealed record Custodian
{
    public required Guid ContactId { get; set; }

    [StringLength(128)]
    public required string Name { get; set; }

    [StringLength(128)]
    public required string NormalizedName { get; set; }

    public ContactType Type { get; set; }

    [StringLength(64)]
    public string? OrganizationNumber { get; set; }

    public DateTime? Archived { get; set; }
}
