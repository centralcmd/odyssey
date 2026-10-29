using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Odyssey.Context;

namespace Odyssey.Core.FileStorage;

/// <summary>Which host is verifying (issue #346 §3.6, Amendment A3.2).</summary>
public enum FileStorageVerifyMode
{
    /// <summary>The API: runs every check, writes markers, adopts (V4) and initialises (V5).</summary>
    Initialize,

    /// <summary>The worker: the same checks, but a volume that would be adopted or initialised is left alone and reported as pending.</summary>
    VerifyOnly,
}

/// <summary>The outcome of one verification run.</summary>
public sealed record FileStorageVerification(bool NoOp, IReadOnlyList<string> PendingVolumes, IReadOnlyList<string> OrphanedFeatures)
{
    public static readonly FileStorageVerification Inactive = new(true, [], []);
}

/// <summary>The identity marker at a volume root, <c>.odyssey-volume.json</c>. Created once and never rewritten.</summary>
public sealed record VolumeMarker(
    [property: JsonPropertyName("volumeId")] Guid VolumeId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("initializedAtUtc")] DateTime InitializedAtUtc,
    [property: JsonPropertyName("layoutVersion")] int LayoutVersion);

/// <summary>
/// The startup verifier (issue #346 §3.6): every storage misconfiguration stops the host before it serves
/// anything, with a message naming the check, the volume or feature, the configuration key and the remedy.
/// The API runs it before Kestrel listens; the worker runs it in <see cref="FileStorageVerifyMode.VerifyOnly"/>
/// before its jobs start.
/// </summary>
public sealed class FileStorageVerifier(
    OdysseyContext context,
    IOptions<FileStorageVolumesOptions> options,
    StorageFeatureCatalogue catalogue,
    FileStorageVolumeRegistry registry,
    FileStorageForbiddenRoots forbiddenRoots,
    TimeProvider time,
    ILogger<FileStorageVerifier> logger)
{
    public const int LayoutVersion = 1;

    /// <summary>The root entries an otherwise-empty volume may hold: <c>lost+found</c> comes from <c>mkfs.ext4</c>.</summary>
    private static readonly HashSet<string> ToleratedRootEntries = new(StringComparer.Ordinal)
    {
        StorageNames.LostAndFound, StorageNames.MarkerFileName, StorageNames.TempFolder, StorageNames.QuarantineFolder,
    };

    private static readonly JsonSerializerOptions MarkerJson = new() { WriteIndented = true };

    public async Task<FileStorageVerification> VerifyAsync(FileStorageVerifyMode mode, CancellationToken cancellationToken)
    {
        var configured = options.Value;
        var volumes = configured.Volumes ?? [];
        var bindings = configured.Bindings ?? [];

        // The no-op rule (§3.6 as amended by A2.1). An InMemory host with no volumes is a test host: it has no
        // StoredObjects for B1 to protect. A relational host is a no-op only when nothing at all is set up.
        if (volumes.Count == 0)
        {
            if (!context.Database.IsRelational())
            {
                return FileStorageVerification.Inactive;
            }

            if (catalogue.IsEmpty && !await AnyLiveObjectsAsync(cancellationToken))
            {
                return FileStorageVerification.Inactive;
            }
        }

        // ── Configuration checks (C1–C5, B1–B3) ──────────────────────────────────────────────────────────
        var roots = new List<(string Name, string Root, string Real)>();
        foreach (var (rawName, volume) in volumes.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!StorageNames.IsValidName(rawName))
            {
                throw Fail("InvalidName", $"Volume name '{rawName}' must match {StorageNames.NamePattern}.",
                    $"FileStorage:Volumes:{rawName}", "Rename the volume: letters and digits only, starting with a letter.");
            }

            var name = rawName.ToLowerInvariant();
            var key = $"FileStorage:Volumes:{rawName}:RootPath";
            var rootPath = volume?.RootPath;
            if (string.IsNullOrWhiteSpace(rootPath) || !Path.IsPathFullyQualified(rootPath))
            {
                throw Fail("InvalidRootPath", $"Volume '{name}' has no absolute RootPath.", key, "Set it to the absolute path the volume is mounted at.");
            }

            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
            if (root == Path.GetPathRoot(root))
            {
                throw Fail("ForbiddenLocation", $"Volume '{name}' is the filesystem root.", key, "Point it at a dedicated directory.");
            }

            if (!Directory.Exists(root))
            {
                throw Fail("RootNotFound", $"Volume '{name}' root {root} does not exist. The verifier never creates a root, so a mistyped "
                    + "or unmounted path cannot silently become a directory on the container's ephemeral layer.", key,
                    "Mount the volume at that path (and create the directory), or correct the path.");
            }

            var real = VolumeFileSystem.RealPath(root);
            if (real == Path.GetPathRoot(real))
            {
                throw Fail("ForbiddenLocation", $"Volume '{name}' resolves to the filesystem root.", key, "Point it at a dedicated directory.");
            }

            foreach (var (label, path) in forbiddenRoots.Locations)
            {
                if (!string.IsNullOrWhiteSpace(path) && VolumeFileSystem.Overlaps(real, VolumeFileSystem.RealPath(path)))
                {
                    throw Fail("ForbiddenLocation", $"Volume '{name}' root {root} equals or overlaps the {label}.", key,
                        "Point it at a dedicated directory outside the application, its key ring and the temp directory.");
                }
            }

            foreach (var other in roots)
            {
                if (VolumeFileSystem.Overlaps(real, other.Real))
                {
                    throw Fail("OverlappingVolumes", $"Volumes '{name}' and '{other.Name}' resolve to the same directory, or one inside the other.",
                        key, "Give every volume its own directory.");
                }
            }

            roots.Add((name, root, real));
        }

        var resolvedBindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (featureKey, volumeName) in bindings)
        {
            var key = $"FileStorage:Bindings:{featureKey}";
            if (!StorageNames.IsValidName(featureKey) || !StorageNames.IsValidName(volumeName))
            {
                throw Fail("InvalidName", $"Binding '{featureKey}' → '{volumeName}' does not match {StorageNames.NamePattern}.", key,
                    "Use a feature key and a volume name of letters and digits only.");
            }

            if (catalogue.Find(featureKey) is not { } feature)
            {
                throw Fail("UnknownFeature", $"Binding names feature '{featureKey}', which this version does not have.", key,
                    "Correct the feature key, or remove the binding.");
            }

            if (!roots.Any(r => string.Equals(r.Name, volumeName, StringComparison.OrdinalIgnoreCase)))
            {
                throw Fail("UnknownVolume", $"Feature '{feature.Key}' is bound to volume '{volumeName}', which is not configured.", key,
                    "Configure that volume under FileStorage:Volumes, or bind the feature to one that is.");
            }

            resolvedBindings[feature.Key] = volumeName.ToLowerInvariant();
        }

        foreach (var feature in catalogue.Features)
        {
            if (!resolvedBindings.ContainsKey(feature.Key))
            {
                throw Fail("UnboundFeature", $"Feature '{feature.Key}' is not bound to a volume.", $"FileStorage:Bindings:{feature.Key}",
                    "Bind it to a configured volume, e.g. FileStorage__Bindings__" + feature.Key + "=primary.");
            }
        }

        // ── B4: live rows of a feature this build does not know — a warning, never a stop ─────────────────
        var orphanedFeatures = (await context.StoredObjects.AsNoTracking()
                .Where(o => o.State != StoredObjectState.Pending)
                .Select(o => o.FeatureKey)
                .Distinct()
                .ToListAsync(cancellationToken))
            .Where(k => catalogue.Find(k) is null)
            .Order(StringComparer.Ordinal)
            .ToList();
        foreach (var featureKey in orphanedFeatures)
        {
            logger.LogCritical(
                "File storage holds live objects for feature {Feature}, which this version does not know (a rollback?). They are kept "
                + "and not served; pending deletions still complete. Deploy the version that knows it, or remove it with the two-release procedure.",
                featureKey);
        }

        // ── Volume identity (V1–V8) ──────────────────────────────────────────────────────────────────────
        // The adoption snapshot is taken once, before any adoption, so several marked volumes on a freshly
        // reset database all adopt (R3).
        var databaseHadVolumes = await context.FileStorageVolumes.AnyAsync(cancellationToken);
        var active = new List<(Guid Id, string Name, string RootPath)>();
        var pending = new List<string>();
        foreach (var (name, root, _) in roots)
        {
            var key = $"FileStorage:Volumes:{name}:RootPath";
            if (!VolumeFileSystem.TryProbeWrite(root, out var probeFailure))
            {
                throw Fail("NotWritable", $"Volume '{name}' root {root} is not writable ({probeFailure!.GetType().Name}).", key,
                    "Give the app user (UID 1654) write access: chown app:app and chmod 0750.");
            }

            var outcome = await VerifyVolumeAsync(name, root, key, mode, databaseHadVolumes, cancellationToken);
            if (outcome is { } id)
            {
                active.Add((id, name, root));
            }
            else
            {
                pending.Add(name);
            }
        }

        // V8: a database volume with live objects that is no longer configured.
        var configuredNames = roots.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unconfigured = await context.FileStorageVolumes.AsNoTracking()
            .Where(v => !configuredNames.Contains(v.Name))
            .Select(v => new { v.Id, v.Name, Live = context.StoredObjects.Any(o => o.VolumeId == v.Id && o.State != StoredObjectState.Pending) })
            .ToListAsync(cancellationToken);
        foreach (var row in unconfigured.OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            if (row.Live)
            {
                throw Fail("MissingVolume", $"Volume '{row.Name}' still holds files but is not configured.",
                    $"FileStorage:Volumes:{row.Name}:RootPath",
                    "Configure it again (a rebinding moves new writes only; existing files are still read from it).");
            }

            logger.LogWarning("File volume {Volume} is recorded but no longer configured; it holds no files.", row.Name);
        }

        if (pending.Count == 0)
        {
            registry.Activate(active, resolvedBindings);
        }

        return new FileStorageVerification(false, pending, orphanedFeatures);
    }

    /// <summary>One volume's identity verdict. Returns its id, or null when it waits for the API (VerifyOnly).</summary>
    private async Task<Guid?> VerifyVolumeAsync(
        string name, string root, string key, FileStorageVerifyMode mode, bool databaseHadVolumes, CancellationToken cancellationToken)
    {
        var markerPath = Path.Combine(root, StorageNames.MarkerFileName);
        var row = await context.FileStorageVolumes.SingleOrDefaultAsync(v => v.Name == name, cancellationToken);
        var marker = ReadMarker(markerPath, name, key);
        var now = time.GetUtcNow().UtcDateTime;

        if (marker is null)
        {
            var empty = IsEmpty(root);
            if (!empty)
            {
                throw Fail("UnrecognisedContent", $"Volume '{name}' has no marker but is not empty.", key,
                    "Point the volume at an empty directory, or restore its .odyssey-volume.json marker from a backup.");
            }

            if (row is not null && await HasLiveObjectsAsync(row.Id, cancellationToken))
            {
                // V6: compose down -v, a mistyped bind mount or an unmounted disk all look like this. Re-initialising
                // would hide the data loss.
                throw Fail("VolumeLost", $"Volume '{name}' is empty and has no marker, but the database says it holds files.", key,
                    "Mount the right disk or restore the volume from a backup. See docs/deployment.md → File storage → Startup checks.");
            }

            if (mode == FileStorageVerifyMode.VerifyOnly)
            {
                return null;
            }

            // V5: initialise. A row with no live objects keeps its id, so its pending rows stay valid.
            var id = row?.Id ?? Guid.NewGuid();
            if (!TryWriteMarker(markerPath, new VolumeMarker(id, name, now, LayoutVersion)))
            {
                // A racing writer created it first: evaluate what it wrote (V2/V3).
                return await VerifyVolumeAsync(name, root, key, mode, databaseHadVolumes, cancellationToken);
            }

            if (row is null)
            {
                context.FileStorageVolumes.Add(new FileStorageVolume { Id = id, Name = name, InitializedAtUtc = now, LastSeenAtUtc = now });
            }
            else
            {
                row.InitializedAtUtc = now;
                row.LastSeenAtUtc = now;
            }

            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Initialised file volume {Volume} at {Root}.", name, root);
            return id;
        }

        if (!string.Equals(marker.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            throw Fail("WrongVolume", $"Volume '{name}' carries the marker of volume '{marker.Name}'.", key,
                "Mount the right disk at this path, or point the volume at its own directory.");
        }

        if (row is not null)
        {
            if (row.Id != marker.VolumeId)
            {
                throw Fail("WrongVolume", $"Volume '{name}' carries a marker from a different volume of that name.", key,
                    "Mount the disk that belongs to this database, or restore both from the same backup.");
            }

            // V2.
            row.LastSeenAtUtc = now;
            await context.SaveChangesAsync(cancellationToken);
            return row.Id;
        }

        // V4: a marker and no row. Adopt only an empty volume on a database that had no volumes at all when this run
        // began — a freshly reset database. Anything else would attach another deployment's volume.
        if (!IsEmpty(root) || databaseHadVolumes)
        {
            throw Fail("UnknownMarker", $"Volume '{name}' carries a marker this database does not know.", key,
                "Its files belong to a different database. Point the volume at an empty directory, or restore the matching database.");
        }

        if (mode == FileStorageVerifyMode.VerifyOnly)
        {
            return null;
        }

        context.FileStorageVolumes.Add(new FileStorageVolume
        {
            Id = marker.VolumeId, Name = name, InitializedAtUtc = marker.InitializedAtUtc, LastSeenAtUtc = now,
        });
        await context.SaveChangesAsync(cancellationToken);
        logger.LogWarning("Adopted file volume {Volume} from its marker after a database reset.", name);
        return marker.VolumeId;
    }

    private VolumeMarker? ReadMarker(string markerPath, string name, string key)
    {
        var info = new FileInfo(markerPath);
        if (!info.Exists)
        {
            return null;
        }

        try
        {
            if (info.LinkTarget is not null)
            {
                throw new JsonException("The marker is a symbolic link.");
            }

            var marker = JsonSerializer.Deserialize<VolumeMarker>(File.ReadAllText(markerPath), MarkerJson);
            return marker is { VolumeId: var id, Name: not null } && id != Guid.Empty
                ? marker
                : throw new JsonException("The marker is incomplete.");
        }
        catch (JsonException ex)
        {
            throw Fail("WrongVolume", $"Volume '{name}' has an unreadable marker ({ex.Message})", key,
                "Restore .odyssey-volume.json from a backup of this volume.");
        }
    }

    private static bool TryWriteMarker(string markerPath, VolumeMarker marker)
    {
        try
        {
            using var stream = VolumeFileSystem.CreateNew(markerPath);
            JsonSerializer.Serialize(stream, marker, MarkerJson);
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch (IOException ex) when (VolumeFileSystem.IsAlreadyExists(ex))
        {
            return false;
        }
    }

    /// <summary>Nothing except the tolerated root entries (A4, F13).</summary>
    internal static bool IsEmpty(string root) =>
        new DirectoryInfo(root).EnumerateFileSystemInfos().All(entry => ToleratedRootEntries.Contains(entry.Name));

    private Task<bool> AnyLiveObjectsAsync(CancellationToken cancellationToken) =>
        context.StoredObjects.AnyAsync(o => o.State != StoredObjectState.Pending, cancellationToken);

    private Task<bool> HasLiveObjectsAsync(Guid volumeId, CancellationToken cancellationToken) =>
        context.StoredObjects.AnyAsync(o => o.VolumeId == volumeId && o.State != StoredObjectState.Pending, cancellationToken);

    private FileStorageMisconfiguredException Fail(string check, string problem, string key, string remedy)
    {
        var exception = new FileStorageMisconfiguredException(check, $"{problem} Key: {key}. Remedy: {remedy}");
        logger.LogCritical("File storage startup check {Check} failed: {Problem} Key: {Key}. Remedy: {Remedy}", check, problem, key, remedy);
        return exception;
    }
}
