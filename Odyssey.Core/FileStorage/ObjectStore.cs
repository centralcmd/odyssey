using System.Buffers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Odyssey.Context;
using Odyssey.Core.Finance;

namespace Odyssey.Core.FileStorage;

/// <summary>A committed object opened for reading. The consumer serves it with range processing and an ETag from <see cref="Sha256Hash"/>.</summary>
public sealed record StoredObjectContent(Stream Content, long Length, string Sha256Hash)
{
    /// <summary>A strong ETag: the content hash, quoted.</summary>
    public string ETag => $"\"{Sha256Hash}\"";
}

/// <summary>
/// The filesystem object store (issue #346 §3.5): put, open-read and release. It owns name sanitizing, path
/// composition and uniqueness — no caller ever supplies a path.
/// </summary>
public interface IObjectStore
{
    /// <summary>
    /// Writes <paramref name="content"/> and returns its row, attached to the caller's context as
    /// <see cref="StoredObjectState.Committed"/> but <em>not saved</em>. The caller sets its reference from the
    /// returned id and saves both in one <c>SaveChangesAsync</c>; if that save never happens, the reservation is
    /// swept with its file.
    /// </summary>
    Task<StoredObject> PutAsync(
        string featureKey, object? layoutArgs, string? originalFileName, string? suffix, string contentType,
        Stream content, long maxBytes, CancellationToken cancellationToken);

    /// <summary>The bytes of a committed object of <paramref name="featureKey"/>; any other row is a 404.</summary>
    Task<StoredObjectContent> OpenReadAsync(string featureKey, Guid storedObjectId, CancellationToken cancellationToken);

    /// <summary>
    /// Marks a tracked committed row for deletion inside the caller's transaction. The caller nulls or deletes its
    /// reference in the same save. The file is never touched before the commit. <paramref name="immediate"/> (and
    /// every release of an <see cref="StorageReleaseMode.Immediate"/> feature) skips the grace.
    /// </summary>
    void StageRelease(StoredObject storedObject, bool immediate = false);

    /// <summary><see cref="StageRelease(StoredObject, bool)"/> for an id, loading the row into the caller's context.</summary>
    Task StageReleaseAsync(Guid storedObjectId, bool immediate, CancellationToken cancellationToken);
}

