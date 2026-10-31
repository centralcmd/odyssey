using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Odyssey.Core.FileStorage;

/// <summary>
/// <c>FileStorage:Volumes</c> and <c>FileStorage:Bindings</c> (issue #346 §3.2). Deploy-time configuration, not
/// settings: an admin-editable path would be an arbitrary filesystem read/write primitive. Shares the
/// <c>FileStorage</c> section with <c>Odyssey.Core.Finance.FileStorageOptions</c>, which is left where it is.
/// </summary>
public sealed class FileStorageVolumesOptions
{
    public const string SectionName = "FileStorage";

    /// <summary>Volume name → its root. Env form: <c>FileStorage__Volumes__primary__RootPath</c>.</summary>
    public Dictionary<string, FileStorageVolumeOptions> Volumes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Feature key → volume name. Env form: <c>FileStorage__Bindings__PhotoThumbnails</c>.</summary>
    public Dictionary<string, string> Bindings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class FileStorageVolumeOptions
{
    /// <summary>An absolute path to an existing directory. The verifier never creates a root.</summary>
    public string? RootPath { get; set; }
}

/// <summary>
/// One verified volume in this process. <see cref="Degraded"/> is in-memory and per process (§3.8, A3.3): each
/// host keeps its own, and a restart re-runs the startup verifier instead.
/// </summary>
public sealed class ActiveVolume
{
    private volatile bool degraded;

    internal ActiveVolume(Guid id, string name, string rootPath)
    {
        Id = id;
        Name = name;
        RootPath = rootPath;
    }

    public Guid Id { get; }

    /// <summary>Lowercase.</summary>
    public string Name { get; }

    /// <summary>The configured root as a full path, without a trailing separator. Never logged outside startup.</summary>
    public string RootPath { get; }

    public bool Degraded => degraded;

    /// <summary>When this process last probed the volume; a degraded volume is re-probed at most every 15 seconds.</summary>
    internal DateTimeOffset LastProbeAt { get; set; } = DateTimeOffset.MinValue;

    internal void SetDegraded(bool value) => degraded = value;
}

/// <summary>
/// The volumes this process verified at startup, the feature bindings, and each volume's runtime health
/// (issue #346 §3.1 item 3, §3.8). Inactive until the startup verifier activates it; while inactive — the
/// no-op rule — every write is refused with <c>503</c>.
/// </summary>
public sealed class FileStorageVolumeRegistry(ILogger<FileStorageVolumeRegistry> logger, TimeProvider time)
{
    /// <summary>
    /// How often a request may re-probe a degraded volume. Writes are refused while a volume is degraded, so
    /// without this the API's flag could only clear on a restart; with it, the volume recovers on its own once
    /// the disk answers, and a burst of requests costs one probe.
    /// </summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<string, ActiveVolume> byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, ActiveVolume> byId = new();
    private IReadOnlyDictionary<string, string> bindings = new Dictionary<string, string>();
    private volatile bool active;

    /// <summary>False under the no-op rule, and before verification has run.</summary>
    public bool IsActive => active;

    /// <summary>Every verified volume, ordered by name.</summary>
    public IReadOnlyList<ActiveVolume> Volumes => [.. byName.Values.OrderBy(v => v.Name, StringComparer.Ordinal)];

    /// <summary>Feature key → volume name, as configured.</summary>
    public IReadOnlyDictionary<string, string> Bindings => bindings;

    /// <summary>Called once by the startup verifier.</summary>
    public void Activate(IEnumerable<(Guid Id, string Name, string RootPath)> volumes, IReadOnlyDictionary<string, string> featureBindings)
    {
        foreach (var (id, name, rootPath) in volumes)
        {
            var volume = new ActiveVolume(id, name, rootPath);
            byName[name] = volume;
            byId[id] = volume;
        }

        bindings = new Dictionary<string, string>(featureBindings, StringComparer.OrdinalIgnoreCase);
        active = true;
    }

    public ActiveVolume? Find(Guid id) => byId.GetValueOrDefault(id);

    public ActiveVolume? Find(string name) => byName.GetValueOrDefault(name);

    /// <summary>The volume new writes of <paramref name="feature"/> go to; a rebinding affects new writes only.</summary>
    public ActiveVolume ForWrite(StorageFeature feature)
    {
        if (!active)
        {
            throw new FileStorageUnavailableException("File storage is not set up on this server.");
        }

        if (!bindings.TryGetValue(feature.Key, out var volumeName) || Find(volumeName) is not { } volume)
        {
            throw new FileStorageUnavailableException("File storage for this feature is not configured.");
        }

        return EnsureAvailable(volume);
    }

