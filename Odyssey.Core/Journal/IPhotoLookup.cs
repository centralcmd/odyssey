namespace Odyssey.Core.Journal;

/// <summary>What the Journal module may know about a linked library photo (issue #428 §5.3): its label, never its bytes.</summary>
public sealed record PhotoLinkInfo(Guid PhotoId, string? Title, string? FileName);

/// <summary>
/// Narrow read surface the Photos module exposes to the Journal module (issue #321 v4), mirroring
/// <c>IFileLookup</c>/<c>IContactLookup</c>. Keeps the Journal→Photos dependency one-directional and FK-free: a
/// journal entry links a library <c>Photo</c> by id, validated and resolved through this lookup rather than an EF
/// navigation. Since issue #428 the journal never creates a photo: it links photos uploaded through the photo API.
/// </summary>
public interface IPhotoLookup
{
    /// <summary>The subset of <paramref name="photoIds"/> that exist — for link validation.</summary>
    Task<IReadOnlySet<Guid>> ExistingIdsAsync(IReadOnlyCollection<Guid> photoIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Each existing photo's label (title and effective file name) in one batched query, for render enrichment. Ids
    /// that no longer resolve are absent from the map (the caller drops them).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, PhotoLinkInfo>> ResolveAsync(IReadOnlyCollection<Guid> photoIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves ICS <c>odyssey-photo:</c> reference ids (issue #428 §3.3): each id as a <c>PhotoId</c>, then as the
    /// current <c>FileId</c> of a legacy photo, then as a backfilled photo's <c>LegacyFileId</c>. Unresolved ids are
    /// absent. Never creates a photo.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, Guid>> ResolveReferencesAsync(IReadOnlyCollection<Guid> referenceIds, CancellationToken cancellationToken = default);
}
