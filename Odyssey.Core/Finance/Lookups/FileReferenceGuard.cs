using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.FileStorage.Files;

namespace Odyssey.Core.Finance;

/// <summary>
/// <see cref="IFileReferenceGuard"/> over <see cref="OdysseyContext"/>.
/// </summary>
/// <param name="store">Releases a file on disk for erasure (issue #448); null only where no file on disk can exist.</param>
public sealed class FileReferenceGuard(OdysseyContext context, IObjectStore? store = null) : IFileReferenceGuard
{
    public async Task<IReadOnlyList<string>> DescribeNonPhotoReferencesAsync(
        Guid fileId, CancellationToken cancellationToken = default)
    {
        // One query per holder rather than a UNION: the set is small and fixed, and naming each table
        // explicitly is what makes a newly-added attachment table a visible omission here instead of a
        // silent one. Photos are excluded — the caller is deleting the photo, so it is not "something
        // else still using the file".
        var references = new List<string>();

        if (await context.TransactionFiles.AnyAsync(f => f.FileMetadataId == fileId, cancellationToken))
        {
            references.Add("a transaction attachment");
        }

        if (await context.AccountFiles.AnyAsync(f => f.FileMetadataId == fileId, cancellationToken))
        {
            references.Add("an account document");
        }

        if (await context.TaxStatementFiles.AnyAsync(f => f.FileMetadataId == fileId, cancellationToken))
        {
            references.Add("a tax-statement document");
        }

        if (await context.ContractFiles.AnyAsync(f => f.FileMetadataId == fileId, cancellationToken))
        {
            references.Add("a contract document");
        }

        if (await context.PropertyFiles.AnyAsync(f => f.FileMetadataId == fileId, cancellationToken))
        {
            references.Add("a property document");
        }

        if (await context.JournalEntryAttachments.AnyAsync(a => a.FileId == fileId, cancellationToken))
        {
            references.Add("a journal-entry attachment");
        }

        if (await context.JournalTaskAttachments.AnyAsync(a => a.FileId == fileId, cancellationToken))
        {
            references.Add("a task attachment");
        }

        return references;
    }

    public async Task DeleteFileAndBlobAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        var metadata = await context.FileMetadata
            .FirstOrDefaultAsync(fm => fm.Id == fileId, cancellationToken);

        if (metadata is null)
        {
            return;
        }

        await StoredFileRemoval.StageRemoveAsync(context, store, metadata, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }
}
