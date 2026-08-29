using Microsoft.EntityFrameworkCore;
using Odyssey.Context;

namespace Odyssey.Core.Finance;

public sealed class ImageContentReader(OdysseyContext context) : IImageContentReader
{
    public async Task<byte[]?> ReadPrefixAsync(Guid fileId, int maxBytes, CancellationToken cancellationToken = default)
    {
        if (maxBytes <= 0)
        {
            return null;
        }

        if (context.Database.IsRelational())
        {
            // Read only a bounded prefix of the blob, projected in SQL, so a potentially large image is
            // never fully materialised just to parse its metadata headers (§5.4/§11). This needs raw
            // SQL: EF/LINQ can't express a byte-range read of a varbinary column — selecting b.Content
            // would pull the whole blob, and there's no EF.Functions SUBSTRING for byte[]. (SqlQuery
            // requires the projected scalar column be aliased "Value".)
            var prefixes = await context.Database
                .SqlQuery<byte[]>(
                    $@"SELECT SUBSTRING(b.`Content`, 1, {maxBytes}) AS `Value`
                       FROM `FileMetadata` m
                       JOIN `FileBlob` b ON m.`FileBlobId` = b.`Id`
                       WHERE m.`Id` = {fileId}")
                .ToListAsync(cancellationToken);

            return prefixes.Count == 0 ? null : prefixes[0];
        }

        // Non-relational (InMemory tests): materialise the blob and slice in memory.
        var content = await context.FileMetadata
            .Where(m => m.Id == fileId)
            .Join(context.FileBlob, m => m.FileBlobId, b => b.Id, (_, b) => b.Content)
            .FirstOrDefaultAsync(cancellationToken);

        if (content is null)
        {
            return null;
        }

        return content.Length <= maxBytes ? content : content[..maxBytes];
    }
}
