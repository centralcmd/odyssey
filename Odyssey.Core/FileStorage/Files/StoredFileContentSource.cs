using Microsoft.EntityFrameworkCore;
using Odyssey.Context;

namespace Odyssey.Core.FileStorage.Files;

/// <summary>A Files-store file opened for reading. The consumer serves it with range processing and a strong ETag.</summary>
public sealed record StoredFileContent(Stream Content, long Length, string ContentType, string FileName, string Sha256Hash)
{
    public string ETag => $"\"{Sha256Hash}\"";
}

/// <summary>
/// The one read path to a Files-store file's bytes (issue #448 §3.4), whichever backend holds them: the object store for
/// a file on disk, the legacy <c>FileBlob</c> for one the move has not reached yet. Downloads, the contact image, the
/// vCard export, file analysis, the admin file export and the legacy-photo reader all read through it, by file id. It
/// is the only reader of <c>FileBlob</c> outside the two moves; a source-lint keeps it so.
/// </summary>
public sealed class StoredFileContentSource(OdysseyContext context, IObjectStore? store)
{
    /// <summary>The file, or null when no such file exists (or a legacy file's blob is gone). Applies no visibility rule.</summary>
    public Task<StoredFileContent?> OpenAsync(Guid fileId, CancellationToken cancellationToken) =>
        OpenAsync(context.FileMetadata.AsNoTracking().Where(f => f.Id == fileId), cancellationToken);

    /// <summary>The first file of <paramref name="files"/> — a caller-scoped query, so the caller's visibility rule applies.</summary>
    public async Task<StoredFileContent?> OpenAsync(IQueryable<FileMetadata> files, CancellationToken cancellationToken)
    {
        var file = await files.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (file is null)
        {
            return null;
        }

        if (StoredFileAreas.AreaOf(file) is { } area)
        {
            var stored = await Store().OpenReadAsync(StoredFileAreas.FeatureKey(area), StoredFileAreas.StoredObjectIdOf(file)!.Value, cancellationToken);
            return new StoredFileContent(stored.Content, stored.Length, file.ContentType, file.FileName, file.Sha256Hash);
        }

        var bytes = await LegacyBytesAsync(file, cancellationToken);
        return bytes is null
            ? null
            : new StoredFileContent(new MemoryStream(bytes, writable: false), bytes.LongLength, file.ContentType, file.FileName, file.Sha256Hash);
    }

    /// <summary>
    /// The whole file when it is at most <paramref name="maxBytes"/>, refusing a larger one from metadata alone (null
    /// bytes, <c>TooLarge</c>). Null when the file does not exist. For the readers that need the body in memory.
    /// </summary>
    public async Task<(byte[]? Bytes, bool TooLarge)?> ReadAllAsync(Guid fileId, long maxBytes, CancellationToken cancellationToken)
    {
        var file = await context.FileMetadata.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken);
        if (file is null)
        {
            return null;
        }

        if (file.SizeBytes > maxBytes)
        {
            return (null, true);
        }

        if (StoredFileAreas.AreaOf(file) is { } area)
        {
            var stored = await Store().OpenReadAsync(StoredFileAreas.FeatureKey(area), StoredFileAreas.StoredObjectIdOf(file)!.Value, cancellationToken);
            await using var stream = stored.Content;
            if (stored.Length > maxBytes)
            {
                return (null, true);
            }

            return (await ReadUpToAsync(stream, (int)stored.Length, cancellationToken), false);
        }

        var bytes = await LegacyBytesAsync(file, cancellationToken);
        if (bytes is null)
        {
            return null;
        }

        return bytes.LongLength > maxBytes ? (null, true) : (bytes, false);
    }

    /// <summary>At most <paramref name="maxBytes"/> leading bytes, never the whole file. Null when the file does not exist.</summary>
    public async Task<byte[]?> ReadPrefixAsync(Guid fileId, int maxBytes, CancellationToken cancellationToken)
    {
        if (maxBytes <= 0)
        {
            return null;
        }

        var file = await context.FileMetadata.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken);
        if (file is null)
        {
            return null;
        }

        if (StoredFileAreas.AreaOf(file) is { } area)
        {
            var stored = await Store().OpenReadAsync(StoredFileAreas.FeatureKey(area), StoredFileAreas.StoredObjectIdOf(file)!.Value, cancellationToken);
            await using var stream = stored.Content;
            return await ReadUpToAsync(stream, maxBytes, cancellationToken);
        }

        if (file.FileBlobId is not { } blobId)
        {
            return null;
        }

        if (context.Database.IsRelational())
        {
            // A bounded prefix projected in SQL, so a large legacy blob is never materialised to read its header.
            // EF/LINQ cannot express a byte-range read of a varbinary column; SqlQuery needs the column aliased "Value".
            var prefixes = await context.Database
                .SqlQuery<byte[]>($"SELECT SUBSTRING(`Content`, 1, {maxBytes}) AS `Value` FROM `FileBlob` WHERE `Id` = {blobId}")
                .ToListAsync(cancellationToken);
            return prefixes.Count == 0 ? null : prefixes[0];
        }

        var bytes = await LegacyBytesAsync(file, cancellationToken);
        return bytes is null ? null : bytes.Length <= maxBytes ? bytes : bytes[..maxBytes];
    }

    /// <summary>Whether the file's bytes are present: a committed stored object, or a legacy blob. For the admin export's count.</summary>
    public async Task<bool> IsPresentAsync(FileMetadata file, CancellationToken cancellationToken)
    {
        if (StoredFileAreas.StoredObjectIdOf(file) is { } objectId)
        {
            return await context.StoredObjects.AnyAsync(o => o.Id == objectId && o.State == StoredObjectState.Committed, cancellationToken);
        }

        return file.FileBlobId is { } blobId && await context.FileBlob.AnyAsync(b => b.Id == blobId, cancellationToken);
    }

    private async Task<byte[]?> LegacyBytesAsync(FileMetadata file, CancellationToken cancellationToken) =>
        file.FileBlobId is { } blobId
            ? await context.FileBlob.AsNoTracking().Where(b => b.Id == blobId).Select(b => b.Content).FirstOrDefaultAsync(cancellationToken)
            : null;

    private IObjectStore Store() =>
        store ?? throw new FileStorageUnavailableException("File storage is not set up on this server.");

    /// <summary>Reads at most <paramref name="maxBytes"/> from a stream's start.</summary>
    public static async Task<byte[]> ReadUpToAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[maxBytes];
        var total = 0;
        while (total < maxBytes)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, maxBytes - total), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total == maxBytes ? buffer : buffer[..total];
    }
}
