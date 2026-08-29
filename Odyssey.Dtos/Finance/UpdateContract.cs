using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record UpdateContract
{
    [Required]
    [StringLength(256, MinimumLength = 1)]
    public required string Name { get; set; }

    [EnumDataType(typeof(ContractType))]
    public ContractType Type { get; set; } = ContractType.Other;

    [StringLength(1024)]
    public string? Description { get; set; }

    /// <summary>Start of a term contract (optional). Null when this is a one-off (see <see cref="CompletionDate"/>).</summary>
    public DateTime? StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    /// <summary>Completion date of a one-off contract; when set, term dates are ignored.</summary>
    public DateTime? CompletionDate { get; set; }

    /// <summary>
    /// Archive (retain but hide) the contract when true, or unarchive when false. Archiving keeps the
    /// contract and its parties/files but drops it from the default list; deletion (<c>DELETE</c>) is
    /// the separate, permanent operation. There is no dedicated archive/unarchive endpoint — the
    /// archive state is toggled here as part of the regular update (issue #174 §6/§7).
    /// </summary>
    public bool IsArchived { get; set; }
}
