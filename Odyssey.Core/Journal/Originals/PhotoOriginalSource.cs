using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.FileStorage;

namespace Odyssey.Core.Journal.Originals;

/// <summary>What a photo's original is: its stored object and the photo's own name, type, size and hash.</summary>
public sealed record PhotoOriginalInfo(
    Guid PhotoId,
    Guid StoredObjectId,
    string ContentType,
    string FileName,
    long SizeBytes,
    string Sha256Hash);

/// <summary>A photo original opened for reading. The consumer serves it with range processing and an ETag.</summary>
public sealed record PhotoOriginalContent(Stream Content, long Length, string ContentType, string FileName, string Sha256Hash)
{
    public string ETag => $"\"{Sha256Hash}\"";
}

/// <summary>An original's bytes, or the fact that it was too large to read.</summary>
public sealed record ImageContent(byte[] Bytes, bool TooLarge)
{
    public static readonly ImageContent OverLimit = new([], TooLarge: true);
}

/// <summary>
/// The one read path to a photo's original (issue #428 §3.1): the object store, under the <c>PhotoOriginals</c>
/// feature. The content endpoint, the thumbnail renderer and metadata extraction all read through it, by <b>photo
/// id</b> — none reads by file id.
/// </summary>
public sealed class PhotoOriginalSource(OdysseyContext context, IObjectStore store)
{
    /// <summary>The projection every reader of a photo's file name, type, size or hash goes through.</summary>
    public static IQueryable<PhotoOriginalInfo> Describe(OdysseyContext context, IQueryable<Photo> photos) =>
        photos.Select(p => new PhotoOriginalInfo(p.PhotoId, p.StoredObjectId, p.ContentType, p.FileName, p.SizeBytes, p.Sha256Hash));

    public Task<PhotoOriginalInfo?> DescribeAsync(Guid photoId, CancellationToken cancellationToken) =>
        Describe(context, context.Photos.AsNoTracking().Where(p => p.PhotoId == photoId)).FirstOrDefaultAsync(cancellationToken);

    /// <summary>The original, or null when the photo does not exist.</summary>
    public async Task<PhotoOriginalContent?> OpenAsync(Guid photoId, CancellationToken cancellationToken)
    {
        if (await DescribeAsync(photoId, cancellationToken) is not { } info)
        {
            return null;
        }

        var stored = await store.OpenReadAsync(PhotoOriginalStorage.FeatureKey, info.StoredObjectId, cancellationToken);
        return new PhotoOriginalContent(stored.Content, stored.Length, info.ContentType, info.FileName, info.Sha256Hash);
    }

    /// <summary>At most <paramref name="maxBytes"/> leading bytes, for metadata extraction; never the whole file.</summary>
    public async Task<byte[]?> ReadPrefixAsync(Guid photoId, int maxBytes, CancellationToken cancellationToken)
    {
        if (maxBytes <= 0 || await DescribeAsync(photoId, cancellationToken) is not { } info)
        {
            return null;
        }

        var stored = await store.OpenReadAsync(PhotoOriginalStorage.FeatureKey, info.StoredObjectId, cancellationToken);
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

        if (info.SizeBytes > maxBytes)
        {
            return ImageContent.OverLimit;
        }

        var stored = await store.OpenReadAsync(PhotoOriginalStorage.FeatureKey, info.StoredObjectId, cancellationToken);
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
