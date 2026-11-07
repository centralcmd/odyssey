using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Finance;

namespace Odyssey.Core.Journal.Originals;

/// <summary>
/// What a photo's original is, whichever backend holds it (issue #428 §3.1). <see cref="FileName"/> is the
/// <b>effective file name</b> (§5.4): <c>Photo.FileName ?? FileMetadata.FileName</c>.
/// </summary>
public sealed record PhotoOriginalInfo(
    Guid PhotoId,
    Guid? FileId,
    Guid? StoredObjectId,
    string? ContentType,
    string? FileName,
    long? SizeBytes,
    string? Sha256Hash)
{
    public bool IsLegacy => StoredObjectId is null;
}

/// <summary>A photo original opened for reading. The consumer serves it with range processing and an ETag.</summary>
public sealed record PhotoOriginalContent(Stream Content, long Length, string ContentType, string FileName, string Sha256Hash)
{
    public string ETag => $"\"{Sha256Hash}\"";
}

/// <summary>
/// The one read path to a photo's original (issue #428 §3.1): the object store for a photo that owns its bytes,
/// the legacy <c>FileBlob</c> for one the backfill has not moved yet. The content endpoint, the thumbnail renderer
/// and metadata extraction all read through it, by <b>photo id</b> — none reads by file id.
/// </summary>
public sealed class PhotoOriginalSource(OdysseyContext context, IObjectStore store, IImageContentReader legacy)
{
    /// <summary>
    /// The effective-name projection (§5.4), defined once. Every reader of a photo's file name, type, size or hash
    /// goes through it, so a rename of a legacy photo (which writes <c>Photo.FileName</c>) wins over its file's name.
    /// </summary>
    public static IQueryable<PhotoOriginalInfo> Describe(OdysseyContext context, IQueryable<Photo> photos) =>
        from p in photos
        join f in context.FileMetadata on p.FileId equals (Guid?)f.Id into files
        from f in files.DefaultIfEmpty()
        select new PhotoOriginalInfo(
            p.PhotoId,
            p.FileId,
            p.StoredObjectId,
            p.ContentType ?? (f == null ? null : f.ContentType),
            p.FileName ?? (f == null ? null : f.FileName),
            p.SizeBytes ?? (f == null ? null : (long?)f.SizeBytes),
            p.Sha256Hash ?? (f == null ? null : f.Sha256Hash));

    public Task<PhotoOriginalInfo?> DescribeAsync(Guid photoId, CancellationToken cancellationToken) =>
        Describe(context, context.Photos.AsNoTracking().Where(p => p.PhotoId == photoId)).FirstOrDefaultAsync(cancellationToken);

    /// <summary>The original, or null when the photo (or a legacy photo's file) does not exist.</summary>
    public async Task<PhotoOriginalContent?> OpenAsync(Guid photoId, CancellationToken cancellationToken)
    {
        if (await DescribeAsync(photoId, cancellationToken) is not { ContentType: { } type, FileName: { } name, Sha256Hash: { } hash } info)
        {
            return null;
        }

        if (info.StoredObjectId is { } objectId)
        {
            var stored = await store.OpenReadAsync(PhotoOriginalStorage.FeatureKey, objectId, cancellationToken);
            return new PhotoOriginalContent(stored.Content, stored.Length, type, name, hash);
        }

        var bytes = await context.FileMetadata
            .Where(f => f.Id == info.FileId)
            .Join(context.FileBlob, f => f.FileBlobId, b => b.Id, (_, b) => b.Content)
            .FirstOrDefaultAsync(cancellationToken);
        return bytes is null
            ? null
            : new PhotoOriginalContent(new MemoryStream(bytes, writable: false), bytes.LongLength, type, name, hash);
    }

    /// <summary>At most <paramref name="maxBytes"/> leading bytes, for metadata extraction; never the whole file.</summary>
    public async Task<byte[]?> ReadPrefixAsync(Guid photoId, int maxBytes, CancellationToken cancellationToken)
    {
        if (maxBytes <= 0 || await DescribeAsync(photoId, cancellationToken) is not { } info)
        {
            return null;
        }

        if (info.StoredObjectId is not { } objectId)
        {
            return info.FileId is { } fileId ? await legacy.ReadPrefixAsync(fileId, maxBytes, cancellationToken) : null;
        }

        var stored = await store.OpenReadAsync(PhotoOriginalStorage.FeatureKey, objectId, cancellationToken);
        await using var stream = stored.Content;
        return await ReadUpToAsync(stream, maxBytes, cancellationToken);
    }

    /// <summary>The whole original when it is at most <paramref name="maxBytes"/>, refusing a larger one from metadata alone.</summary>
    public async Task<ImageContent?> ReadAllAsync(Guid photoId, long maxBytes, CancellationToken cancellationToken)
    {
        if (await DescribeAsync(photoId, cancellationToken) is not { } info)
        {
            return null;
        }

        if (info.StoredObjectId is not { } objectId)
        {
            return info.FileId is { } fileId ? await legacy.ReadAllAsync(fileId, maxBytes, cancellationToken) : null;
        }

        if (info.SizeBytes > maxBytes)
        {
            return ImageContent.OverLimit;
        }

        var stored = await store.OpenReadAsync(PhotoOriginalStorage.FeatureKey, objectId, cancellationToken);
        await using var stream = stored.Content;
        if (stored.Length > maxBytes)
        {
            return ImageContent.OverLimit;
        }

        var bytes = await ReadUpToAsync(stream, (int)stored.Length, cancellationToken);
        return new ImageContent(bytes, TooLarge: false);
    }

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
