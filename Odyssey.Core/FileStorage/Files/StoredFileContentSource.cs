using Microsoft.EntityFrameworkCore;
using Odyssey.Context;

namespace Odyssey.Core.FileStorage.Files;

/// <summary>A Files-store file opened for reading. The consumer serves it with range processing and a strong ETag.</summary>
public sealed record StoredFileContent(Stream Content, long Length, string ContentType, string FileName, string Sha256Hash)
{
    public string ETag => $"\"{Sha256Hash}\"";
}

/// <summary>
/// The one read path to a Files-store file's bytes (issue #448 §3.4): the object store, under the feature of the file's
/// area. Downloads, the contact image, the vCard export, file analysis and the admin file export all read through it, by
/// file id; a source-lint keeps it so.
/// </summary>
public sealed class StoredFileContentSource(OdysseyContext context, IObjectStore? store)
{
    /// <summary>The file, or null when no such file exists. Applies no visibility rule.</summary>
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

        var stored = await OpenStoredAsync(file, cancellationToken);
        return new StoredFileContent(stored.Content, stored.Length, file.ContentType, file.FileName, file.Sha256Hash);
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

        var stored = await OpenStoredAsync(file, cancellationToken);
        await using var stream = stored.Content;
        if (stored.Length > maxBytes)
        {
            return (null, true);
        }

        return (await ReadUpToAsync(stream, (int)stored.Length, cancellationToken), false);
    }

    private Task<StoredObjectContent> OpenStoredAsync(FileMetadata file, CancellationToken cancellationToken) =>
        Store().OpenReadAsync(StoredFileAreas.FeatureKey(StoredFileAreas.AreaOf(file)), StoredFileAreas.StoredObjectIdOf(file), cancellationToken);

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
