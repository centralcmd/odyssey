using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

[Index(nameof(Type), nameof(Archived))]
[Index(nameof(Archived))]
public class Contract
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid ContractId { get; set; }

    [StringLength(256)]
    [Required]
    public required string Name { get; set; }

    [Required]
    public ContractType Type { get; set; } = ContractType.Other;

    [StringLength(1024)]
    public string? Description { get; set; }

    /// <summary>
    /// Start of a <b>term</b> contract. Optional — an open-started ongoing agreement leaves it null.
    /// Mutually exclusive with <see cref="CompletionDate"/>: a contract is either term-based
    /// (<see cref="StartDate"/>/<see cref="EndDate"/>) or a one-off (<see cref="CompletionDate"/>).
    /// </summary>
    public DateTime? StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    /// <summary>
    /// Completion date of a <b>one-off</b> contract — a point-in-time agreement (a purchase / closing)
    /// with no ongoing term. Non-null marks the contract as one-off; <see cref="StartDate"/> and
    /// <see cref="EndDate"/> are then null (issue #174 §6).
    /// </summary>
    public DateTime? CompletionDate { get; set; }

    /// <summary>
    /// Archival timestamp — <b>not</b> a soft-delete marker. Non-null means the contract is archived
    /// (retained, hidden from the default list, fully restorable by clearing it); null means it is in
    /// the active set. Toggled through the regular update endpoint; permanent removal is the separate
    /// hard <c>DELETE</c> (issue #174 §6).
    /// </summary>
    public DateTime? Archived { get; set; }

    /// <summary>
    /// Pause timestamp (issue #140) — non-null means the contract is temporarily suspended, and the
    /// value records <b>when</b> it entered that state. Null is the default and the healthy steady
    /// state. The same shape as <see cref="Archived"/> and as <c>Subscription.Paused</c>, but the
    /// opposite intent: a paused contract stays listed, stays fully editable, and only stops
    /// contributing to what the file costs to run.
    ///
    /// <para>
    /// <b>Orthogonal in storage, ordered in presentation.</b> A row may legitimately hold both stamps;
    /// nothing clears one when the other is set, and the derived <c>ContractStatus</c> decides which
    /// is reported. No index: the derived status is computed in memory after projection, so this is
    /// never a SQL predicate.
    /// </para>
    /// </summary>
    public DateTime? Paused { get; set; }

    [Required]
    public required DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<ContractParty> Parties { get; set; } = new List<ContractParty>();
    public ICollection<ContractFile> Files { get; set; } = new List<ContractFile>();

    /// <summary>
    /// The contract's time-versioned price history (issue #135) — the mirror of <c>Account.Terms</c>.
    /// Load-bearing in two places, not cosmetic: <c>ContractService.Delete</c> includes it so the
    /// cascade also happens under the EF InMemory provider the fast test tiers run on, and
    /// <c>ContractService.ListAsync</c> projects <c>Terms.Count</c> as a correlated subquery in the
    /// one list query, which is what keeps <c>TermCount</c> free of an extra round trip.
    /// </summary>
    public ICollection<Term> Terms { get; set; } = new List<Term>();
}
