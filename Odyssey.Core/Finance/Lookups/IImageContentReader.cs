namespace Odyssey.Core.Finance;

/// <summary>
/// Narrow read-only accessor over the Files store's image bytes for the Photos module's metadata
/// extraction (issue #321, §5.5). Returns only a <b>bounded prefix</b> of the blob so header/EXIF/IPTC
/// reads never load a whole image into memory.
/// </summary>
public interface IImageContentReader
{
    /// <summary>
    /// Return at most <paramref name="maxBytes"/> leading bytes of the blob backing file
    /// <paramref name="fileId"/>, or <see langword="null"/> if the file does not exist. On the relational
    /// store this projects a SQL <c>SUBSTRING</c> so the full blob is never materialised.
    /// </summary>
    Task<byte[]?> ReadPrefixAsync(Guid fileId, int maxBytes, CancellationToken cancellationToken = default);

    /// <summary>
    /// The whole blob backing <paramref name="fileId"/>, or <see langword="null"/> when the file does not exist or its
    /// stored size exceeds <paramref name="maxBytes"/> — a larger blob is never read (issue #362 §3.1 item 6). The one
    /// place that reads an original's bytes, so moving originals off <c>FileBlob</c> changes only this method.
    /// </summary>
    Task<ImageContent?> ReadAllAsync(Guid fileId, long maxBytes, CancellationToken cancellationToken = default);
}

/// <summary>An original's bytes, or the fact that it was too large to read.</summary>
public sealed record ImageContent(byte[] Bytes, bool TooLarge)
{
    public static readonly ImageContent OverLimit = new([], TooLarge: true);
}
