using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Core.FileStorage;
using Odyssey.Core.FileStorage.Files;
using Odyssey.Core.Journal.Interop;
using Odyssey.Core.Journal.Originals;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Journal;

/// <summary>The two kinds of calendar link (issue #521 v3 §A.1).</summary>
internal enum CalendarLinkKind
{
    File,
    Photo,
}

/// <summary>One <c>ATTACH</c> of one imported component, as far as it has been resolved.</summary>
internal sealed class CalendarAttachSlot
{
    public required CalendarLinkKind Kind { get; init; }

    public CalendarAttachSlotState State { get; set; }

    /// <summary>The stored content it names, once resolved.</summary>
    public Guid? Id { get; set; }

    /// <summary>New content written as a reservation in phase A, attached in phase B.</summary>
    public CalendarAttachmentImporter.Reservation? Reservation { get; set; }

    /// <summary>A reference's id, declared hash and size (§8.3).</summary>
    public Guid? ReferenceId { get; init; }

    public string? ReferenceHash { get; init; }

    public long? ReferenceSize { get; init; }
}

internal enum CalendarAttachSlotState
{
    /// <summary>Names stored content: <see cref="CalendarAttachSlot.Id"/>.</summary>
    Resolved,

    /// <summary>New content, reserved: <see cref="CalendarAttachSlot.Reservation"/>.</summary>
    Reserved,

    /// <summary>A reference, resolved per batch.</summary>
    Reference,

    /// <summary>Not linkable; its reason is already counted.</summary>
    Skipped,

    /// <summary>On a component whose target is an occurrence: stored nowhere.</summary>
    Ignored,
}

