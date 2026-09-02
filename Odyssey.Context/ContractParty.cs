using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

/// <summary>
/// A party to a contract: a polymorphic link to exactly one of an <see cref="Account"/> or a
/// <see cref="Contact"/> (the #174 draft's "Institution"). The one-of-two (XOR) invariant is enforced
/// both in the service layer and by a database <c>CHECK</c> constraint declared on the model
/// (issue #174 §6). Both targets use <see cref="DeleteBehavior.Cascade"/>: deleting a linked account
/// or contact simply removes the party <em>link</em> row (the contract survives, just with one fewer
/// party). The whole row is removed, so the XOR invariant is preserved — unlike <c>SetNull</c>, which
/// would null the only target and leave an invalid party.
/// </summary>
/// <remarks>
/// A third target, <c>InsurancePolicyId</c>, existed until the design system reduced parties to
/// one-of-two. A contract naming a policy is expressed the other way round now — through the policy's
/// own party collections — so the column was dropped rather than left unused.
/// </remarks>
[Index(nameof(ContractId))]
[Index(nameof(AccountId))]
[Index(nameof(ContactId))]
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
}
