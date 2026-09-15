using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Core.Finance;

public class FileService
{
    private readonly OdysseyContext context;
    private readonly FileValidationService validationService;
    private readonly TimeProvider timeProvider;

    public FileService(OdysseyContext context, FileValidationService validationService, TimeProvider? timeProvider = null)
    {
        this.context = context;
        this.validationService = validationService;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<FileUploadResponse> UploadFileAsync(IFormFile file, string userId, string? description, CancellationToken cancellationToken = default)
    {
        await validationService.ValidateFileAsync(file, cancellationToken);

        // Compute hash
        await using var stream = file.OpenReadStream();
        var hash = await validationService.ComputeSha256HashAsync(stream);
        stream.Position = 0; // Reset stream for reading content

        var content = new byte[file.Length];
        await stream.ReadExactlyAsync(content, cancellationToken);

        var fileMetadata = StageUpload(
            content,
            validationService.SanitizeFileName(file.FileName),
            file.ContentType,
            userId,
            description,
            precomputedHash: hash);

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

    /// <summary>
    /// Queues a new <see cref="FileBlob"/> + <see cref="FileMetadata"/> pair onto the caller's context
    /// and returns the metadata, <b>without saving</b> (issue #86 §5.6) — the same shape
    /// <c>SecretSettingsService.StageClearAsync</c> was carved out for in issue #8.
    ///
    /// <para>
    /// This exists because <see cref="UploadFileAsync"/> is not composable: it commits, it takes an
    /// <see cref="IFormFile"/> (which the vCard import path does not have at all), and it hashes the
    /// <i>original</i> stream — so it can store neither a stripped body nor a generated filename. A
    /// contact avatar must stage its file, release the previous one and repoint the contact in ONE
    /// transaction, which a method that saves cannot take part in.
    /// </para>
    ///
    /// <para>
    /// It performs <b>no validation</b>. The caller owns that, because the two callers validate
    /// differently: an ordinary upload against the global allow-list and cap, an avatar against the
    /// narrower image policy that also strips and re-verifies the bytes it hands in here.
    /// </para>
    /// </summary>
    /// <param name="content">The exact bytes to store — already stripped, where the caller strips.</param>
    /// <param name="fileName">A <b>generated</b> name for an avatar; never the user's original filename.</param>
    /// <param name="contentType">The <b>validated</b> content type, not the client-declared one.</param>
    /// <param name="precomputedHash">The SHA-256 when the caller already has it; otherwise it is computed here.</param>
    public FileMetadata StageUpload(
        byte[] content,
        string fileName,
        string contentType,
        string? userId,
        string? description,
        string? precomputedHash = null)
    {
        var fileBlob = new FileBlob
        {
            Id = Guid.NewGuid(),
            Content = content
        };

        var fileMetadata = new FileMetadata
        {
            Id = Guid.NewGuid(),
            UploadedByUserId = userId,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = content.LongLength,
            Sha256Hash = precomputedHash ?? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant(),
            FileBlobId = fileBlob.Id,
            Description = description?.Length > 256 ? description[..256] : description,
            UploadedAtUtc = timeProvider.GetUtcNow().UtcDateTime
        };

        context.FileBlob.Add(fileBlob);
        context.FileMetadata.Add(fileMetadata);

        return fileMetadata;
    }

    public async Task<FileMetadataResponse?> GetFileMetadataAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        var metadata = await context.FileMetadata
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
        var metadata = await context.FileMetadata
            .Include(fm => fm.FileBlob)
            .AsNoTracking()
            .FirstOrDefaultAsync(fm => fm.Id == fileId, cancellationToken);

        if (metadata?.FileBlob is null)
        {
            return (null, null);
        }

        var contentStream = new MemoryStream(metadata.FileBlob.Content);
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
        var q = context.FileMetadata.AsNoTracking().AsQueryable();

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

    public async Task<FileMetadataResponse?> UpdateFileMetadataAsync(Guid fileId, UpdateFileMetadataRequest request, CancellationToken cancellationToken = default)
    {
        var metadata = await context.FileMetadata.FirstOrDefaultAsync(fm => fm.Id == fileId, cancellationToken);

        if (metadata == null)
        {
            return null;
        }

        metadata.Description = request.Description?.Length > 256 ? request.Description[..256] : request.Description;

        // Optional rename. Only applied when a non-blank name is supplied so existing
        // description-only callers leave the file name untouched. FileName is MaxLength(256).
        if (!string.IsNullOrWhiteSpace(request.FileName))
        {
            var trimmed = request.FileName.Trim();
            metadata.FileName = trimmed.Length > 256 ? trimmed[..256] : trimmed;
        }

        await context.SaveChangesAsync(cancellationToken);

        return new FileMetadataResponse(
            metadata.Id,
            metadata.FileName,
            metadata.ContentType,
            metadata.SizeBytes,
            metadata.Sha256Hash,
            metadata.UploadedAtUtc,
            metadata.Description);
    }

    public async Task<bool> DeleteFileAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        var metadata = await context.FileMetadata
            .Include(fm => fm.FileBlob)
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

        if (metadata.FileBlob is not null)
        {
            context.FileBlob.Remove(metadata.FileBlob);
        }

        context.FileMetadata.Remove(metadata);
        await context.SaveChangesAsync(cancellationToken);

        return true;
    }
}