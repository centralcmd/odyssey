using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

/// <summary>
/// A party to a contract: a polymorphic link to exactly one of an <see cref="Account"/>, a
/// <see cref="Contact"/> (the #174 draft's "Institution") or a <see cref="Property"/> (issue #208). The
/// one-of-three invariant is enforced both in the service layer and by a database <c>CHECK</c>
/// constraint declared on the model (issue #174 §6). Every target uses
/// <see cref="DeleteBehavior.Cascade"/>: deleting a linked account, contact or property simply removes
/// the party <em>link</em> row (the contract survives, just with one fewer party). The whole row is
/// removed, so the invariant is preserved — unlike <c>SetNull</c>, which would null the only target
/// and leave an invalid party.
/// </summary>
/// <remarks>
/// An earlier third target, <c>InsurancePolicyId</c>, existed until the design system reduced parties
/// to one-of-two, and was dropped rather than left unused. <c>PropertyId</c> is a new column, not that
/// one revived, and its wire kind takes a fresh ordinal (<c>ContractPartyKind.Property = 3</c>).
/// </remarks>
/// <remarks>
/// The three composite indexes are the real arbiter of the <i>(contract, target, role)</i> uniqueness
/// rule (issue #121 §8 rule 6); <c>ContractService.EnsureNotDuplicateParty</c> keeps its pre-check for
/// the explaining message and because the EF InMemory tiers enforce no indexes at all. MariaDB treats
/// <c>NULL</c> as distinct in a unique index, so the account-side index does not constrain contact
/// parties (whose <c>AccountId</c> is null) and vice versa — which is what lets one table carry three
/// uniqueness rules without a discriminator column. The single-column indexes are all kept: the
/// composites lead with <c>ContractId</c>, so a lookup by target alone (the contact-deletion blockers,
/// the property Contracts section and its count) still needs its own.
/// </remarks>
[Index(nameof(ContractId))]
[Index(nameof(AccountId))]
[Index(nameof(ContactId))]
[Index(nameof(PropertyId))]
[Index(nameof(ContractId), nameof(AccountId), nameof(Role), IsUnique = true)]
[Index(nameof(ContractId), nameof(ContactId), nameof(Role), IsUnique = true)]
[Index(nameof(ContractId), nameof(PropertyId), nameof(Role), IsUnique = true)]
public class ContractParty
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid ContractPartyId { get; set; }

    [Required]
    public required Guid ContractId { get; set; }

    [ForeignKey(nameof(ContractId))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public Contract? Contract { get; set; }

    public Guid? AccountId { get; set; }

    [ForeignKey(nameof(AccountId))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public Account? Account { get; set; }

    // A real FK to Contact with ON DELETE CASCADE, declared in OdysseyContext — a party row is its link
    // to the counterparty, so it dies with the contact. Validated on write via IContactLookup.
    public Guid? ContactId { get; set; }

    /// <summary>
    /// The property this party names (issue #208). <c>PropertyService.Delete</c> removes these rows by
    /// hand as well, so the cascade — and its audit trail — also happens under the EF InMemory provider.
    /// </summary>
    public Guid? PropertyId { get; set; }

    [ForeignKey(nameof(PropertyId))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public Property? Property { get; set; }

    /// <summary>
    /// What the linked record does in the agreement (issue #121). Required on the wire as well as in
    /// the column since issue #157 §8.1: <c>Unspecified</c> — the value every pre-#121 row was
    /// backfilled to, and what a role-less write used to resolve to — is retired, so there is nothing
    /// left for an omitted role to mean.
    /// </summary>
    /// <remarks>
    /// Orthogonal to which target column is set, but <b>not</b> to the contract's <c>Type</c>: which
    /// roles are legal is decided by <c>Odyssey.Dtos.Finance.ContractPartyRoleMatrix</c>, enforced in
    /// <c>ContractService</c> on every party write and on a contract type change. The column itself
    /// carries no such constraint — legality depends on a row in another table, which is a service
    /// rule rather than a database one.
    ///
    /// <para>
    /// <b>The <c>Beneficiary</c> role blocks deletion of its contact</b> (issue #157 §7.4). The
    /// <c>Contact</c> FK below stays <c>CASCADE</c> for every role — the other seventeen should keep
    /// cascading — so that one rule lives entirely in <c>IContactReferenceGuard</c> with no constraint
    /// behind it.
    /// </para>
    /// </remarks>
    [Required]
    public ContractPartyRole Role { get; set; }

    /// <summary>
    /// When the party entered the role. <see langword="null"/> is the <b>default term</b> — the
    /// contract's own extent — not an unset value, so a party added with the defaults follows the
    /// contract for its whole lifetime and a later extension never re-dates it.
    /// </summary>
    public DateTime? FromDate { get; set; }

    /// <summary>When the party left the role. <see langword="null"/> means it is still in the role.</summary>
    public DateTime? ToDate { get; set; }
}
