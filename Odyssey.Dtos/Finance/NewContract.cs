using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record NewContract
{
    [Required]
    [StringLength(256, MinimumLength = 1)]
    public required string Name { get; set; }

    [EnumDataType(typeof(ContractType))]
    public ContractType Type { get; set; } = ContractType.Other;

    [StringLength(1024)]
    public string? Description { get; set; }

    /// <summary>
    /// Start of a term contract (optional — null is an open-started ongoing agreement). Leave this and
    /// <see cref="EndDate"/> null when <see cref="CompletionDate"/> is set (a one-off).
    /// </summary>
    public DateTime? StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    /// <summary>
    /// Completion date of a one-off contract — a point-in-time agreement with no ongoing term. When set,
    /// the contract is a one-off and <see cref="StartDate"/>/<see cref="EndDate"/> are ignored.
    /// </summary>
    public DateTime? CompletionDate { get; set; }
}
