namespace Odyssey.Core.Journal;

/// <summary>
/// Reads an image's embedded metadata from a (bounded) byte buffer. Pure, synchronous and total: any
/// failure (corrupt/absent/hostile data) returns <see cref="PhotoMetadata.Empty"/> rather than throwing.
/// The buffer is only read, never written — the original file is never modified (§2 Non-Goal 6).
/// </summary>
public interface IPhotoMetadataExtractor
{
    PhotoMetadata Extract(byte[] content);
}
