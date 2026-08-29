namespace Odyssey.Core.Journal;

/// <summary>
/// Narrow read / find-or-create surface the Photos module exposes to the Journal module (issue #321 v4),
/// mirroring <c>IFileLookup</c>/<c>IContactLookup</c>. Keeps the Journal→Photos dependency
/// one-directional and FK-free: a journal entry links a library <c>Photo</c> by id, validated/resolved
/// through this lookup rather than an EF navigation.
/// </summary>
public interface IPhotoLookup
{
    /// <summary>The subset of <paramref name="photoIds"/> that exist — for link validation.</summary>
    Task<IReadOnlySet<Guid>> ExistingIdsAsync(IReadOnlyCollection<Guid> photoIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Map each existing photo id to its backing <c>FileId</c> in one batched query (WHERE PhotoId IN …),
    /// for render enrichment. Ids that no longer resolve are simply absent from the map (caller drops them).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, Guid>> ResolveFileIdsAsync(IReadOnlyCollection<Guid> photoIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Return the id of the single library <c>Photo</c> for <paramref name="fileId"/>, creating it
    /// (running scalar metadata extraction) if it does not yet exist. Idempotent and race-safe (keyed on
    /// the <c>Photo.FileId</c> unique index; a concurrent create's duplicate-key is caught + re-fetched).
    /// The file must be a known image, else <see cref="Shared.DomainUnprocessableException"/>.
    /// </summary>
    Task<Guid> FindOrCreatePhotoIdForFileAsync(Guid fileId, string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Batched find-or-create for many image file ids (issue #339 §5 step 3.7). Callers must pre-filter to
    /// image-type files (e.g. via <c>IFileLookup.ExistingImageIdsAsync</c>) — this method does not re-check
    /// content type. Returns <c>fileId → PhotoId</c> for every input, using one query for the already-linked
    /// rows plus a single batched insert for the rest, rather than one <c>SaveChanges</c> per file.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, Guid>> FindOrCreatePhotoIdsForFilesAsync(
        IReadOnlyCollection<Guid> fileIds, string userId, CancellationToken cancellationToken = default);
}