public sealed partial class ObjectStore(
    OdysseyContext context,
    IDbContextFactory<OdysseyContext> contextFactory,
    StorageFeatureCatalogue catalogue,
    FileStorageVolumeRegistry volumes,
    IFileStorageSpaceProbe space,
    IOptions<FileStorageOptions> fileStorageOptions,
    TimeProvider time,
    ILogger<ObjectStore> logger) : IObjectStore
{
    /// <summary>Writes are refused below this much free space (§3.5 step 3).</summary>
    public const long FreeSpaceFloorBytes = 1L * 1024 * 1024 * 1024;

    /// <summary>Concurrent writers racing for one name re-read at most this many times (§3.3 rule 5).</summary>
    public const int MaxReservationAttempts = 5;

    /// <summary>Above this number the collision marker becomes eight hex characters of a new GUID.</summary>
    public const int MaxNumberedCollision = 99;

    private const int BufferSize = 81920;

    [GeneratedRegex(@"^(?<stem>.*) \((?<n>[0-9]{1,2})\)$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedMarker();

    public async Task<StoredObject> PutAsync(
        string featureKey, object? layoutArgs, string? originalFileName, string? suffix, string contentType,
        Stream content, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var feature = Feature(featureKey);
        var volume = volumes.ForWrite(feature);
        volumes.EnsureMounted(volume);

        // Contract checks: a code defect, never user input.
        if (maxBytes <= 0 || maxBytes > fileStorageOptions.Value.MaxFileSizeBytes)
        {
            throw ContractViolation($"maxBytes must be between 1 and FileStorage:MaxFileSizeBytes ({fileStorageOptions.Value.MaxFileSizeBytes}).");
        }

        var extension = ContentTypeExtensions.For(contentType);
        if (!StorageNames.IsValidExtension(extension))
        {
            throw ContractViolation("The content type has no mapped extension.");
        }

        if (suffix is not null && !StorageNames.IsValidSuffix(suffix))
        {
            throw ContractViolation($"The suffix does not match {StorageNames.SuffixPattern}.");
        }

        var segments = feature.LayoutFor(layoutArgs);
        if (segments.Count is < StorageNames.MinSegments or > StorageNames.MaxSegments || !segments.All(StorageNames.IsValidSegment))
        {
            throw ContractViolation($"Feature '{feature.Key}' produced a layout that breaks the segment rules.");
        }

        var folder = feature.RootFolder + "/" + string.Join('/', segments);
        var folderPath = VolumeFileSystem.Contain(volume.RootPath, folder + "/x", feature.RootFolder);
        folderPath = Path.GetDirectoryName(folderPath)!;
        var tempFolder = Path.Combine(volume.RootPath, StorageNames.TempFolder);
        VolumeFileSystem.EnsureNoLinks(volume.RootPath, folderPath);
        if (VolumeFileSystem.IsLink(tempFolder))
        {
            throw Security(volume, "The volume's .tmp folder is a symbolic link.");
        }

        var stem = feature.NamingMode == StorageNamingMode.Opaque
            ? null
            : StoredFileNames.SanitizeStem(originalFileName, suffix, extension!);

        // A stray file the unique index cannot see makes a move fail; its name is excluded and the reservation retried.
        var strays = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var row = await ReserveAsync(volume, feature, folder, stem, suffix, extension!, strays, cancellationToken);
            var finalPath = VolumeFileSystem.Contain(volume.RootPath, row.RelativePath, feature.RootFolder);

            if (space.Measure(volume.RootPath) is { } measured && measured.Free < FreeSpaceFloorBytes)
            {
                throw new FileStorageFullException($"File volume '{volume.Name}' is below its free-space floor.");
            }

            var tempPath = Path.Combine(tempFolder, $"{row.Id:N}.part");
            var (size, hash) = await WriteTempAsync(volume, tempPath, content, maxBytes, cancellationToken);

            try
            {
                VolumeFileSystem.CreateDirectory(folderPath);
                VolumeFileSystem.EnsureNoLinks(volume.RootPath, folderPath);
                File.Move(tempPath, finalPath, overwrite: false);
            }
            catch (IOException ex) when (VolumeFileSystem.IsAlreadyExists(ex))
            {
                // Not ours: release the reservation now (the abandoned-write sweep would delete "its" final file).
                strays.Add(Path.GetFileName(finalPath));
                VolumeFileSystem.TryDelete(tempPath);
                await DeleteReservationAsync(row.Id, cancellationToken);
                if (strays.Count > MaxReservationAttempts)
                {
                    throw new FileStorageUnavailableException("The file could not be stored under a free name.");
                }

                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                VolumeFileSystem.TryDelete(tempPath);
                throw VolumeFailure(volume, ex);
            }

            volumes.MarkAvailable(volume);

            // Attach the reserved row to the CALLER's context, staged but not saved: the caller's save emits
            // UPDATE … WHERE Id = @id AND State = Pending, together with its referencing row.
            context.StoredObjects.Attach(row);
            row.State = StoredObjectState.Committed;
            row.SizeBytes = size;
            row.Sha256Hash = hash;
            return row;
        }
    }

    public async Task<StoredObjectContent> OpenReadAsync(string featureKey, Guid storedObjectId, CancellationToken cancellationToken)
    {
        var feature = Feature(featureKey);
        var row = await context.StoredObjects.AsNoTracking().SingleOrDefaultAsync(o => o.Id == storedObjectId, cancellationToken);
        if (row is null || row.State != StoredObjectState.Committed || !string.Equals(row.FeatureKey, feature.Key, StringComparison.Ordinal))
        {
            throw new DomainNotFoundException("The file was not found.");
        }

        var volume = volumes.ForExisting(row.VolumeId);
        if (!StorageNames.IsConformingPath(row.RelativePath, feature.RootFolder))
        {
            throw Security(volume, $"Stored object {row.Id} has a path that fails the structural check.");
        }

        string fullPath;
        try
        {
            fullPath = VolumeFileSystem.Contain(volume.RootPath, row.RelativePath, feature.RootFolder);
            VolumeFileSystem.EnsureNoLinks(volume.RootPath, fullPath);
        }
        catch (FileStorageIntegrityException)
        {
            logger.LogError("Security: stored object {StoredObjectId} on volume {Volume} failed the containment or symlink check.", row.Id, volume.Name);
            throw;
        }

        // The read path never changes the row: only the scrub does, after re-checking.
        if (row.IntegrityFaultAtUtc is not null)
        {
            throw Integrity(row, volume, "is flagged by the integrity scrub");
        }

        FileStream stream;
        try
        {
            stream = new FileStream(fullPath, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException && Directory.Exists(volume.RootPath))
        {
            throw Integrity(row, volume, "is missing");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw VolumeFailure(volume, ex);
        }

        if (stream.Length != row.SizeBytes)
        {
            await stream.DisposeAsync();
            throw Integrity(row, volume, "has the wrong length");
        }

        return new StoredObjectContent(stream, row.SizeBytes, row.Sha256Hash!);
    }

    public void StageRelease(StoredObject storedObject, bool immediate = false)
    {
        ArgumentNullException.ThrowIfNull(storedObject);
        if (context.Entry(storedObject).State == EntityState.Detached)
        {
            throw ContractViolation("StageRelease needs a row tracked by the caller's context.");
        }

        if (storedObject.State == StoredObjectState.PendingDelete)
        {
            storedObject.DeleteImmediately |= immediate || IsImmediate(storedObject.FeatureKey);
            return;
        }

        // Any committed row, including one the scrub has flagged.
        if (storedObject.State != StoredObjectState.Committed)
        {
            throw ContractViolation("Only a committed object can be released.");
        }

        storedObject.State = StoredObjectState.PendingDelete;
        storedObject.DeleteRequestedAtUtc = time.GetUtcNow().UtcDateTime;
        storedObject.DeleteImmediately = immediate || IsImmediate(storedObject.FeatureKey);
    }

    public async Task StageReleaseAsync(Guid storedObjectId, bool immediate, CancellationToken cancellationToken)
    {
        var row = await context.StoredObjects.SingleOrDefaultAsync(o => o.Id == storedObjectId, cancellationToken)
            ?? throw new DomainNotFoundException("The file was not found.");
        StageRelease(row, immediate);
    }

    private bool IsImmediate(string featureKey) => catalogue.Find(featureKey)?.ReleaseMode == StorageReleaseMode.Immediate;

    private StorageFeature Feature(string featureKey) =>
        catalogue.Find(featureKey) ?? throw ContractViolation($"Storage feature '{featureKey}' is not in the catalogue.");

    /// <summary>
    /// Inserts the <c>Pending</c> row through a context of its own, so it commits whatever the caller's context or
    /// transaction does, and never flushes the caller's staged changes. Each attempt uses a fresh short-lived
    /// context, so a failed entity is never left tracked.
    /// </summary>
    private async Task<StoredObject> ReserveAsync(
        ActiveVolume volume, StorageFeature feature, string folder, string? stem, string? suffix, string extension,
        HashSet<string> strays, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        for (var attempt = 1; ; attempt++)
        {
            string fileName;
            if (stem is null)
            {
                fileName = StoredFileNames.Compose(id.ToString("N"), null, suffix, extension);
            }
            else if (attempt > MaxReservationAttempts)
            {
                fileName = StoredFileNames.Compose(stem, Guid.NewGuid().ToString("N")[..8], suffix, extension);
            }
            else
            {
                fileName = await FreeNameAsync(volume, folder, stem, suffix, extension, strays, cancellationToken);
            }

            var row = new StoredObject
            {
                Id = id,
                VolumeId = volume.Id,
                FeatureKey = feature.Key,
                RelativePath = folder + "/" + fileName,
                State = StoredObjectState.Pending,
                CreatedAtUtc = time.GetUtcNow().UtcDateTime,
            };

            await using var reservation = await contextFactory.CreateDbContextAsync(cancellationToken);
            reservation.StoredObjects.Add(row);
            try
            {
                await reservation.SaveChangesAsync(cancellationToken);
                return new StoredObject
                {
                    Id = row.Id,
                    VolumeId = row.VolumeId,
                    FeatureKey = row.FeatureKey,
                    RelativePath = row.RelativePath,
                    State = row.State,
                    CreatedAtUtc = row.CreatedAtUtc,
                };
            }
            catch (DbUpdateException ex) when (ex.GetBaseException() is MySqlException { ErrorCode: MySqlErrorCode.DuplicateKeyEntry })
            {
                // A concurrent writer took the name between our read and our insert: re-read.
                if (attempt > MaxReservationAttempts)
                {
                    throw;
                }
            }
        }
    }

    /// <summary>
    /// One indexed read for the names in the folder that share the stem, then the lowest free number. The
    /// comparison is as insensitive as the database's collation, so a case- or accent-only variant counts as taken.
    /// </summary>
    private async Task<string> FreeNameAsync(
        ActiveVolume volume, string folder, string stem, string? suffix, string extension, HashSet<string> strays,
        CancellationToken cancellationToken)
    {
        var tail = (suffix ?? string.Empty) + extension;
        var pattern = StorageNames.EscapeLike(folder + "/" + stem) + "%" + StorageNames.EscapeLike(tail);
        await using var read = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await read.StoredObjects.AsNoTracking()
            .Where(o => o.VolumeId == volume.Id && EF.Functions.Like(o.RelativePath, pattern, "\\"))
            .Select(o => o.RelativePath)
            .ToListAsync(cancellationToken);

        var taken = new HashSet<int>();
        foreach (var name in existing.Select(p => p[(folder.Length + 1)..]).Concat(strays))
        {
            if (!EndsWithInsensitive(name, tail))
            {
                continue;
            }

            var middle = name[..^tail.Length];
            if (SameName(middle, stem))
            {
                taken.Add(1);
            }
            else if (NumberedMarker().Match(middle) is { Success: true } match && SameName(match.Groups["stem"].Value, stem))
            {
                taken.Add(int.Parse(match.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        if (!taken.Contains(1))
        {
            return StoredFileNames.Compose(stem, null, suffix, extension);
        }

        // AdminFileExportService.AppendSuffix's convention: the first collision is (2).
        for (var n = 2; n <= MaxNumberedCollision; n++)
        {
            if (!taken.Contains(n))
            {
                return StoredFileNames.Compose(stem, n.ToString(System.Globalization.CultureInfo.InvariantCulture), suffix, extension);
            }
        }

        return StoredFileNames.Compose(stem, Guid.NewGuid().ToString("N")[..8], suffix, extension);
    }

    private static bool SameName(string a, string b) => StorageNames.CollationComparer.Equals(a, b);

    private static bool EndsWithInsensitive(string value, string tail) =>
        value.Length >= tail.Length && SameName(value[^tail.Length..], tail);

    private async Task DeleteReservationAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var cleanup = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await cleanup.StoredObjects.SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (row is not null)
        {
            cleanup.StoredObjects.Remove(row);
            await cleanup.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>Streams into <c>.tmp/&lt;id&gt;.part</c>, hashing as the bytes arrive; the body is never buffered whole.</summary>
    private async Task<(long Size, string Hash)> WriteTempAsync(
        ActiveVolume volume, string tempPath, Stream content, long maxBytes, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size = 0;
        FileStream? output = null;
        try
        {
            try
            {
                VolumeFileSystem.CreateDirectory(Path.GetDirectoryName(tempPath)!);
                output = VolumeFileSystem.CreateNew(tempPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw VolumeFailure(volume, ex);
            }

            while (true)
            {
                // A failure reading the caller's stream says nothing about the disk, so it is not classified.
                var read = await content.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                size += read;
                if (size > maxBytes)
                {
                    throw new StoredObjectTooLargeException("The file is larger than this feature accepts.");
                }

                hash.AppendData(buffer, 0, read);
                try
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw VolumeFailure(volume, ex);
                }
            }

            try
            {
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw VolumeFailure(volume, ex);
            }

            await output.DisposeAsync();
            output = null;
            return (size, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        catch
        {
            if (output is not null)
            {
                await output.DisposeAsync();
            }

            // Best effort; the Pending row stays and the abandoned-write sweep finishes the job.
            VolumeFileSystem.TryDelete(tempPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private Exception VolumeFailure(ActiveVolume volume, Exception cause)
    {
        if (!VolumeFileSystem.IsVolumeFailure(cause))
        {
            return cause;
        }

        volumes.MarkDegraded(volume, cause);
        return VolumeFileSystem.IsDiskFull(cause)
            ? new FileStorageFullException($"File volume '{volume.Name}' is full.")
            : new FileStorageUnavailableException($"File volume '{volume.Name}' is temporarily unavailable.");
    }

    private FileStorageIntegrityException Integrity(StoredObject row, ActiveVolume volume, string what)
    {
        logger.LogError("Stored object {StoredObjectId} on volume {Volume} {What}.", row.Id, volume.Name, what);
        return new FileStorageIntegrityException("The stored file failed its integrity check.");
    }

    private FileStorageIntegrityException Security(ActiveVolume volume, string what)
    {
        logger.LogError("Security: file storage refused an operation on volume {Volume}: {What}", volume.Name, what);
        return new FileStorageIntegrityException("A stored file's path failed its safety check.");
    }

    private StoreContractViolationException ContractViolation(string message)
    {
        logger.LogError("File storage contract violation (a code defect): {Message}", message);
        return new StoreContractViolationException(message);
    }
}
