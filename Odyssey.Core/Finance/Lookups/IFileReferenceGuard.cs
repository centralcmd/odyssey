namespace Odyssey.Core.Finance;

/// <summary>
/// The Files store's side of a delete that starts somewhere else. Photos are the first caller: deleting
/// a library photo now deletes the file it wraps, so the Journal module needs a way to ask what else
/// would go with it and then to remove it — without reaching into Finance's tables itself.
/// </summary>
/// <remarks>
/// The shape mirrors <see cref="IContactReferenceGuard"/>, and for the same reason: nine tables carry a
/// cascading foreign key to <c>FileMetadata</c>, so removing one row can silently take an attachment off
/// a transaction, a tax statement or a journal entry. The database will do that without complaint. This
/// is what turns it into a 409 that names the other holders instead.
/// </remarks>
public interface IFileReferenceGuard
{
    /// <summary>
    /// Human-readable descriptions of everything other than the photo library that references
    /// <paramref name="fileId"/> — one entry per holder, e.g. <c>"a transaction attachment"</c>. Empty
    /// when the file is the photo's alone and deleting it is safe.
    /// </summary>
    Task<IReadOnlyList<string>> DescribeNonPhotoReferencesAsync(Guid fileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the file's metadata row and its blob. The blob is explicit because the FK runs the other
    /// way — <c>FileMetadata.FileBlobId</c> references <c>FileBlob</c>, so deleting the metadata alone
    /// would orphan the bytes rather than reclaim them.
    /// </summary>
    Task DeleteFileAndBlobAsync(Guid fileId, CancellationToken cancellationToken = default);
}
