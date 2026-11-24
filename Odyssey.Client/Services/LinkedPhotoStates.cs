using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Services;

/// <summary>
/// The library state (favourite, archived, the rail's metadata) of photos a record links by id — a journal entry's
/// photos, a book's images. The link DTOs carry only the label and the thumbnail flag, but the shared
/// <c>OdsPhotoTile</c> shows the heart and the "Archived" badge, and the shared <c>PhotoViewer</c> binds
/// <see cref="PhotoSummary"/> rows (Odyssey Design System · components/PhotoTile, version 1795477868-c9a2). So a
/// surface hydrates each linked photo once (<c>GET /api/photos/{id}</c>, under <c>photos.read</c>) and keeps it here.
/// </summary>
/// <remarks>
/// <para>
/// One instance per surface. A photo that cannot be fetched (no access, since deleted) is remembered as missing so it
/// is not asked for again; <see cref="Get"/> returns null for it and the surface shows no heart and no badge rather
/// than a state it does not know. <see cref="Summaries"/> turns a missing one into
/// <see cref="PhotoMappers.MinimalSummary"/>, so the viewer still opens it.
/// </para>
/// <para>
/// A follow-up could add <c>Favourited</c> / <c>Archived</c> to the link DTOs and drop the per-photo reads.
/// </para>
/// </remarks>
public sealed class LinkedPhotoStates
{
    private readonly Dictionary<Guid, PhotoSummary?> known = [];

    /// <summary>The hydrated summary, or null when it is not loaded yet or could not be read.</summary>
    public PhotoSummary? Get(Guid photoId) => known.GetValueOrDefault(photoId);

    /// <summary>Fetch every id not already known, in parallel. Returns whether anything was added.</summary>
    public async Task<bool> EnsureAsync(IPhotosApiClient photos, IEnumerable<Guid> photoIds)
    {
        var missing = photoIds.Distinct().Where(id => !known.ContainsKey(id)).ToList();
        if (missing.Count == 0)
        {
            return false;
        }

        // Claimed before the reads so an overlapping call does not ask again.
        foreach (var id in missing)
        {
            known[id] = null;
        }

        var fulls = await Task.WhenAll(missing.Select(id => photos.GetAsync(id)));
        for (var i = 0; i < missing.Count; i++)
        {
            known[missing[i]] = fulls[i] is { } full ? PhotoMappers.ToSummary(full) : null;
        }

        return true;
    }

    /// <summary>Re-read one photo (after an archive or an edit made in the viewer).</summary>
    public async Task RefreshAsync(IPhotosApiClient photos, Guid photoId)
    {
        known.Remove(photoId);
        await EnsureAsync(photos, [photoId]);
    }

    /// <summary>Flip the favourite in place — what the viewer just saved.</summary>
    public void ToggleFavourite(Guid photoId)
    {
        if (known.GetValueOrDefault(photoId) is { } p)
        {
            known[photoId] = p with { Favourited = p.Favourited is null ? DateTime.UtcNow : null };
        }
    }

    /// <summary>Forget one photo (deleted), so it is never shown with a stale state.</summary>
    public void Forget(Guid photoId) => known.Remove(photoId);

    /// <summary>Forget everything (a different user).</summary>
    public void Clear() => known.Clear();

    /// <summary>The viewer's rows for these ids, in this order: the hydrated summary, else a minimal one.</summary>
    public IReadOnlyList<PhotoSummary> Summaries(IEnumerable<Guid> photoIds) =>
        [.. photoIds.Select(id => Get(id) ?? PhotoMappers.MinimalSummary(id))];
}
