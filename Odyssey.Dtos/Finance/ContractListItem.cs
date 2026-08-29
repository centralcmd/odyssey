namespace Odyssey.Dtos.Finance;

/// <summary>
/// Lean list projection (issue #174 §7): per-row scalars and counts only — no full parties[]/files[]
/// arrays — so the list endpoint stays a single batched query with no N+1.
/// </summary>
public sealed record ContractListItem
{
    public required Guid ContractId { get; set; }

    public required string Name { get; set; }

    public ContractType Type { get; set; }

    public string? Description { get; set; }

    public DateTime? StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    public DateTime? CompletionDate { get; set; }

    public ContractStatus Status { get; set; }

    /// <summary>
    /// Display-only name of the contract's primary institution (the first contact party), or null
    /// when none is linked. A minimal projection (name only) — the same data the detail party reference
    /// already exposes under <c>contracts.read</c>; no extra exposure (issue #174 §10 #2).
    /// </summary>
    public string? InstitutionName { get; set; }

    public int PartyCount { get; set; }

    public int FileCount { get; set; }

    public DateTime? Archived { get; set; }
}
