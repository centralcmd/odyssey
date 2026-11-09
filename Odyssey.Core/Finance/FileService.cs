using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Microsoft.EntityFrameworkCore;
using Odyssey.Core.FileStorage;
using Odyssey.Core.FileStorage.Files;

namespace Odyssey.Core.Finance;

public class FileService
{
    private readonly OdysseyContext context;
    private readonly FileValidationService validationService;
    private readonly TimeProvider timeProvider;

    /// <summary>A rename that collides with a concurrent write an observer depends on is re-run at most this often.</summary>
    public const int RenameAttempts = 3;

    private readonly IReadOnlyList<IFileRenameObserver> renameObservers;
    private readonly StoredFileWriter? writer;
    private readonly IObjectStore? store;
    private readonly StoredFileContentSource source;

    /// <param name="writer">The file store's writer (issue #448): every upload goes to disk. Null only in a host with no
    /// file store, where an upload is a <c>503</c> and every read is of a legacy file.</param>
    public FileService(
        OdysseyContext context,
        FileValidationService validationService,
        TimeProvider? timeProvider = null,
        IEnumerable<IFileRenameObserver>? observers = null,
        StoredFileWriter? writer = null,
        IObjectStore? store = null,
        StoredFileContentSource? source = null)
    {
        this.context = context;
        this.validationService = validationService;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        renameObservers = [.. observers ?? []];
        this.writer = writer;
        this.store = store;
        this.source = source ?? new StoredFileContentSource(context, store);
    }

    /// <summary>The files <c>/api/files</c> may show: every one but a photo's own original (issue #428).</summary>
    private IQueryable<FileMetadata> VisibleFiles => context.FileMetadata.Where(FileReferenceCounter.VisibleInFilesStore(context));

    /// <summary>
    /// Validates the upload, writes it to <paramref name="area"/>'s folder on the file store and creates its
    /// <see cref="FileMetadata"/> in one save (issue #448 §3.3). The body is streamed from the caller's spooled form file —
    /// never copied into a <c>byte[]</c> — and the size and hash recorded are the store's measurement of what it wrote.
    /// </summary>
    public async Task<FileUploadResponse> UploadFileAsync(
        FileUpload file, FileUploadArea area, string userId, string? description, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(area))
        {
            throw new DomainValidationException("Choose where the file is stored: Finance or Journal.", code: null, field: "area");
        }

        await validationService.ValidateFileAsync(file, cancellationToken);
        if (writer is null)
        {
            throw new FileStorageUnavailableException("File storage is not set up on this server.");
        }

        FileMetadata fileMetadata;
        await using (var stream = file.OpenReadStream())
        {
            fileMetadata = await writer.StageNewFileAsync(
                context, StoredFileAreas.For(area), stream, validationService.SanitizeFileName(file.FileName), file.ContentType,
                userId, description, Math.Max(1, file.Length), cancellationToken);
        }

        await context.SaveChangesAsync(cancellationToken);

