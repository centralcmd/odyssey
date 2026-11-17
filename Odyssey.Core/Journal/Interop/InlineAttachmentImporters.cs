using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.FileStorage.Files;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal.Originals;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Journal.Interop;

/// <summary>
/// Turns spooled inline FILES (<see cref="IcsAttachmentSpooler"/>) into Files-store ids for a task or journal import,
/// once per attachment and once per distinct content. Each is matched to the file it was exported from (same id, same
/// SHA-256 and size), else to the oldest visible file with that content, and stored as a new Journal-area file only
/// when neither matches — after the same <see cref="FileValidationService"/> checks an upload passes, the admin upload
/// cap included. A file stored earlier in the same import is reused by its content before it has even been saved; the
/// caller saves the staged rows with its own.
/// </summary>
internal sealed class InlineFileImporter(
    OdysseyContext context, FileValidationService validation, StoredFileWriter? writer, string userId,
    long maxFileBytes, long maxNewFileBytes, ImportSkipCollector skipped)
{
    private readonly Dictionary<string, Guid?> byReference = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> byContent = new(StringComparer.Ordinal);

    public int Stored { get; private set; }

    public int Reused { get; private set; }

    private IQueryable<FileMetadata> VisibleFiles => context.FileMetadata.Where(FileReferenceCounter.VisibleInFilesStore(context));

    public async Task<Guid?> ResolveAsync(SpooledAttachment attachment, string sampleTitle, CancellationToken cancellationToken)
    {
        if (byReference.TryGetValue(attachment.Reference, out var known))
        {
            return known;
        }

        var resolved = await ResolveOnceAsync(attachment, sampleTitle, cancellationToken);
        byReference[attachment.Reference] = resolved;
        return resolved;
    }

    private async Task<Guid?> ResolveOnceAsync(SpooledAttachment attachment, string sampleTitle, CancellationToken cancellationToken)
    {
        if (attachment.Problem is { } problem)
        {
            skipped.Add(InlineAttachmentReasons.Problem("file", problem, maxFileBytes), sampleTitle);
            return null;
        }

        var hash = attachment.Sha256Hash!;
        var contentKey = $"{hash}:{attachment.Size}";
        if (byContent.TryGetValue(contentKey, out var storedId))
        {
            Reused++;
            return storedId;
        }

        if (await FindExistingAsync(attachment.OriginalFileId, hash, attachment.Size, cancellationToken) is { } existingId)
        {
            Reused++;
            byContent[contentKey] = existingId;
            IcsAttachmentSpool.Delete(attachment);
            return existingId;
        }

        if (attachment.Size > maxNewFileBytes)
        {
            skipped.Add(InlineAttachmentReasons.OverUploadCap("file", maxNewFileBytes), sampleTitle);
            return null;
        }

        var path = attachment.Path!;
        var fileName = InlineAttachmentReasons.FileName(validation, attachment.FileName, "attachment");
        var contentType = attachment.ContentType ?? "application/octet-stream";
        try
        {
            validation.ValidateFile(new FileUpload(fileName, contentType, attachment.Size, () => File.OpenRead(path)), maxNewFileBytes);
        }
        catch (DomainValidationException)
        {
            skipped.Add("Attached file is of a type uploads do not accept, or its content does not match its type.", sampleTitle);
            return null;
        }

        if (writer is null)
        {
            throw new FileStorageUnavailableException("File storage is not set up on this server.");
        }

        FileMetadata stored;
        await using (var stream = File.OpenRead(path))
        {
            stored = await writer.StageNewFileAsync(
                context, StoredFileArea.Journal, stream, fileName, contentType, userId, description: null,
                Math.Max(1, attachment.Size), cancellationToken);
        }

        IcsAttachmentSpool.Delete(attachment);
        Stored++;
        byContent[contentKey] = stored.Id;
        return stored.Id;
    }

    private async Task<Guid?> FindExistingAsync(Guid? originalId, string sha256, long size, CancellationToken cancellationToken)
    {
        if (originalId is { } id)
        {
            var original = await VisibleFiles.AsNoTracking()
                .Where(f => f.Id == id && f.SizeBytes == size && f.Sha256Hash == sha256)
                .Select(f => (Guid?)f.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (original is not null)
            {
                return original;
            }
        }

        // Compared as stored (lower-case hex, the store's own format), so the Sha256Hash index can serve it.
        return await VisibleFiles.AsNoTracking()
            .Where(f => f.SizeBytes == size && f.Sha256Hash == sha256)
            .OrderBy(f => f.UploadedAtUtc).ThenBy(f => f.Id)
            .Select(f => (Guid?)f.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }
}

/// <summary>
/// Turns spooled inline PHOTOS (<c>X-ODYSSEY-KIND=PHOTO</c>) into library photo ids for a journal import, the photo
/// counterpart of <see cref="InlineFileImporter"/>. A photo is matched to the one it was exported from (same id and
/// content), else to any photo with that content — a legacy photo by its wrapped file's hash, as the upload's
/// duplicate check does — and otherwise uploaded through <see cref="PhotoService.StageUploadAsync"/>: the same type,
/// magic-byte, cap and metadata path as a library upload, carrying the exported title and caption, staged unsaved.
/// </summary>
internal sealed class InlinePhotoImporter(
    OdysseyContext context, PhotoService? photos, string userId, long maxFileBytes, ImportSkipCollector skipped)
{
    private readonly Dictionary<string, Guid?> byReference = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> byContent = new(StringComparer.Ordinal);

    public int Stored { get; private set; }

    public int Reused { get; private set; }

    public async Task<Guid?> ResolveAsync(SpooledAttachment attachment, string sampleTitle, CancellationToken cancellationToken)
    {
        if (byReference.TryGetValue(attachment.Reference, out var known))
        {
            return known;
        }

        var resolved = await ResolveOnceAsync(attachment, sampleTitle, cancellationToken);
        byReference[attachment.Reference] = resolved;
        return resolved;
    }

    private async Task<Guid?> ResolveOnceAsync(SpooledAttachment attachment, string sampleTitle, CancellationToken cancellationToken)
    {
        if (attachment.Problem is { } problem)
        {
            skipped.Add(InlineAttachmentReasons.Problem("photo", problem, maxFileBytes), sampleTitle);
            return null;
        }

        var hash = attachment.Sha256Hash!;
        var contentKey = $"{hash}:{attachment.Size}";
        if (byContent.TryGetValue(contentKey, out var storedId))
        {
            Reused++;
            return storedId;
        }

        if (await FindExistingAsync(attachment.OriginalPhotoId, hash, attachment.Size, cancellationToken) is { } existingId)
        {
            Reused++;
            byContent[contentKey] = existingId;
            IcsAttachmentSpool.Delete(attachment);
            return existingId;
        }

        if (photos is null)
        {
            throw new FileStorageUnavailableException("Photo storage is not set up on this server.");
        }

        var path = attachment.Path!;
        var upload = new FileUpload(
            InlineAttachmentReasons.FileName(new FileValidationService(), attachment.FileName, "photo"),
            attachment.ContentType ?? "application/octet-stream", attachment.Size, () => File.OpenRead(path));
        var request = new NewPhotoUpload
        {
            Title = Clamp(attachment.Title, PhotoLimits.MaxTitleLength),
            Caption = Clamp(attachment.Caption, PhotoLimits.MaxCaptionLength),
        };

        Photo photo;
        try
        {
            photo = await photos.StageUploadAsync(upload, request, userId, canAutoCreateTags: false, cancellationToken);
        }
        catch (PhotoTooLargeException ex)
        {
            skipped.Add($"Attached photo was refused: {ex.Message}", sampleTitle);
            return null;
        }
        catch (PhotoUnsupportedTypeException)
        {
            skipped.Add("Attached photo is not a JPEG, PNG, GIF or WebP image, or its content does not match its type.", sampleTitle);
            return null;
        }
        catch (DuplicatePhotoException duplicate)
        {
            // Raced in after the lookup above: the library already holds these bytes, so link that photo.
            Reused++;
            byContent[contentKey] = duplicate.ExistingPhotoId;
            IcsAttachmentSpool.Delete(attachment);
            return duplicate.ExistingPhotoId;
        }
        catch (DomainValidationException)
        {
            skipped.Add("Attached photo was refused by the upload checks.", sampleTitle);
            return null;
        }

        IcsAttachmentSpool.Delete(attachment);
        Stored++;
        byContent[contentKey] = photo.PhotoId;
        return photo.PhotoId;
    }

    private async Task<Guid?> FindExistingAsync(Guid? originalId, string sha256, long size, CancellationToken cancellationToken)
    {
        var described = PhotoOriginalSource.Describe(context, context.Photos.AsNoTracking());
        if (originalId is { } id)
        {
            var original = await described
                .Where(p => p.PhotoId == id && p.SizeBytes == size && p.Sha256Hash == sha256)
                .Select(p => (Guid?)p.PhotoId)
                .FirstOrDefaultAsync(cancellationToken);
            if (original is not null)
            {
                return original;
            }
        }

        return await described
            .Where(p => p.SizeBytes == size && p.Sha256Hash == sha256)
            .OrderBy(p => p.PhotoId)
            .Select(p => (Guid?)p.PhotoId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static string? Clamp(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value.Trim() : value[..max].Trim();
}

/// <summary>The skip reasons and name handling both importers share, so a task and a journal import word them alike.</summary>
internal static class InlineAttachmentReasons
{
    public static string Problem(string noun, string problem, long maxFileBytes) => problem switch
    {
        IcsAttachmentSpooler.TooLargeProblem => $"Attached {noun} is larger than the {maxFileBytes / (1024 * 1024)} MB limit.",
        IcsAttachmentSpooler.ChecksumProblem => $"Attached {noun} does not match its checksum.",
        IcsAttachmentSpooler.EmptyProblem => $"Attached {noun} is empty.",
        _ => $"Attached {noun} is damaged (not valid base64).",
    };

    public static string OverUploadCap(string noun, long maxBytes) =>
        $"Attached {noun} is larger than the {maxBytes / (1024 * 1024)} MB upload limit, and no stored {noun} matches it.";

    public static string FileName(FileValidationService validation, string? raw, string fallback)
    {
        var fileName = validation.SanitizeFileName(raw ?? string.Empty);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = fallback;
        }

        return fileName.Length > FileValidationService.MaxFileNameLength
            ? fileName[..FileValidationService.MaxFileNameLength].TrimEnd()
            : fileName;
    }
}
