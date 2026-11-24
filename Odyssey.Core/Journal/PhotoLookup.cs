using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.Journal.Originals;

namespace Odyssey.Core.Journal;

/// <summary>The <see cref="IPhotoLookup"/> implementation. Read-only: it never creates a photo (issue #428).</summary>
public sealed class PhotoLookup(OdysseyContext context) : IPhotoLookup
{
    public async Task<IReadOnlySet<Guid>> ExistingIdsAsync(IReadOnlyCollection<Guid> photoIds, CancellationToken cancellationToken = default)
    {
        if (photoIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var ids = photoIds.Distinct().ToList();
        var found = await context.Photos
            .Where(p => ids.Contains(p.PhotoId))
            .Select(p => p.PhotoId)
            .ToListAsync(cancellationToken);

        return found.ToHashSet();
    }

    public async Task<IReadOnlyDictionary<Guid, PhotoLinkInfo>> ResolveAsync(IReadOnlyCollection<Guid> photoIds, CancellationToken cancellationToken = default)
    {
        if (photoIds.Count == 0)
        {
            return new Dictionary<Guid, PhotoLinkInfo>();
        }

        var ids = photoIds.Distinct().ToList();
        var rows = await context.Photos
            .Where(p => ids.Contains(p.PhotoId))
            .Select(p => new { p.PhotoId, p.Title, p.Favourited, p.Archived })
            .ToDictionaryAsync(p => p.PhotoId, cancellationToken);
        var names = await PhotoOriginalSource.Describe(context, context.Photos.Where(p => ids.Contains(p.PhotoId)))
            .Select(i => new { i.PhotoId, i.FileName })
            .ToListAsync(cancellationToken);

        return names.ToDictionary(n => n.PhotoId, n =>
        {
            var row = rows.GetValueOrDefault(n.PhotoId);
            return new PhotoLinkInfo(n.PhotoId, row?.Title, n.FileName, row?.Favourited, row?.Archived);
        });
    }

    public async Task<IReadOnlyDictionary<Guid, Guid>> ResolveReferencesAsync(
        IReadOnlyCollection<Guid> referenceIds, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<Guid, Guid>();
        if (referenceIds.Count == 0)
        {
            return result;
        }

        var ids = referenceIds.Distinct().ToList();
        foreach (var id in await context.Photos.Where(p => ids.Contains(p.PhotoId)).Select(p => p.PhotoId).ToListAsync(cancellationToken))
        {
            result[id] = id;
        }

        var rest = ids.Where(id => !result.ContainsKey(id)).ToList();
        if (rest.Count > 0)
        {
            var byFile = await context.Photos
                .Where(p => p.LegacyFileId != null && rest.Contains(p.LegacyFileId.Value))
                .Select(p => new { p.PhotoId, p.LegacyFileId })
                .ToListAsync(cancellationToken);
            foreach (var row in byFile)
            {
                result.TryAdd(row.LegacyFileId!.Value, row.PhotoId);
            }
        }

        return result;
    }
}
