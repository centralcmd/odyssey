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

    /// <summary>
    /// Pause (temporarily suspend) the contract when true, or resume when false (issue #140). A pause
    /// suspends what the agreement costs — it leaves the run rate and the upcoming charges — without
    /// ending it, hiding it, locking it or touching its price history. Like <see cref="IsArchived"/>
    /// it rides the regular update; there is no dedicated pause endpoint.
    ///
    /// <para>
    /// Only a contract deriving as <c>Active</c> may be paused; anything else is refused with a
    /// <c>400</c> carrying <c>contract_pause_requires_active</c>. Clearing is never refused. Because
    /// <c>PUT</c> is a full replacement, an omitted <c>isPaused</c> reads as false and therefore
    /// <b>resumes</b> a paused contract — the same semantics <see cref="IsArchived"/> already has.
    /// </para>
    /// </summary>
    public bool IsPaused { get; set; }
}
