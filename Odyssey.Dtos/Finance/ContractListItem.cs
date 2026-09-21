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

    /// <summary>
    /// The number of term (rate/fee) ROWS on the contract — never the number of in-force series
    /// (issue #135 §5). Counting rows is the same rule the insurance link counts follow: a display
    /// count resolved down to in-force values would make a busy price history look empty.
    /// </summary>
    public int TermCount { get; set; }

    /// <summary>
    /// The number of entries in the contract's event log (issue #138). Counted the same way
    /// <see cref="TermCount"/> is — one correlated subquery in the list read, never a second grouped
    /// query — so a page of 50 costs the same as a page of 1.
    ///
    /// <para>
    /// The collapsed card's counts strip is the body's table of contents, and the design system lists
    /// four entries in it: Parties · Terms · Documents · Events. A section present in the body and
    /// absent from the strip reads as a section that is empty.
    /// </para>
    /// </summary>
    public int EventCount { get; set; }

    public DateTime? Archived { get; set; }

    /// <summary>
    /// When the contract was paused, or null when it is not (issue #140). Orthogonal in storage to
    /// <see cref="Archived"/> and ordered only in presentation: a row may hold both, and
    /// <see cref="Status"/> decides which one is reported.
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

    /// <summary>
    /// Whether any term IN FORCE on this contract today is <c>Incoming</c> (issue #159). The collapsed
    /// row marks it, because "this file is money in" changes how the whole row reads and would
    /// otherwise only be visible once the record is expanded.
    ///
    /// <para>
    /// Resolved through the SAME series collapse the record card and the run rate use, so a superseded
    /// or future-dated incoming entry never marks a row that no longer takes money in. It is a flag
    /// rather than a count: the row says which way, and the body says how much.
    /// </para>
    /// </summary>
    public bool HasIncomingTerm { get; set; }
}