    /// <summary>The volume an existing object lives on.</summary>
    public ActiveVolume ForExisting(Guid volumeId)
    {
        if (!active || Find(volumeId) is not { } volume)
        {
            throw new FileStorageUnavailableException("The file volume holding this file is not available.");
        }

        return EnsureAvailable(volume);
    }

    /// <summary>
    /// An I/O failure not attributable to one object (§3.8). A single missing object file is an integrity fault,
    /// never this.
    /// </summary>
    public void MarkDegraded(ActiveVolume volume, Exception cause)
    {
        if (!volume.Degraded)
        {
            logger.LogError("File volume {Volume} is degraded; its reads and writes return 503 until it recovers ({Failure}).", volume.Name, FileStorageLog.Describe(cause));
            volume.LastProbeAt = time.GetUtcNow();
        }

        volume.SetDegraded(true);
    }

    public void MarkAvailable(ActiveVolume volume)
    {
        if (volume.Degraded)
        {
            logger.LogInformation("File volume {Volume} is available again.", volume.Name);
        }

        volume.SetDegraded(false);
    }

    /// <summary>
    /// The degraded-volume probe (§3.7, A2.2): a probe write and delete in the root. Success clears
    /// <c>Degraded</c>; failure leaves it set. A consumer whose health gate stops it writing calls this to recover
    /// within its own tick.
    /// </summary>
    public Task<bool> ProbeAsync(ActiveVolume volume, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        volume.LastProbeAt = time.GetUtcNow();
        Exception? failure = null;
        if (HasMarker(volume, out var missing) && VolumeFileSystem.TryProbeWrite(volume.RootPath, out failure))
        {
            MarkAvailable(volume);
            return Task.FromResult(true);
        }

        MarkDegraded(volume, failure ?? missing!);
        return Task.FromResult(false);
    }

    /// <summary>
    /// Refuses a write to a volume whose marker has gone — an unmounted disk leaves an empty mount point, and a
    /// write there would land on the container's ephemeral layer. The volume becomes <c>Degraded</c>.
    /// </summary>
    public void EnsureMounted(ActiveVolume volume)
    {
        if (!HasMarker(volume, out var missing))
        {
            MarkDegraded(volume, missing!);
            throw new FileStorageUnavailableException($"File volume '{volume.Name}' is temporarily unavailable.");
        }
    }

    private static bool HasMarker(ActiveVolume volume, out Exception? missing)
    {
        missing = File.Exists(Path.Combine(volume.RootPath, StorageNames.MarkerFileName))
            ? null
            : new DirectoryNotFoundException($"The marker of file volume '{volume.Name}' is missing: the disk may be unmounted.");
        return missing is null;
    }

    private ActiveVolume EnsureAvailable(ActiveVolume volume) =>
        RecheckIfDue(volume)
            ? throw new FileStorageUnavailableException($"File volume '{volume.Name}' is temporarily unavailable.")
            : volume;

    /// <summary>
    /// Re-probes a degraded volume when the last probe is older than <see cref="RecheckInterval"/>. Returns whether
    /// the volume is (still) degraded.
    /// </summary>
    public bool RecheckIfDue(ActiveVolume volume)
    {
        if (!volume.Degraded)
        {
            return false;
        }

        var now = time.GetUtcNow();
        if (now - volume.LastProbeAt >= RecheckInterval)
        {
            // Stamped before probing, so the rest of a burst skips straight past.
            volume.LastProbeAt = now;
            _ = ProbeAsync(volume, CancellationToken.None);
        }

        return volume.Degraded;
    }
}

/// <summary>Free and total bytes on the filesystem holding a root. Injectable so the 507 floor is testable.</summary>
public interface IFileStorageSpaceProbe
{
    /// <summary>Null when the root's filesystem cannot be read.</summary>
    (long Free, long Total)? Measure(string rootPath);
}

internal sealed class DriveInfoSpaceProbe : IFileStorageSpaceProbe
{
    public (long Free, long Total)? Measure(string rootPath)
    {
        try
        {
            var drive = new DriveInfo(rootPath);
            return (drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// The places no volume root may equal or overlap (§3.6 C4): the Data Protection key ring, the application
/// directory, the content root and the temp directory, compared after resolving symlinks. Injectable because a
/// test volume has to live somewhere, and the test host's temp directory is the obvious place.
/// </summary>
public sealed record FileStorageForbiddenRoots(IReadOnlyList<(string Label, string Path)> Locations);
