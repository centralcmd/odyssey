namespace Odyssey.Dtos.Application;

/// <summary>
/// Progress of the file move (issue #448 §5.4): how many files still hold their bytes in the database, why any could not
/// be moved, and how many sit on disk per area. Counts only — never an id, a name, a path or a volume.
/// <para>
/// The three counts of legacy files are <b>disjoint</b> and computed by the server, so a client subtracts nothing:
/// <see cref="Remaining"/> = <see cref="ToMove"/> + <see cref="WaitingOnPhotos"/> + <see cref="Failed"/>.
/// </para>
/// </summary>
public sealed record FileBackfillStatus
{
    /// <summary>Files whose bytes are still in the database: parked and photo-wrapped included.</summary>
    public int Remaining { get; set; }

    /// <summary>Legacy files neither parked nor wrapped by a legacy photo.</summary>
    public int ToMove { get; set; }

    /// <summary>Legacy files a not-yet-moved photo still wraps, not parked; the photo move handles them first.</summary>
    public int WaitingOnPhotos { get; set; }

    /// <summary>Parked files; not retried until an operator clears them. A parked, photo-wrapped file counts here only.</summary>
    public int Failed { get; set; }

    /// <summary>The total size of <see cref="Remaining"/>, so an operator can size the volumes before the move.</summary>
    public long RemainingBytes { get; set; }

    /// <summary>Files on the store per area, new uploads included. Placement counts, not access boundaries.</summary>
    public FileBackfillOnDisk OnDisk { get; set; } = new();

    /// <summary>Parked files per code: <c>hash</c>, <c>read</c>, <c>too-large</c>, <c>type</c>, <c>error</c> — every code present.</summary>
    public Dictionary<string, int> FailedByCode { get; set; } = [];

    /// <summary>Set while the job backs off after storage or database errors.</summary>
    public DateTime? BackingOffUntilUtc { get; set; }

    public DateTime? LastRunAtUtc { get; set; }
}

/// <summary>Files on the object store per area (issue #448 §5.4).</summary>
public sealed record FileBackfillOnDisk
{
    public int Finance { get; set; }

    public int Journal { get; set; }

    public int ContactAvatars { get; set; }
}
