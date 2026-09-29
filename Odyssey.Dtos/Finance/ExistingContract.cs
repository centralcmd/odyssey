namespace Odyssey.Dtos.Finance;

public sealed record ExistingContract
{
    public required Guid ContractId { get; set; }

    public required string Name { get; set; }

    public ContractType Type { get; set; }

    public string? Description { get; set; }

    /// <summary>The number printed on the paperwork, or null when none is on file (issue #181).</summary>
    public string? ReferenceNumber { get; set; }

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
    /// per <c>Label</c>. An empty list is a healthy state — a contract with no recorded
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

    /// <summary>
    /// When the contract was marked ready for signature, or null when it has not been (issue #145).
    /// A stamp in the same shape as <see cref="Archived"/> and <see cref="Paused"/>; the derived
    /// <see cref="Status"/> reports one state, every stored stamp keeps its own field.
    /// </summary>
    public DateTime? Ready { get; set; }

    /// <summary>
    /// When the contract was signed by all parties, or null when it is unsigned (issue #145). An
    /// unsigned contract derives as <see cref="ContractStatus.Ready"/> or
    /// <see cref="ContractStatus.Draft"/> whatever its dates say, and contributes nothing to the run
    /// rate or the upcoming charges.
    /// </summary>
    public DateTime? Signed { get; set; }

    public required DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// Who added the record, as a display <b>label</b> resolved at the API edge — never the raw user
    /// id, which the response does not carry, the same rule <see cref="ExistingContractEvent.CreatedBy"/>
    /// follows. Null when no author is recorded: a contract created before attribution existed, or one
    /// whose author has been deleted. A null means "show no author", never "Unknown user".
    /// </summary>
    public string? CreatedBy { get; set; }
}
