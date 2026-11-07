namespace Odyssey.Dtos.Application;

/// <summary>
/// Progress of the photo-original backfill (issue #428 §5.5): how many legacy photos still hold their bytes in the
/// database, and why any could not be moved. Counts only — never an id, a name or a path.
/// </summary>
public sealed record PhotoBackfillStatus
{
    /// <summary>Legacy photos not yet moved, parked ones included.</summary>
    public int Remaining { get; set; }

    /// <summary>Moved photos whose file was kept because something else still used it.</summary>
    public int CopiedShared { get; set; }

    /// <summary>Photos parked for a deterministic reason; not retried until an operator clears them.</summary>
    public int Failed { get; set; }

    /// <summary>Parked photos per failure code: <c>hash</c>, <c>read</c>, <c>too-large</c>.</summary>
    public Dictionary<string, int> FailedByCode { get; set; } = [];

    /// <summary>Set while the job backs off after storage or database errors.</summary>
    public DateTime? BackingOffUntilUtc { get; set; }

    public DateTime? LastRunAtUtc { get; set; }
}
