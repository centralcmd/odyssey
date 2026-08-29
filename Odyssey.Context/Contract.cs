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

    [Required]
    public required DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<ContractParty> Parties { get; set; } = new List<ContractParty>();
    public ICollection<ContractFile> Files { get; set; } = new List<ContractFile>();
}