/// <summary>
/// The calendar import's attachment pipeline (issue #521 §8.3, v4 §1, v5 §1, implementation notes 1–5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Phase A</b>, outside any lock and once per batch: each inline attachment is decoded to a temp file, hashed and
/// validated, matched read-only against the cross-batch cache and then the database, and — when new — written to its
/// store as a <c>Pending</c> reservation; the temp file is deleted before the next attachment is decoded. A reservation
/// never becomes a <c>FileMetadata</c> or <c>Photo</c> here, so a batch that fails leaves nothing visible.
/// </para>
/// <para>
/// <b>Phase B</b>, inside each attempt of the batch's retried transaction: every reservation is re-checked against
/// content stored concurrently, then a FRESH row is attached per attempt. Nothing is decoded or written twice.
/// </para>
/// </remarks>
internal sealed partial class CalendarAttachmentImporter(
    OdysseyContext context, FileValidationService validation, StoredFileWriter? writer, PhotoService? photos,
    string userId, long maxNewBytes, string spoolDirectory, Func<string, long> freeSpace, ILogger logger)
{
    /// <summary>
    /// Staged reservation bytes at which a batch closes early (v3 §B.1, v4 §1). Inside one component the same ceiling
    /// skips further new content once reached, so a batch holds at most this plus one attachment (v5 §2).
    /// </summary>
    public const long BatchReservedBytesCeiling = 256L * 1024 * 1024;

    /// <summary>The cross-batch cache's capacity (v3 §B.4): the journal's <c>MaxInlineAttachments</c>.</summary>
    public const int CacheCapacity = 10_000;

    public const string UriReason = "Linked (URI) attachments are not imported.";

    public const string UnresolvedReason = "Attached content is not in this file or this instance.";

    public const string InvalidReferenceReason = "An attachment reference is malformed.";

    public const string TooManyLinesReason = "An event carries more attachment lines than one import reads; the rest were skipped.";

    public const string BatchBudgetReason =
        "An event carries more new attached content than one import batch stages; the rest of it was skipped.";

    public const string ExpiredReservationMessage =
        "An attached file's storage reservation expired before the import could save it; importing the same file again completes it.";

    public static string CappedReason(int cap) =>
        $"An event or series may link at most {cap} files and {cap} photos; the rest were skipped.";

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HashPattern();

    /// <summary>New content written in phase A, waiting to be attached.</summary>
    internal sealed class Reservation
    {
        public required CalendarLinkKind Kind { get; init; }

        public required string Key { get; init; }

        public PreparedStoredFile? File { get; init; }

        public PreparedPhoto? Photo { get; init; }

        public long SizeBytes => File?.SizeBytes ?? Photo!.SizeBytes;

        public string Sha256Hash => File?.Sha256Hash ?? Photo!.Sha256Hash;
    }

    /// <summary>What one attempt attached, and what it found already stored instead.</summary>
    internal sealed class Attempt
    {
        public Dictionary<Reservation, Guid> Attached { get; } = [];

        public Dictionary<Reservation, Guid> Matched { get; } = [];
    }

    private readonly Dictionary<string, LinkedListNode<(string Key, Guid Id)>> cacheIndex = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, Guid Id)> cacheOrder = new();
    private readonly Dictionary<string, CalendarAttachSlot> batchContent = new(StringComparer.Ordinal);
    private readonly List<Reservation> batchReservations = [];
    private int batchReusedFiles;
    private int batchReusedPhotos;

    /// <summary>
    /// The importer's claims, enforced again where content is stored or linked (PR #529 review S1): pass 1's file-level
    /// check is the early refusal, this is the one no parsing difference can route around.
    /// </summary>
    public CalendarIcsImportClaims Claims { get; init; } = CalendarIcsImportClaims.None;

    /// <summary>The staged-reservation ceiling: a batch never holds more than this plus one attachment (v5 §2).</summary>
    public long ReservedBytesCeiling { get; init; } = BatchReservedBytesCeiling;

    private void Demand(CalendarLinkKind kind, bool store)
    {
        var missing = new List<string>();
        if (kind == CalendarLinkKind.File)
        {
            if (!Claims.FilesRead)
            {
                missing.Add(Odyssey.Dtos.Authorization.PermissionClaims.FilesRead);
            }

            if (store && !Claims.FilesCreate)
            {
                missing.Add(Odyssey.Dtos.Authorization.PermissionClaims.FilesCreate);
            }
        }
        else
        {
            if (!Claims.PhotosRead)
            {
                missing.Add(Odyssey.Dtos.Authorization.PermissionClaims.PhotosRead);
            }

            if (store && !Claims.PhotosCreate)
            {
                missing.Add(Odyssey.Dtos.Authorization.PermissionClaims.PhotosCreate);
            }
        }

        if (missing.Count > 0)
        {
            throw CalendarAttachmentsException.ImportForbidden(missing);
        }
    }

    /// <summary>The slots phase A produced for the component being read, by spool placeholder.</summary>
    public Dictionary<string, CalendarAttachSlot> ComponentSlots { get; } = new(StringComparer.Ordinal);

    public long BatchReservedBytes { get; private set; }

    public int StoredFiles { get; private set; }

    public int ReusedFiles { get; private set; }

    public int StoredPhotos { get; private set; }

    public int ReusedPhotos { get; private set; }

    /// <summary>The decode cap: the admin upload cap, so the free-disk check below holds for any one attachment.</summary>
    public long MaxDecodeBytes => maxNewBytes;

    private static string KeyOf(CalendarLinkKind kind, string hash, long size) =>
        $"{(kind == CalendarLinkKind.Photo ? 'p' : 'f')}:{hash}:{size}";

    // ------------------------------------------------------------------ Phase A

    /// <summary>
    /// Whether an inline value can be streamed past undecoded (v3 §B.2, v4 §2): it declares a well-formed hash and size
    /// whose content this import has already VERIFIED — never on the strength of a declaration alone.
    /// </summary>
    public bool CanSkip(SpooledAttachment attachment)
    {
        if (attachment.DeclaredSha256 is not { } hash || !HashPattern().IsMatch(hash) || attachment.DeclaredSize is not { } size)
        {
            return false;
        }

        var key = KeyOf(attachment.IsPhoto ? CalendarLinkKind.Photo : CalendarLinkKind.File, hash, size);
        return batchContent.ContainsKey(key) || cacheIndex.ContainsKey(key);
    }

    /// <summary>The free-disk check before each decode (v4 §1): at least 1.25 × the upload cap.</summary>
    public void EnsureTempSpace()
    {
        if (freeSpace(spoolDirectory) < maxNewBytes + (maxNewBytes / 4))
        {
            throw new FileStorageFullException(
                "There is not enough free temporary disk space to read an attached file. Try again later.");
        }
    }

    /// <summary>The slot of an attachment streamed past by <see cref="CanSkip"/>.</summary>
    public CalendarAttachSlot Skimmed(SpooledAttachment attachment, ImportSkip skip)
    {
        var kind = attachment.IsPhoto ? CalendarLinkKind.Photo : CalendarLinkKind.File;
        Demand(kind, store: true);
        var key = KeyOf(kind, attachment.DeclaredSha256!, attachment.DeclaredSize!.Value);
        if (batchContent.TryGetValue(key, out var known))
        {
            CountReuse(kind);
            return Copy(known);
        }

        if (Cached(key) is { } id)
        {
            CountReuse(kind);
            return new CalendarAttachSlot { Kind = kind, State = CalendarAttachSlotState.Resolved, Id = id };
        }

        skip(UnresolvedReason);
        return new CalendarAttachSlot { Kind = kind, State = CalendarAttachSlotState.Skipped };
    }

    /// <summary>A counted skip with fixed text and no sample (v3 §E).</summary>
    public delegate void ImportSkip(string reason);

    /// <summary>
    /// Phase A for one decoded attachment. <paramref name="isOccurrence"/> is asked only for NEW content, at most once
    /// per component: a component whose target is a generated occurrence reserves nothing (implementation note 4).
    /// The temp file is deleted before this returns, whatever happened.
    /// </summary>
    public async Task<CalendarAttachSlot> ResolveInlineAsync(
        SpooledAttachment attachment, Func<Task<bool>> isOccurrence, ImportSkip skip, CancellationToken cancellationToken)
    {
        var kind = attachment.IsPhoto ? CalendarLinkKind.Photo : CalendarLinkKind.File;
        try
        {
            // Inline content needs the kind's read AND create claims, whatever it turns out to match (goal 6).
            Demand(kind, store: true);
            if (attachment.Problem is { } problem)
            {
                skip(InlineAttachmentReasons.Problem(kind == CalendarLinkKind.Photo ? "photo" : "file", problem, maxNewBytes));
                return new CalendarAttachSlot { Kind = kind, State = CalendarAttachSlotState.Skipped };
            }

            var hash = attachment.Sha256Hash!;
            var key = KeyOf(kind, hash, attachment.Size);
            if (batchContent.TryGetValue(key, out var known))
            {
                CountReuse(kind);
                return Copy(known);
            }

            var existing = Cached(key) ?? await FindStoredAsync(
                kind, kind == CalendarLinkKind.Photo ? attachment.OriginalPhotoId : attachment.OriginalFileId,
                hash, attachment.Size, cancellationToken);
            if (existing is not null)
            {
                CountReuse(kind);
                var resolved = new CalendarAttachSlot { Kind = kind, State = CalendarAttachSlotState.Resolved, Id = existing };
                batchContent[key] = resolved;
                return resolved;
            }

            if (await isOccurrence())
            {
                return new CalendarAttachSlot { Kind = kind, State = CalendarAttachSlotState.Ignored };
            }

            // The batch holds at most the ceiling plus one attachment (v5 §2): once a component has carried the batch to
            // the ceiling, its further new content is skipped and counted — a component cannot be split across batches.
            if (BatchReservedBytes >= ReservedBytesCeiling)
            {
                skip(BatchBudgetReason);
                return new CalendarAttachSlot { Kind = kind, State = CalendarAttachSlotState.Skipped };
            }

            var reservation = kind == CalendarLinkKind.Photo
                ? await PreparePhotoAsync(attachment, key, skip, cancellationToken)
                : await PrepareFileAsync(attachment, key, skip, cancellationToken);
            if (reservation is null)
            {
                return new CalendarAttachSlot { Kind = kind, State = CalendarAttachSlotState.Skipped };
            }

            batchReservations.Add(reservation);
            BatchReservedBytes += reservation.SizeBytes;
            var slot = new CalendarAttachSlot { Kind = kind, State = CalendarAttachSlotState.Reserved, Reservation = reservation };
            batchContent[key] = slot;
            return slot;
        }
        finally
        {
            IcsAttachmentSpool.Delete(attachment);
        }
    }

    private async Task<Reservation?> PrepareFileAsync(
        SpooledAttachment attachment, string key, ImportSkip skip, CancellationToken cancellationToken)
    {
        if (attachment.Size > maxNewBytes)
        {
            skip(InlineAttachmentReasons.OverUploadCap("file", maxNewBytes));
            return null;
        }

        var path = attachment.Path!;
        var fileName = InlineAttachmentReasons.FileName(validation, attachment.FileName, "attachment");
        var contentType = attachment.ContentType ?? "application/octet-stream";
        try
        {
            validation.ValidateFile(new FileUpload(fileName, contentType, attachment.Size, () => File.OpenRead(path)), maxNewBytes);
        }
        catch (DomainValidationException)
        {
            skip("Attached file is of a type uploads do not accept, or its content does not match its type.");
            return null;
        }

        if (writer is null)
        {
            throw new FileStorageUnavailableException("File storage is not set up on this server.");
        }

        await using var stream = File.OpenRead(path);
        var prepared = await writer.PrepareNewFileAsync(
            StoredFileArea.Journal, stream, fileName, contentType, Math.Max(1, attachment.Size), cancellationToken);
        return new Reservation { Kind = CalendarLinkKind.File, Key = key, File = prepared };
    }

    private async Task<Reservation?> PreparePhotoAsync(
        SpooledAttachment attachment, string key, ImportSkip skip, CancellationToken cancellationToken)
    {
        if (photos is null)
        {
            throw new FileStorageUnavailableException("Photo storage is not set up on this server.");
        }

        var path = attachment.Path!;
        var upload = new FileUpload(
            InlineAttachmentReasons.FileName(validation, attachment.FileName, "photo"),
            attachment.ContentType ?? "application/octet-stream", attachment.Size, () => File.OpenRead(path));
        try
        {
            var prepared = await photos.PrepareUploadAsync(
                upload, Clamp(attachment.Title, PhotoLimits.MaxTitleLength), Clamp(attachment.Caption, PhotoLimits.MaxCaptionLength),
                cancellationToken);
            return new Reservation { Kind = CalendarLinkKind.Photo, Key = key, Photo = prepared };
        }
        catch (PhotoTooLargeException)
        {
            skip(InlineAttachmentReasons.OverUploadCap("photo", maxNewBytes));
        }
        catch (PhotoUnsupportedTypeException)
        {
            skip("Attached photo is not a JPEG, PNG, GIF or WebP image, or its content does not match its type.");
        }
        catch (DomainValidationException)
        {
            skip("Attached photo was refused by the upload checks.");
        }

        return null;
    }

    private static string? Clamp(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value.Trim() : value[..max].Trim();

    // Id + hash + size first (the file it was exported from), then hash + size over what the caller can see.
    private async Task<Guid?> FindStoredAsync(
        CalendarLinkKind kind, Guid? originalId, string hash, long size, CancellationToken cancellationToken)
    {
        if (kind == CalendarLinkKind.File)
        {
            var visible = context.FileMetadata.AsNoTracking().Where(FileReferenceCounter.VisibleInFilesStore(context));
            if (originalId is { } id
                && await visible.Where(f => f.Id == id && f.SizeBytes == size && f.Sha256Hash == hash)
                    .Select(f => (Guid?)f.Id).FirstOrDefaultAsync(cancellationToken) is { } original)
            {
                return original;
            }

            return await visible.Where(f => f.SizeBytes == size && f.Sha256Hash == hash)
                .OrderBy(f => f.UploadedAtUtc).ThenBy(f => f.Id)
                .Select(f => (Guid?)f.Id).FirstOrDefaultAsync(cancellationToken);
        }

        var described = PhotoContent();
        if (originalId is { } photoId
            && await described.Where(p => p.Id == photoId && p.Size == size && p.Hash == hash)
                .Select(p => (Guid?)p.Id).FirstOrDefaultAsync(cancellationToken) is { } originalPhoto)
        {
            return originalPhoto;
        }

        return await described.Where(p => p.Size == size && p.Hash == hash)
            .OrderBy(p => p.Id).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(cancellationToken);
    }

    private void CountReuse(CalendarLinkKind kind)
    {
        if (kind == CalendarLinkKind.Photo)
        {
            batchReusedPhotos++;
        }
        else
        {
            batchReusedFiles++;
        }
    }

    private static CalendarAttachSlot Copy(CalendarAttachSlot known) => new()
    {
        Kind = known.Kind, State = known.State, Id = known.Id, Reservation = known.Reservation,
    };

    // ------------------------------------------------------------------ References

    /// <summary>
    /// A reference <c>ATTACH</c> (§8.3, SF7): skipped with its own reason unless the id is a GUID, the hash matches
    /// <c>^[0-9a-f]{64}$</c> after case-folding and <c>SIZE</c> is a non-negative integer — all three, as the spec states.
    /// </summary>
    public static CalendarAttachSlot Reference(CalendarLinkKind kind, string value, string? hash, string? size, ImportSkip skip)
    {
        var folded = hash?.Trim().ToLowerInvariant();
        long? parsedSize = null;
        var valid = Guid.TryParse(value, out var id)
            && folded is not null && HashPattern().IsMatch(folded)
            && size is not null && long.TryParse(size.Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var s) && (parsedSize = s) >= 0;
        if (!valid)
        {
            skip(InvalidReferenceReason);
            return new CalendarAttachSlot { Kind = kind, State = CalendarAttachSlotState.Skipped };
        }

        return new CalendarAttachSlot
        {
            Kind = kind, State = CalendarAttachSlotState.Reference, ReferenceId = id, ReferenceHash = folded, ReferenceSize = parsedSize,
        };
    }

    /// <summary>
    /// Resolves a batch's references (§8.3 resolution 2, SF2): the cross-batch cache and this batch's content, then one
    /// <c>IN (…)</c> query per kind by id and one by hash, never one per line. Over visible content only, and one skip
    /// reason for unknown and invisible alike (SF1, AC 13).
    /// </summary>
    public async Task ResolveReferencesAsync(IReadOnlyList<CalendarAttachSlot> slots, ImportSkip skip, CancellationToken cancellationToken)
    {
        foreach (var kind in new[] { CalendarLinkKind.File, CalendarLinkKind.Photo })
        {
            var pending = slots.Where(s => s.Kind == kind && s.State == CalendarAttachSlotState.Reference).ToList();
            if (pending.Count > 0)
            {
                // A reference links stored content: the kind's read claim, at the point of use (S1).
                Demand(kind, store: false);
            }

            foreach (var slot in pending.Where(s => s.ReferenceHash is not null).ToList())
            {
                var key = KeyOf(kind, slot.ReferenceHash!, slot.ReferenceSize!.Value);
                if (batchContent.TryGetValue(key, out var known) && known.State is CalendarAttachSlotState.Resolved or CalendarAttachSlotState.Reserved)
                {
                    slot.State = known.State;
                    slot.Id = known.Id;
                    slot.Reservation = known.Reservation;
                    pending.Remove(slot);
                }
                else if (Cached(key) is { } cached)
                {
                    slot.State = CalendarAttachSlotState.Resolved;
                    slot.Id = cached;
                    pending.Remove(slot);
                }
            }

            if (pending.Count == 0)
            {
                continue;
            }

            var ids = pending.Select(s => s.ReferenceId!.Value).Distinct().ToList();
            var hashes = pending.Where(s => s.ReferenceHash is not null).Select(s => s.ReferenceHash!).Distinct().ToList();
            var rows = new List<(Guid Id, string? Hash, long? Size, DateTime Order)>();
            foreach (var chunk in ids.Chunk(ExportChunking.ChunkSize))
            {
                rows.AddRange(await Candidates(kind, c => chunk.Contains(c.Id), cancellationToken));
            }

            var byHash = new List<(Guid Id, string? Hash, long? Size, DateTime Order)>();
            foreach (var chunk in hashes.Chunk(ExportChunking.ChunkSize))
            {
                byHash.AddRange(await Candidates(kind, c => c.Hash != null && chunk.Contains(c.Hash), cancellationToken));
            }

            foreach (var slot in pending)
            {
                var byId = rows.FirstOrDefault(r => r.Id == slot.ReferenceId
                    && (slot.ReferenceHash is null || (r.Hash == slot.ReferenceHash && r.Size == slot.ReferenceSize)));
                Guid? match = byId.Id != Guid.Empty ? byId.Id : null;
                if (match is null && slot.ReferenceHash is not null)
                {
                    var content = byHash.Where(r => r.Hash == slot.ReferenceHash && r.Size == slot.ReferenceSize)
                        .OrderBy(r => r.Order).ThenBy(r => r.Id).FirstOrDefault();
                    match = content.Id != Guid.Empty ? content.Id : null;
                }

                if (match is { } id)
                {
                    slot.State = CalendarAttachSlotState.Resolved;
                    slot.Id = id;
                }
                else
                {
                    slot.State = CalendarAttachSlotState.Skipped;
                    skip(UnresolvedReason);
                }
            }
        }
    }

    private sealed class Candidate
    {
        public Guid Id { get; init; }

        public string? Hash { get; init; }

        public long? Size { get; init; }

        public DateTime Order { get; init; }
    }

    // A photo's hash and size, as a member-init shape: EF composes a filter over a positional record's members on the
    // in-memory provider only, so PhotoOriginalSource.Describe is not reused here.
    private IQueryable<Candidate> PhotoContent() =>
        context.Photos.AsNoTracking().Select(p => new Candidate
        {
            Id = p.PhotoId,
            Hash = p.Sha256Hash,
            Size = p.SizeBytes,
            Order = DateTime.MinValue,
        });

    private async Task<List<(Guid Id, string? Hash, long? Size, DateTime Order)>> Candidates(
        CalendarLinkKind kind, System.Linq.Expressions.Expression<Func<Candidate, bool>> filter, CancellationToken cancellationToken)
    {
        IQueryable<Candidate> source = kind == CalendarLinkKind.File
            ? context.FileMetadata.AsNoTracking().Where(FileReferenceCounter.VisibleInFilesStore(context))
                .Select(f => new Candidate { Id = f.Id, Hash = f.Sha256Hash, Size = f.SizeBytes, Order = f.UploadedAtUtc })
            : PhotoContent();
        var rows = await source.Where(filter).ToListAsync(cancellationToken);
        return rows.Select(r => (r.Id, r.Hash, r.Size, r.Order)).ToList();
    }

    // ------------------------------------------------------------------ Phase B

    /// <summary>
    /// The id a slot links in this attempt. A reservation is re-checked under the lock against content stored since
    /// phase A — a match is linked and the reservation abandoned after commit (implementation note 2) — and otherwise
    /// attached as a NEW row (note 1). Each reservation is resolved once per attempt.
    /// </summary>
    public async Task<Guid?> MaterializeAsync(CalendarAttachSlot slot, Attempt attempt, CancellationToken cancellationToken)
    {
        if (slot.State == CalendarAttachSlotState.Resolved)
        {
            return slot.Id;
        }

        if (slot.State != CalendarAttachSlotState.Reserved || slot.Reservation is not { } reservation)
        {
            return null;
        }

        if (attempt.Attached.TryGetValue(reservation, out var attached))
        {
            return attached;
        }

        if (attempt.Matched.TryGetValue(reservation, out var matched))
        {
            return matched;
        }

        if (await FindStoredAsync(reservation.Kind, null, reservation.Sha256Hash, reservation.SizeBytes, cancellationToken) is { } stored)
        {
            attempt.Matched[reservation] = stored;
            return stored;
        }

        Guid id;
        if (reservation.File is { } file)
        {
            id = writer!.AttachPrepared(context, file, userId).Id;
        }
        else
        {
            id = (await photos!.AttachPreparedAsync(reservation.Photo!, userId, cancellationToken)).PhotoId;
        }

        attempt.Attached[reservation] = id;
        return id;
    }

    /// <summary>
    /// After the batch committed: the attached content enters the cache, and every reservation the committed attempt did
    /// not attach is abandoned — best effort; the abandoned-write sweep removes whatever this misses.
    /// </summary>
    public async Task CommitBatchAsync(Attempt attempt, CancellationToken cancellationToken)
    {
        foreach (var (reservation, id) in attempt.Attached)
        {
            Remember(reservation.Key, id);
            if (reservation.Kind == CalendarLinkKind.Photo)
            {
                StoredPhotos++;
            }
            else
            {
                StoredFiles++;
            }
        }

        foreach (var (reservation, id) in attempt.Matched)
        {
            Remember(reservation.Key, id);
            CountReuse(reservation.Kind);
        }

        foreach (var (key, slot) in batchContent)
        {
            if (slot.State == CalendarAttachSlotState.Resolved && slot.Id is { } id)
            {
                Remember(key, id);
            }
        }

        ReusedFiles += batchReusedFiles;
        ReusedPhotos += batchReusedPhotos;
        foreach (var reservation in batchReservations.Where(r => !attempt.Attached.ContainsKey(r)))
        {
            await AbandonAsync(reservation, cancellationToken);
        }

        ResetBatch();
    }

    /// <summary>Forgets a batch that will not commit; its reservations are left to the sweep.</summary>
    public void ResetBatch()
    {
        batchContent.Clear();
        batchReservations.Clear();
        BatchReservedBytes = 0;
        batchReusedFiles = 0;
        batchReusedPhotos = 0;
    }

    private async Task AbandonAsync(Reservation reservation, CancellationToken cancellationToken)
    {
        try
        {
            if (reservation.File is { } file)
            {
                await writer!.AbandonPreparedAsync(file, cancellationToken);
            }
            else if (reservation.Photo is { } photo)
            {
                await photos!.AbandonPreparedAsync(photo, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("An unused calendar import reservation was left for the abandoned-write sweep ({Failure}).", ex.GetType().Name);
        }
    }

    private Guid? Cached(string key)
    {
        if (!cacheIndex.TryGetValue(key, out var node))
        {
            return null;
        }

        cacheOrder.Remove(node);
        cacheOrder.AddFirst(node);
        return node.Value.Id;
    }

    // Least-recently-used out first; a miss falls through to the database, so overflow never fails an import (v3 §B.4).
    private void Remember(string key, Guid id)
    {
        if (cacheIndex.TryGetValue(key, out var existing))
        {
            cacheOrder.Remove(existing);
        }
        else if (cacheIndex.Count >= CacheCapacity && cacheOrder.Last is { } oldest)
        {
            cacheOrder.RemoveLast();
            cacheIndex.Remove(oldest.Value.Key);
        }

        cacheIndex[key] = cacheOrder.AddFirst((key, id));
    }
}