        return new FileUploadResponse(
            fileMetadata.Id,
            fileMetadata.FileName,
            fileMetadata.ContentType,
            fileMetadata.SizeBytes,
            fileMetadata.Sha256Hash,
            fileMetadata.UploadedAtUtc,
            fileMetadata.Description);
    }

    public async Task<FileMetadataResponse?> GetFileMetadataAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        var metadata = await VisibleFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(fm => fm.Id == fileId, cancellationToken);

        if (metadata is null)
        {
            return null;
        }

        return new FileMetadataResponse(
            metadata.Id,
            metadata.FileName,
            metadata.ContentType,
            metadata.SizeBytes,
            metadata.Sha256Hash,
            metadata.UploadedAtUtc,
            metadata.Description);
    }

    public async Task<(FileMetadataResponse? Metadata, Stream? Content)> GetFileContentAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        var metadata = await VisibleFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(fm => fm.Id == fileId, cancellationToken);
        if (metadata is null)
        {
            return (null, null);
        }

        // The one read path (issue #448 §3.4): a file on disk streams from the store, a legacy one from its blob.
        var content = await source.OpenAsync(VisibleFiles.Where(fm => fm.Id == fileId), cancellationToken);
        if (content is null)
        {
            return (null, null);
        }

        var contentStream = content.Content;
        var response = new FileMetadataResponse(
            metadata.Id,
            metadata.FileName,
            metadata.ContentType,
            metadata.SizeBytes,
            metadata.Sha256Hash,
            metadata.UploadedAtUtc,
            metadata.Description);

        return (response, contentStream);
    }

    /// <summary>Server-side paged list (issue #277): filename search + date/kind filters + allowlisted sort, returning a total count.</summary>
    public async Task<PagedResult<FileListItem>> ListAsync(
        FilesQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var q = VisibleFiles.AsNoTracking();

        var term = ListQuery.NormalizeSearch(query.Search);
        if (term is not null)
        {
            var pattern = ListQuery.ContainsPattern(term);
            q = q.Where(fm => EF.Functions.Like(fm.FileName, pattern));
        }

        if (query.UploadedFromUtc is { } from)
        {
            q = q.Where(fm => fm.UploadedAtUtc >= from);
        }
        if (query.UploadedToUtc is { } to)
        {
            q = q.Where(fm => fm.UploadedAtUtc <= to);
        }

        // `kind` derives from the MIME type via a SQL-translatable prefix mapping (PDF · Image · File).
        q = query.Kind switch
        {
            FileKind.Pdf => q.Where(fm => fm.ContentType == "application/pdf"),
            FileKind.Image => q.Where(fm => fm.ContentType.StartsWith("image/")),
            FileKind.File => q.Where(fm => fm.ContentType != "application/pdf" && !fm.ContentType.StartsWith("image/")),
            _ => q,
        };

        var ascending = ListQuery.Ascending(query.SortDir, naturalDefaultAscending: query.SortBy is FileSortBy.Name or FileSortBy.Kind);
        IOrderedQueryable<FileMetadata> sorted = query.SortBy switch
        {
            FileSortBy.Name => ascending ? q.OrderBy(fm => fm.FileName) : q.OrderByDescending(fm => fm.FileName),
            FileSortBy.Size => ascending ? q.OrderBy(fm => fm.SizeBytes) : q.OrderByDescending(fm => fm.SizeBytes),
            FileSortBy.Kind => ascending
                ? q.OrderBy(fm => fm.ContentType == "application/pdf" ? 0 : fm.ContentType.StartsWith("image/") ? 1 : 2)
                : q.OrderByDescending(fm => fm.ContentType == "application/pdf" ? 0 : fm.ContentType.StartsWith("image/") ? 1 : 2),
            _ => ascending ? q.OrderBy(fm => fm.UploadedAtUtc) : q.OrderByDescending(fm => fm.UploadedAtUtc),
        };
        q = sorted.ThenBy(fm => fm.Id);

        return await q.ToPagedResultAsync(
            query.Offset, query.Limit,
            fm => new FileListItem(fm.Id, fm.FileName, fm.ContentType, fm.SizeBytes, fm.UploadedAtUtc, fm.Description),
            cancellationToken);
    }

    /// <summary>
    /// The name a rename to <paramref name="fileName"/> would store, or a
    /// <see cref="Odyssey.Core.DomainValidationException"/> when nothing survives sanitization. Exposed so a
    /// caller that commits other work before the rename (the photo edit) can refuse up front rather
    /// than half-commit.
    /// </summary>
    public string SanitizeRenamedFileName(string fileName) => validationService.SanitizeRenamedFileName(fileName);

    public async Task<FileMetadataResponse?> UpdateFileMetadataAsync(Guid fileId, UpdateFileMetadataRequest request, CancellationToken cancellationToken = default)
    {
        var metadata = await VisibleFiles.FirstOrDefaultAsync(fm => fm.Id == fileId, cancellationToken);

        if (metadata == null)
        {
            return null;
        }

        // Optional rename. Only applied when a non-blank name is supplied so existing
        // description-only callers leave the file name untouched. The name goes through the same
        // sanitizer as an upload (issue #247), and is resolved before anything is mutated so a refused
        // name leaves the tracked row untouched: a stored CR/LF or quote reaches every consumer of the
        // name, not only the download header.
        var renamedTo = string.IsNullOrWhiteSpace(request.FileName)
            ? null
            : validationService.SanitizeRenamedFileName(request.FileName);

        metadata.Description = request.Description?.Length > 256 ? request.Description[..256] : request.Description;

        // Observers run only on a real change of name (issue #362 §3.1 item 5) — saving the same name must not
        // regenerate anything — and before the save, so what they stage lands in the rename's own save.
        var renaming = renamedTo is not null && !string.Equals(renamedTo, metadata.FileName, StringComparison.Ordinal);
        if (renamedTo is not null)
        {
            metadata.FileName = renamedTo;
        }

        if (!renaming || renameObservers.Count == 0)
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        else
        {
            await SaveRenameWithObserversAsync(fileId, cancellationToken);
        }

        return new FileMetadataResponse(
            metadata.Id,
            metadata.FileName,
            metadata.ContentType,
            metadata.SizeBytes,
            metadata.Sha256Hash,
            metadata.UploadedAtUtc,
            metadata.Description);
    }

    /// <summary>
    /// Runs the observers and saves. A concurrency failure on an entity an observer owns (a worker wrote between our
    /// load and our save) reloads those entries and re-runs the observers, at most <see cref="RenameAttempts"/> times;
    /// then <see cref="FileRenameBusyException"/>, with nothing committed. Any other failure propagates as before.
    /// </summary>
    private async Task SaveRenameWithObserversAsync(Guid fileId, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            foreach (var observer in renameObservers)
            {
                await observer.OnFileRenamingAsync(fileId, cancellationToken);
            }

            try
            {
                await context.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException ex)
                when (ex.Entries.Count > 0 && ex.Entries.All(e => renameObservers.Any(o => o.Owns(e.Metadata.ClrType))))
            {
                if (attempt >= RenameAttempts)
                {
                    throw new FileRenameBusyException();
                }

                // Everything the observers staged, not only the conflicting entry: a provider that applied part of the
                // failed save (EF InMemory) must not leave a stale token behind for the retry to trip on.
                foreach (var entry in context.ChangeTracker.Entries()
                             .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                                 && renameObservers.Any(o => o.Owns(e.Metadata.ClrType)))
                             .ToList())
                {
                    if (entry.State == EntityState.Added)
                    {
                        entry.State = EntityState.Detached;
                    }
                    else
                    {
                        await entry.ReloadAsync(cancellationToken);
                    }
                }
            }
        }
    }

    public async Task<bool> DeleteFileAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        var metadata = await VisibleFiles
            .FirstOrDefaultAsync(fm => fm.Id == fileId, cancellationToken);

        if (metadata is null)
        {
            return false;
        }

        // The application-level counterpart to Contact.AvatarFileId's ON DELETE SET NULL (issue #86 §6).
        // The real constraint does this in MariaDB, but the EF InMemory provider enforces no foreign
        // keys at all, so without this the fast test tiers would exercise none of the graceful detach —
        // and a contact whose image file was deleted from the Files page must simply fall back to its
        // type glyph, never fail. Tracked update, not ExecuteUpdateAsync, for the same provider reason.
        var referencing = await context.Contacts
            .Where(c => c.AvatarFileId == fileId)
            .ToListAsync(cancellationToken);
        foreach (var contact in referencing)
        {
            contact.AvatarFileId = null;
        }

        // The blob goes with a legacy file; a file on disk is released for erasure after the deletion grace (issue #448).
        await StoredFileRemoval.StageRemoveAsync(context, store, metadata, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        return true;
    }
}