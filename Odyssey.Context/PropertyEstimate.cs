using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Odyssey.Context;

/// <summary>
/// A time-versioned estimated value for a <see cref="Property"/> (issue #167), mirroring
/// <see cref="AccountEstimate"/> field for field. There is no explicit end date: the value in force on a
/// date is the entry with the greatest <see cref="EffectiveFrom"/> on or before it.
///
/// <para>
/// A <b>sibling table</b> of <see cref="AccountEstimate"/>, not a nullable second owner added to it. The
/// codebase has removed polymorphic ownership from both places it had it (<see cref="ContractSmartTag"/>
/// declined it for smart tags; issue #190 undid it for <see cref="Term"/>), so a new owner gets its own
/// table. The two query-shaped rules both tables obey — the duplicate-date conflict and the
/// current-as-of resolution — live once, in <c>EstimateEffectiveDating</c>, so the siblings cannot
/// disagree about which entry is current.
/// </para>
///
/// <para>
/// The index is <b>non-unique</b>, matching <see cref="AccountEstimate"/>: the duplicate-date rule is a
/// service pre-check on both tables, and closing its check-then-act race belongs to a cross-cutting
/// change covering both owners at once.
/// </para>
/// </summary>
[Index(nameof(PropertyId), nameof(EffectiveFrom))]
public class PropertyEstimate : IEffectiveDated
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid PropertyEstimateId { get; set; }

    [Required]
    public Guid PropertyId { get; set; }

    public Property Property { get; set; } = null!;

    [Required]
    [Precision(18, 6)]
    public decimal Value { get; set; }

    // Retained for storage simplicity, but always equal to the property currency (enforced by the API).
    [StringLength(3)]
    public string? CurrencyCode { get; set; }

    [Required]
    public DateTime EffectiveFrom { get; set; }

    [StringLength(512)]
    public string? Note { get; set; }

    [Required]
    public DateTime CreatedAtUtc { get; set; }
}
