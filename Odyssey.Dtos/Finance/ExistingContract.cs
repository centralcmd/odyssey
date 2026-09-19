namespace Odyssey.Dtos.Finance;

public sealed record ExistingContract
{
    public required Guid ContractId { get; set; }

    public required string Name { get; set; }

    public ContractType Type { get; set; }

    public string? Description { get; set; }

    public DateTime? StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    /// <summary>Completion date of a one-off contract; null for term contracts (issue #174 §6).</summary>
    public DateTime? CompletionDate { get; set; }

    /// <summary>Derived, never stored (issue #174 §6).</summary>
    public ContractStatus Status { get; set; }

    public List<ExistingContractParty> Parties { get; set; } = new();

    public List<ExistingContractFile> Files { get; set; } = new();

    /// <summary>
    /// The in-force entry of each of the contract's term series, as of today (issue #135). At most one
    /// per <c>(TermKind, Label)</c>. An empty list is a healthy state — a contract with no recorded
    /// price is not a defect. Reuses <see cref="AccountCurrentTerm"/> verbatim: that projection
    /// carries no owner id, so it is already owner-agnostic.
    /// </summary>
    public List<AccountCurrentTerm> CurrentTerms { get; set; } = new();

    public DateTime? Archived { get; set; }

    /// <summary>
    /// When the contract was paused, or null when it is not (issue #140). Retained across an archive
    /// and across an expiry — the derived <see cref="Status"/> reports the terminal fact, the stamp
    /// stays on file so resuming is one write.
    /// </summary>
    public DateTime? Paused { get; set; }

    public required DateTime CreatedAtUtc { get; set; }
}
