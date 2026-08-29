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

        var fileBlob = new FileBlob
        {
            Id = Guid.NewGuid(),
            Content = content
        };

        var fileMetadata = new FileMetadata
        {
            Id = Guid.NewGuid(),
            UploadedByUserId = userId,
            FileName = validationService.SanitizeFileName(file.FileName),
            ContentType = file.ContentType,
            SizeBytes = file.Length,
            Sha256Hash = hash,
            FileBlobId = fileBlob.Id,
            Description = description?.Length > 256 ? description[..256] : description,
            UploadedAtUtc = timeProvider.GetUtcNow().UtcDateTime
        };

        await context.FileBlob.AddAsync(fileBlob, cancellationToken);
        await context.FileMetadata.AddAsync(fileMetadata, cancellationToken);
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

        if (metadata.FileBlob is not null)
        {
            context.FileBlob.Remove(metadata.FileBlob);
        }
        
        context.FileMetadata.Remove(metadata);
        await context.SaveChangesAsync(cancellationToken);

        return true;
    }
}