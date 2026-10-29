using System.Text.Json.Serialization;

namespace Odyssey.Dtos.Application;

/// <summary>How a file volume looks to the process answering the request (issue #346 §5).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FileStorageVolumeState>))]
public enum FileStorageVolumeState
{
    /// <summary>Verified at startup and answering.</summary>
    Available,

    /// <summary>An I/O failure not attributable to one object; reads and writes return 503 until it recovers.</summary>
    Degraded,

    /// <summary>A volume the database knows about, holding no live objects, that is no longer configured.</summary>
    NotConfigured,
}

/// <summary>
/// One volume's state, usage and bindings, from <c>GET /api/file-storage/volumes</c> (issue #346 §5,
/// amended by A3.1 and A3.2). Never carries a root path, a relative path, a file name or an object id.
/// </summary>
public sealed record FileStorageVolumeStatus
{
    /// <summary>The configuration name, lowercase.</summary>
    public string Name { get; set; } = string.Empty;

    public FileStorageVolumeState State { get; set; }

    public DateTime? InitializedAtUtc { get; set; }

    /// <summary>The catalogue features whose new writes go to this volume, as canonical keys.</summary>
    public IReadOnlyList<string> BoundFeatures { get; set; } = [];

    /// <summary>Every feature with live objects on this volume, including one rebound away.</summary>
    public IReadOnlyList<FileStorageFeatureUsage> Features { get; set; } = [];

    public int PendingDeleteCount { get; set; }

    public int IntegrityFaultCount { get; set; }

    /// <summary>Free space on the volume's filesystem; null when it cannot be read (or it is not configured).</summary>
    public long? FreeBytes { get; set; }

    public long? TotalBytes { get; set; }

    /// <summary>The most recent integrity check of any object on the volume.</summary>
    public DateTime? LastScrubAtUtc { get; set; }

    public int QuarantinedCount { get; set; }

    public int? LastPassOrphanCount { get; set; }

    public bool ReconciliationHalted { get; set; }

    /// <summary>Feature keys with live objects on this volume that this build's catalogue does not know.</summary>
    public IReadOnlyList<string> OrphanedFeatures { get; set; } = [];

    /// <summary>
    /// When the instance's maintenance job last completed a full pass. Instance-wide: every entry carries the
    /// same value (A3.2).
    /// </summary>
    public DateTime? MaintenanceLastCompletedAtUtc { get; set; }

    /// <summary>
    /// True when no maintenance pass has completed for over three hours — erasure depends on it. Instance-wide,
    /// like <see cref="MaintenanceLastCompletedAtUtc"/>.
    /// </summary>
    public bool MaintenanceStale { get; set; }
}

/// <summary>One feature's committed objects on one volume.</summary>
public sealed record FileStorageFeatureUsage
{
    /// <summary>The canonical PascalCase catalogue key — a code identifier, not user data.</summary>
    public string FeatureKey { get; set; } = string.Empty;

    public int ObjectCount { get; set; }

    public long CommittedBytes { get; set; }
}
