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
}
