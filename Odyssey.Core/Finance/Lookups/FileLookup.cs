using Microsoft.EntityFrameworkCore;
using Odyssey.Context;

namespace Odyssey.Core.Finance;

public sealed class FileLookup(OdysseyContext context) : IFileLookup
{
    private static readonly HashSet<string> ImageContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/jpg",
        "image/png",
        "image/gif",
        "image/webp",
    };

    public async Task<IReadOnlySet<Guid>> ExistingIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var found = await context.FileMetadata
            .Where(f => ids.Contains(f.Id))
            .Select(f => f.Id)
            .ToListAsync(ct);

        return found.ToHashSet();
    }

    public async Task<IReadOnlySet<Guid>> ExistingImageIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var candidates = await context.FileMetadata
            .Where(f => ids.Contains(f.Id))
            .Select(f => new { f.Id, f.ContentType })
            .ToListAsync(ct);

        return candidates
            .Where(f => ImageContentTypes.Contains(f.ContentType))
            .Select(f => f.Id)
            .ToHashSet();
    }
}
