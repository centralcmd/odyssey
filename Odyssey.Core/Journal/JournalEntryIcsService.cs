using Odyssey.Core;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Odyssey.Core.FileStorage.Files;
using Odyssey.Core.Journal.Originals;
using Odyssey.Core.Finance;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Odyssey.Core.Journal;
using Odyssey.Core.Journal.Interop;
using Odyssey.Core.Locations;
using Ical.Net.Serialization;
using IcalCalendar = Ical.Net.Calendar;
using IcalJournal = Ical.Net.CalendarComponents.Journal;
using CalDateTime = Ical.Net.DataTypes.CalDateTime;
using CalendarProperty = Ical.Net.CalendarProperty;
using Attachment = Ical.Net.DataTypes.Attachment;

namespace Odyssey.Core.Journal;

/// <summary>
/// VJOURNAL/.ics (RFC 5545 §3.6.3) import/export for the shared journal-entries board (issue #339).
/// Structurally parallel to <c>TaskIcsService</c>/<c>CalendarIcsService</c>: export emits one
/// <c>VJOURNAL</c> per matching entry; import parses <c>parsed.Journals</c>, matches each by <c>UID</c>
/// against <see cref="JournalEntry.ExternalUid"/> (update in place) or creates a new row, and aggregates
/// per-component problems into a skip summary rather than failing the whole file.
/// <para>
/// This service writes <b>directly</b> against <see cref="OdysseyContext"/> — it does not call
/// <c>JournalEntryService.Create/Update</c>, whose link validation throws on any unresolved reference,
/// the opposite of this feature's skip-and-continue contract. Every field/link is resolved and validated
/// before the single batched <c>SaveChanges</c>, so nothing partially-invalid ever reaches EF.
/// </para>
/// </summary>
public class JournalEntryIcsService
{
    private const int MaxTitleLength = ImportLimits.MaxTitleLength;
    private const int MaxContentLength = ImportLimits.MaxContentLength;
    private const int MaxLocationLength = 300;
    private const int MaxExternalUidLength = 255;
    private const string ProductId = "-//Odyssey//Journal//EN";
    private const string FileUriScheme = ImportLimits.FileUriScheme;
    private const string PhotoUriScheme = "odyssey-photo";
    private const string LocationProperty = "X-ODYSSEY-LOCATION";
    private const string ContactProperty = "X-ODYSSEY-CONTACT";
    private const string UntitledEntry = "(untitled)";

    /// <summary>
    /// Reported when an entry carries more links of one kind than the cap allows (issue #434 §9-A).
    ///
    /// <para>
    /// This path used to cap and move on <em>silently</em>. That is a deliberate, declared contract
    /// change, not a bug fix: a silent cap is indistinguishable from data loss to the user, and the
    /// task import already reported its own capped links. The number is interpolated because the cap is
    /// admin-editable — a literal in the text would go stale the moment it changed.
    /// </para>
    /// </summary>
    internal static string LinksCappedReason(int maxLinksPerKind) => ImportLinks.LinksCappedReason("entry", maxLinksPerKind);

    private readonly OdysseyContext context;
    private readonly IContactLookup contacts;
    private readonly IFileLookup files;
    private readonly IPhotoLookup photos;
    private readonly IImportExportLimitsLookup limits;
    private readonly IJournalLimitsLookup journalLimits;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<JournalEntryIcsService> logger;
    private readonly StoredFileContentSource? fileContent;
    private readonly PhotoOriginalSource? photoContent;
    private readonly StoredFileWriter? fileWriter;
    private readonly FileValidationService fileValidation;
    private readonly IUploadLimitsLookup? uploadLimits;
    private readonly PhotoService? photoService;
    private readonly long transportFileCeiling;

    // journalLimits is a SECOND lookup, newly injected in issue #434 to fix defect A: this service
    // enforced a hardcoded 50 links per kind while an administrator's JournalEntryMaxLinksPerKind
    // setting was honoured on the create/update path and silently ignored here — the precise "I lowered
    // the limit and it did not take effect" failure the settings feature refuses to ship.
    //
    // A second lookup rather than mirroring the cap onto ImportExportLimits, which was tried and
    // abandoned: SystemSettingDescriptor.CacheKeyToEvict is a single string, so a mirrored value would
    // sit behind two cache entries with only one evicted, and the two records degrade by different
    // rules. One owner, one cache key, one eviction, one degraded rule. The cost is one extra CACHED
    // read per import — a warm in-process dictionary hit on a request that already performs several
    // database round-trips and holds one of only two global import permits. No extra database query.
    public JournalEntryIcsService(
        OdysseyContext context,
        IContactLookup contacts,
        IFileLookup files,
        IPhotoLookup photos,
        IImportExportLimitsLookup limits,
        IJournalLimitsLookup journalLimits,
        ILogger<JournalEntryIcsService> logger,
        TimeProvider? timeProvider = null,
        StoredFileContentSource? fileContent = null,
        PhotoOriginalSource? photoContent = null,
        StoredFileWriter? fileWriter = null,
        FileValidationService? fileValidation = null,
        IUploadLimitsLookup? uploadLimits = null,
        PhotoService? photoService = null,
        IOptions<FileStorageOptions>? fileStorageOptions = null)
    {
        // The file and photo collaborators are optional so a unit test without a store still builds one; without them
        // attached files and photos export as bare references and inline ones cannot be imported.
        this.fileContent = fileContent;
        this.photoContent = photoContent;
        this.fileWriter = fileWriter;
        this.fileValidation = fileValidation ?? new FileValidationService();
        this.uploadLimits = uploadLimits;
        this.photoService = photoService;
        transportFileCeiling = (fileStorageOptions?.Value ?? new FileStorageOptions()).MaxFileSizeBytes;
        this.context = context;
        this.contacts = contacts;
        this.files = files;
        this.photos = photos;
        this.limits = limits;
        this.journalLimits = journalLimits;
        this.logger = logger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    // ---------------------------------------------------------------- Export

    private sealed record ExportFile(Guid Id, string Head, long EncodedBytes, long SizeBytes);

    private sealed record ExportPlan(
        IQueryable<JournalEntry> BaseQuery, List<Guid> OrderedIds,
        IReadOnlyDictionary<Guid, ExportFile> Files, IReadOnlyDictionary<Guid, ExportFile> Photos,
        int AttachmentCount, int PhotoCount, long AttachmentBytes, long MaxBytes);

    private IQueryable<FileMetadata> VisibleFiles => context.FileMetadata.Where(FileReferenceCounter.VisibleInFilesStore(context));

    /// <summary>
    /// What <see cref="ExportStreamingAsync"/> would produce for <paramref name="query"/> (or, with
    /// <paramref name="onlyId"/>, for that one entry), refusing exactly where it would: over the entry cap, over the
    /// byte cap (judged from the attached files' and photos' stored sizes before any byte is encoded), or when an
    /// exported entry carries an attached file without <paramref name="canReadFiles"/> or a photo without
    /// <paramref name="canReadPhotos"/>. Null when <paramref name="onlyId"/> names no entry.
    /// </summary>
    public async Task<JournalEntryIcsExportSummary?> SummarizeExportAsync(
        JournalEntriesQueryParams query, bool canReadFiles, bool canReadPhotos, Guid? onlyId = null,
        CancellationToken cancellationToken = default)
    {
        var plan = await PrepareExportAsync(query, onlyId, canReadFiles, canReadPhotos, cancellationToken);
        return onlyId is not null && plan.OrderedIds.Count == 0
            ? null
            : new JournalEntryIcsExportSummary
            {
                EntryCount = plan.OrderedIds.Count,
                AttachmentCount = plan.AttachmentCount,
                PhotoCount = plan.PhotoCount,
                AttachmentBytes = plan.AttachmentBytes,
            };
    }

    private async Task<ExportPlan> PrepareExportAsync(
        JournalEntriesQueryParams query, Guid? onlyId, bool canReadFiles, bool canReadPhotos, CancellationToken cancellationToken)
    {
        IQueryable<JournalEntry> baseQuery;
        if (onlyId is { } id)
        {
            // The single-entry export ignores every filter and status: the row menu names exactly one entry.
            baseQuery = context.JournalEntries.AsNoTracking().Where(e => e.JournalEntryId == id);
        }
        else
        {
            baseQuery = JournalEntryService.ApplyFilters(context.JournalEntries.AsNoTracking(), query);
            baseQuery = query.Status switch
            {
                ArchivalStatus.Active => baseQuery.Where(e => e.Archived == null),
                ArchivalStatus.Archived => baseQuery.Where(e => e.Archived != null),
                _ => baseQuery, // omitted → all statuses, including archived (deliberate divergence from the list default)
            };
        }

        var effectiveLimits = await limits.GetAsync(cancellationToken);
        var maxExportRows = effectiveLimits.JournalIcsMaxExportRows;
        var maxExportBytes = effectiveLimits.JournalIcsMaxExportBytes;

        // No explicit transaction: OdysseyContext enables EnableRetryOnFailure() in production, which
        // forbids a bare Database.BeginTransactionAsync unless the ENTIRE unit of work runs inside one
        // CreateExecutionStrategy().ExecuteAsync call (verified against real MariaDB) — that doesn't
        // compose with a chunked read that yields output as it goes. Instead, the ordered
        // JournalEntryId set is captured in one cheap up-front read, and each chunk is fetched
        // independently by a fixed id batch — see ExportChunking.ReorderToSnapshot for the consistency
        // trade-off this makes relative to the RepeatableRead snapshot this replaced (PR #403 review
        // fix). Unlike the other three surfaces, the row-count cap here throws on the exact count
        // rather than a bounded max+1 probe (ExportLimitExceededException, not DomainValidationException
        // — pre-existing shape, unchanged), so the full ordered id list is always fetched.
        var orderedIds = await baseQuery
            .OrderBy(e => e.EntryDate).ThenBy(e => e.JournalEntryId)
            .Select(e => e.JournalEntryId)
            .ToListAsync(cancellationToken);
        if (onlyId is null && maxExportRows is { } max && orderedIds.Count > max)
        {
            throw new ExportLimitExceededException(max);
        }

        // Attached files and linked photos travel inline. Only a file the Files store shows is exported with its
        // content (a photo's own original is not readable under files.read), and only a photo whose original
        // resolves; one that does not keeps its bare reference, which a re-import into the same instance resolves.
        var fileLinks = await baseQuery.SelectMany(e => e.Attachments).Select(a => a.FileId).ToListAsync(cancellationToken);
        var files = new Dictionary<Guid, ExportFile>();
        if (fileLinks.Count > 0 && fileContent is not null)
        {
            var distinct = fileLinks.Distinct().ToList();
            var rows = await VisibleFiles.AsNoTracking()
                .Where(f => distinct.Contains(f.Id))
                .Select(f => new { f.Id, f.FileName, f.ContentType, f.Sha256Hash, f.SizeBytes })
                .ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                files[row.Id] = Planned(new IcsBinaryAttachmentHeader(row.Id, row.FileName, row.ContentType, row.Sha256Hash, row.SizeBytes));
            }
        }

        var photoLinks = await baseQuery.SelectMany(e => e.Photos).Select(p => p.PhotoId).ToListAsync(cancellationToken);
        var photoFiles = new Dictionary<Guid, ExportFile>();
        if (photoLinks.Count > 0 && photoContent is not null)
        {
            var distinct = photoLinks.Distinct().ToList();
            var described = await PhotoOriginalSource
                .Describe(context, context.Photos.AsNoTracking().Where(p => distinct.Contains(p.PhotoId)))
                .ToListAsync(cancellationToken);
            var labels = await context.Photos.AsNoTracking()
                .Where(p => distinct.Contains(p.PhotoId))
                .Select(p => new { p.PhotoId, p.Title, p.Caption })
                .ToDictionaryAsync(p => p.PhotoId, cancellationToken);
            foreach (var row in described.Where(d => d.SizeBytes is not null && d.Sha256Hash is not null && d.ContentType is not null))
            {
                var label = labels.GetValueOrDefault(row.PhotoId);
                photoFiles[row.PhotoId] = Planned(new IcsBinaryAttachmentHeader(
                    row.PhotoId, row.FileName, row.ContentType, row.Sha256Hash, row.SizeBytes!.Value,
                    IsPhoto: true, Title: label?.Title, Caption: label?.Caption));
            }
        }

        var inlineFiles = fileLinks.Where(files.ContainsKey).ToList();
        var inlinePhotos = photoLinks.Where(photoFiles.ContainsKey).ToList();
        if (inlineFiles.Count > 0 && !canReadFiles)
        {
            throw new DomainForbiddenException(
                $"These entries have {inlineFiles.Count} attached {(inlineFiles.Count == 1 ? "file" : "files")}, and exporting "
                + "attached files needs permission to read files.");
        }

        if (inlinePhotos.Count > 0 && !canReadPhotos)
        {
            throw new DomainForbiddenException(
                $"These entries have {inlinePhotos.Count} {(inlinePhotos.Count == 1 ? "photo" : "photos")}, and exporting "
                + "photos needs permission to read photos.");
        }

        var estimatedBytes = inlineFiles.Sum(fid => files[fid].EncodedBytes) + inlinePhotos.Sum(pid => photoFiles[pid].EncodedBytes);
        if (estimatedBytes > maxExportBytes)
        {
            throw new DomainValidationException(
                $"The export would be at least {estimatedBytes / (1024 * 1024)} MB with its attached files and photos, over the "
                + $"{maxExportBytes / (1024 * 1024)} MB export limit — narrow the filters.");
        }

        return new ExportPlan(
            baseQuery, orderedIds, files, photoFiles, inlineFiles.Count, inlinePhotos.Count,
            inlineFiles.Sum(fid => files[fid].SizeBytes) + inlinePhotos.Sum(pid => photoFiles[pid].SizeBytes),
            maxExportBytes);
    }

    private static ExportFile Planned(IcsBinaryAttachmentHeader header)
    {
        var head = IcsBinaryAttachmentWriter.BuildHead(header);
        return new ExportFile(header.Id!.Value, head, IcsBinaryAttachmentWriter.EncodedLength(head, header.Size), header.Size);
    }

    /// <summary>
    /// Streams every entry matching <paramref name="query"/>'s filters — or, with <paramref name="onlyId"/>, that one
    /// entry — directly to <paramref name="output"/>, in row-keyset chunks (issue #343 §5 Goal 8). Unlike the list
    /// endpoint, archived entries are <b>included</b> when no status is supplied (§5 — backup/migration use case).
    /// Attached files and linked photos travel inline as base64 <c>ATTACH</c> lines, streamed from their stores and
    /// never held whole in memory. Throws <see cref="ExportLimitExceededException"/> (→ 400) if the matched set
    /// exceeds the configured <c>JournalIcsMaxExportRows</c> cap, and refuses (400/403) exactly as
    /// <see cref="SummarizeExportAsync"/> does — all before <paramref name="onReady"/> is invoked.
    /// <paramref name="onReady"/> is called exactly once, with the file name and row count, before the first byte.
    /// <para>
    /// The byte cap is judged exactly for the attached content up front; an entry whose text would still cross it
    /// throws <see cref="IcsExportSizeExceededException"/> once the response has started, and the caller aborts the
    /// connection rather than delivering a file that stops early.
    /// </para>
    /// </summary>
    public async Task ExportStreamingAsync(
        JournalEntriesQueryParams query, bool includeContacts, Stream output, Action<string, int> onReady,
        CancellationToken cancellationToken = default, bool includePlaceNames = false,
        bool canReadFiles = false, bool canReadPhotos = false, Guid? onlyId = null)
    {
        var plan = await PrepareExportAsync(query, onlyId, canReadFiles, canReadPhotos, cancellationToken);
        var count = plan.OrderedIds.Count;

        var exportDate = timeProvider.GetUtcNow().UtcDateTime;
        var name = onlyId is not null
            ? $"odyssey-journal-entry-{exportDate:yyyyMMdd-HHmmss}Z.ics"
            : $"odyssey-journal-entries{(HasAnyFilter(query) ? "-filtered" : string.Empty)}-{exportDate:yyyyMMdd-HHmmss}Z.ics";
        onReady(name, count);

        var (head, tail) = IcsChunkSerializer.BuildEnvelope(ProductId);
        await IcsChunkSerializer.WriteAsync(output, head, cancellationToken);
        var writtenBytes = (long)Encoding.UTF8.GetByteCount(head) + Encoding.UTF8.GetByteCount(tail);

        var written = 0;
        foreach (var idBatch in plan.OrderedIds.Chunk(ExportChunking.ChunkSize))
        {
            var rows = await plan.BaseQuery
                .Where(e => idBatch.Contains(e.JournalEntryId))
                .Include(e => e.EntryTags)
                .Include(e => e.Contacts)
                .Include(e => e.Photos)
                .Include(e => e.Attachments)
                .ToListAsync(cancellationToken);
            var entries = ExportChunking.ReorderToSnapshot(idBatch, rows, e => e.JournalEntryId);
            if (entries.Count == 0)
            {
                continue; // every id in this batch was deleted between the snapshot and this fetch
            }

            var lookups = await LoadExportLookupsAsync(entries, includeContacts, includePlaceNames, cancellationToken);
            foreach (var entry in entries)
            {
                var inlinePhotos = entry.Photos.OrderBy(p => p.Position)
                    .Select(p => plan.Photos.GetValueOrDefault(p.PhotoId)).OfType<ExportFile>().ToList();
                var inlineFiles = entry.Attachments.OrderBy(a => a.CreatedAt)
                    .Select(a => plan.Files.GetValueOrDefault(a.FileId)).OfType<ExportFile>().ToList();

                var calendar = new IcalCalendar();
                calendar.Journals.Add(BuildVJournal(entry, lookups, plan.Files, plan.Photos));
                var text = IcsChunkSerializer.SerializeComponents(calendar);
                var split = text.LastIndexOf("END:VJOURNAL", StringComparison.Ordinal);
                var (prefix, suffix) = split < 0 ? (text, string.Empty) : (text[..split], text[split..]);

                var entryBytes = Encoding.UTF8.GetByteCount(text) + inlinePhotos.Sum(f => f.EncodedBytes) + inlineFiles.Sum(f => f.EncodedBytes);
                if (writtenBytes + entryBytes > plan.MaxBytes)
                {
                    logger.LogWarning(
                        "Journal entries .ics export stopped at {WrittenBytes} bytes (cap {MaxBytes}); {WrittenRows}/{TotalRows} entries delivered.",
                        writtenBytes, plan.MaxBytes, written, count);
                    throw new IcsExportSizeExceededException(
                        $"The export grew past the {plan.MaxBytes / (1024 * 1024)} MB export limit while it was being written.");
                }

                await IcsChunkSerializer.WriteAsync(output, prefix, cancellationToken);
                foreach (var photo in inlinePhotos)
                {
                    await WriteInlinePhotoAsync(output, photo, cancellationToken);
                }

                foreach (var file in inlineFiles)
                {
                    await WriteInlineFileAsync(output, file, cancellationToken);
                }

                await IcsChunkSerializer.WriteAsync(output, suffix, cancellationToken);
                writtenBytes += entryBytes;
                written++;
            }
        }

        await IcsChunkSerializer.WriteAsync(output, tail, cancellationToken);
    }

    private async Task WriteInlineFileAsync(Stream output, ExportFile file, CancellationToken cancellationToken)
    {
        var content = await fileContent!.OpenAsync(VisibleFiles.Where(f => f.Id == file.Id), cancellationToken);
        if (content is null)
        {
            // Removed since the plan was made: keep the reference rather than drop the link silently.
            await IcsChunkSerializer.WriteAsync(output, $"ATTACH:{FileUriScheme}:{file.Id}\r\n", cancellationToken);
            return;
        }

        await using var stream = content.Content;
        await IcsBinaryAttachmentWriter.WriteAsync(output, file.Head, stream, cancellationToken);
    }

    private async Task WriteInlinePhotoAsync(Stream output, ExportFile photo, CancellationToken cancellationToken)
    {
        var content = await photoContent!.OpenAsync(photo.Id, cancellationToken);
        if (content is null)
        {
            await IcsChunkSerializer.WriteAsync(output, $"ATTACH:{PhotoUriScheme}:{photo.Id}\r\n", cancellationToken);
            return;
        }

        await using var stream = content.Content;
        await IcsBinaryAttachmentWriter.WriteAsync(output, photo.Head, stream, cancellationToken);
    }

    // Every filter JournalEntriesQueryParams carries has to be listed here, because this decides the
    // "-filtered" segment in the exported filename — a set narrowed by a filter this misses would be
    // handed to the user named as the full export. Adding a filter to the query model means adding it
    // here too; ExportFiltered_FileName_* pins each one.
    private static bool HasAnyFilter(JournalEntriesQueryParams query) =>
        !string.IsNullOrWhiteSpace(query.Search)
        || query.TagIds is { Length: > 0 }
        || query.ContactIds is { Length: > 0 }
        || query.From is not null
        || query.To is not null
        || query.HasPhotos is not null
        || query.HasFiles is not null
        || query.Status is not null;

    private sealed record ExportLookups(
        IReadOnlyDictionary<Guid, string> TagNames,
        IReadOnlySet<Guid> ExistingPhotoIds,
        IReadOnlyDictionary<Guid, string>? ContactUids,
        IReadOnlyDictionary<Guid, string> PlaceNames);

    // Batches the tag/photo/(optionally) contact lookups for exactly the entries passed in, per chunk, so each chunk's
    // lookups stay scoped to that chunk rather than the whole matched set (issue #343 §5 Goal 8).
    private async Task<ExportLookups> LoadExportLookupsAsync(
        IReadOnlyList<JournalEntry> entries, bool includeContacts, bool includePlaceNames, CancellationToken cancellationToken)
    {
        // A linked place's name is written only for a locations.read holder (issue #380 §8.4); never GEO.
        var placeNames = await IcsPlaces.NamesAsync(context, entries.Select(e => e.LocationId), includePlaceNames, cancellationToken);

        var tagIds = entries.SelectMany(e => e.EntryTags.Select(t => t.JournalTagId)).Distinct().ToList();
        var tagNames = tagIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await context.JournalTags
                .Where(t => tagIds.Contains(t.JournalTagId))
                .ToDictionaryAsync(t => t.JournalTagId, t => t.Name, cancellationToken);

        var photoIds = entries.SelectMany(e => e.Photos.Select(p => p.PhotoId)).Distinct().ToList();
        // Issue #428: a photo is written by its own id; import still accepts an old export's file ids.
        var existingPhotoIds = await photos.ExistingIdsAsync(photoIds, cancellationToken);

        IReadOnlyDictionary<Guid, string>? contactUids = null;
        if (includeContacts)
        {
            var contactIds = entries
                .SelectMany(e => e.Contacts.Select(c => c.ContactId)).Distinct().ToList();
            contactUids = contactIds.Count == 0
                ? new Dictionary<Guid, string>()
                : await contacts.ResolveExternalUidsAsync(contactIds, cancellationToken);
        }

        return new ExportLookups(tagNames, existingPhotoIds, contactUids, placeNames);
    }

    private static IcalJournal BuildVJournal(
        JournalEntry entry, ExportLookups lookups,
        IReadOnlyDictionary<Guid, ExportFile> inlineFiles, IReadOnlyDictionary<Guid, ExportFile> inlinePhotos)
    {
        var journal = new IcalJournal
        {
            Uid = entry.ExternalUid,
            Summary = entry.Title,
            Description = entry.Content,
            // VJOURNAL status set is DRAFT/FINAL/CANCELLED; Odyssey has only active vs archived (§9).
            Status = entry.Archived is null ? "FINAL" : "CANCELLED",
            // Date component only (VALUE=DATE) — a journal entry carries no time of day.
            Start = new CalDateTime(entry.EntryDate.Year, entry.EntryDate.Month, entry.EntryDate.Day),
        };

        if (IcsPlaces.ExportText(entry.LocationId, entry.Location, lookups.PlaceNames) is { } place)
        {
            journal.Properties.Add(new CalendarProperty(LocationProperty, place));
        }

        foreach (var link in entry.EntryTags)
        {
            if (lookups.TagNames.TryGetValue(link.JournalTagId, out var name))
            {
                journal.Categories.Add(name);
            }
        }

        // X-ODYSSEY-CONTACT is emitted only when the caller holds contacts.read (§9/§10.2).
        if (lookups.ContactUids is { } contactUids)
        {
            foreach (var link in entry.Contacts)
            {
                if (contactUids.TryGetValue(link.ContactId, out var uid))
                {
                    journal.Properties.Add(new CalendarProperty(ContactProperty, uid));
                }
            }
        }

        // A photo or file whose content travels inline is written by the caller; only one that cannot keeps its bare
        // reference (issue #428 §3.3: a photo by its PhotoId). A link whose library Photo no longer resolves is dropped,
        // mirroring the read DTO.
        foreach (var photo in entry.Photos.OrderBy(p => p.Position))
        {
            if (!inlinePhotos.ContainsKey(photo.PhotoId) && lookups.ExistingPhotoIds.Contains(photo.PhotoId))
            {
                journal.Attachments.Add(new Attachment { Uri = new Uri($"{PhotoUriScheme}:{photo.PhotoId}") });
            }
        }

        foreach (var attachment in entry.Attachments.Where(a => !inlineFiles.ContainsKey(a.FileId)).OrderBy(a => a.CreatedAt))
        {
            journal.Attachments.Add(new Attachment { Uri = new Uri($"{FileUriScheme}:{attachment.FileId}") });
        }

        return journal;
    }

    // ---------------------------------------------------------------- Import

    /// <summary>Upper bound on the calendar's own text once inline files are lifted out — what <c>Ical.Net</c> parses in
    /// memory. Attached files and photos are bounded by the byte cap and disk, never by this.</summary>
    internal const long MaxCalendarTextChars = 64L * 1024 * 1024;

    /// <summary>Inline attachments one import may carry: each costs a temp file and an entry until the import ends.</summary>
    internal const int MaxInlineAttachments = 10_000;

    /// <summary>
    /// Imports a VJOURNAL <c>.ics</c>. Inline (base64) files and photos are spooled to disk by a streaming pre-pass,
    /// then each is matched to a stored file or library photo — the one it was exported from, else any with the same
    /// content — and stored new only when neither matches (files in the Journal area, photos in the library, through
    /// the upload checks). A file carrying inline files needs <paramref name="canLinkFiles"/> (<c>files.read</c>) and
    /// <paramref name="canCreateFiles"/>; one carrying inline photos needs <paramref name="canReadPhotos"/> and
    /// <paramref name="canCreatePhotos"/> — refused whole (403) at the first such attachment without them.
    /// </summary>
    public async Task<JournalEntryIcsImportResult> ImportAsync(
        Stream icsFile, long contentLength, string? contentType, string userId,
        bool canLinkFiles, bool canReadContacts, CancellationToken cancellationToken = default,
        LocationClaims? claims = null, bool canReadPhotos = false, bool canCreateFiles = false, bool canCreatePhotos = false)
    {
        var cap = await limits.GetAsync(cancellationToken);
        var maxFileBytes = Math.Min(cap.JournalIcsMaxImportBytes, transportFileCeiling);
        using var spool = await SpoolAsync(
            icsFile, contentLength, contentType, maxFileBytes, canLinkFiles && canCreateFiles, canReadPhotos && canCreatePhotos,
            cancellationToken);
        var journals = ParseJournals(spool, cap.JournalIcsMaxImportEntries);
        var lookups = await LoadLookupsAsync(journals, userId, canLinkFiles, canReadPhotos, canReadContacts, cancellationToken);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        // One snapshot of each record for the whole import, so a concurrent admin write cannot split
        // one file across two values of the same setting.
        var importLimits = await limits.GetAsync(cancellationToken);
        var maxLinksPerKind = (await journalLimits.GetAsync(cancellationToken)).JournalEntryMaxLinksPerKind;
        var skipped = new ImportSkipCollector(importLimits.ImportMaxSamplesPerSkipReason);
        var counts = new LinkSkipCounts();
        // A file or photo stored new by this import is an upload like any other, so the admin's per-file upload cap
        // applies to it; one matched to a stored file or photo is not stored again, so only the import's caps bound it.
        var maxNewFileBytes = Math.Min(maxFileBytes,
            uploadLimits is null ? long.MaxValue : (await uploadLimits.GetAsync(cancellationToken)).MaxUploadBytes);
        var inlineFiles = new InlineFileImporter(context, fileValidation, fileWriter, userId, maxFileBytes, maxNewFileBytes, skipped);
        var inlinePhotos = new InlinePhotoImporter(context, photoService, userId, maxFileBytes, skipped);
        // Places link only with locations.read + locations.create (issue #380 §8.4); null claims import text.
        var places = await IcsPlaces.ForImportAsync(context, claims ?? LocationClaims.None, userId, timeProvider, cancellationToken);
        var createdByUid = new Dictionary<string, JournalEntry>(StringComparer.Ordinal);
        var imported = 0;
        var updated = 0;

        foreach (var journal in journals)
        {
            // A block that fails any field rule is skipped whole and recorded — never partially applied.
            if (Validate(journal, skipped) is not { } fields)
            {
                continue;
            }

            var (target, isNew) = ResolveTarget(fields, lookups.EntriesByUid, createdByUid, userId, now);

            target.Title = fields.Title;
            target.Content = fields.Content;
            target.EntryDate = fields.EntryDate;
            ApplyLocation(target, journal, places);
            ApplyStatus(target, journal, isNew, now);

            if (isNew)
            {
                imported++;
            }
            else
            {
                target.UpdatedByUserId = userId;
                target.UpdatedAt = now;
                updated++;
            }

            ApplyTags(target, journal, lookups.TagIdsByName, counts, maxLinksPerKind, skipped);
            ApplyContacts(
                target, journal, canReadContacts, lookups.ContactIdsByUid, counts, maxLinksPerKind, skipped);
            var (fileRefs, photoRefs) = await ResolveAttachmentsAsync(
                journal, spool, inlineFiles, inlinePhotos, lookups, fields.Title, cancellationToken);
            ApplyAttachments(target, fileRefs, canLinkFiles, now, counts, maxLinksPerKind, skipped);
            ApplyPhotos(target, photoRefs, canReadPhotos, now, counts, maxLinksPerKind, skipped);
        }

        var collisions = await SaveWithCollisionHandlingAsync(skipped, places, cancellationToken);
        imported -= collisions;

        var result = BuildResult(imported, updated, skipped, counts);
        result.PlacesLinked = places.Linked;
        result.PlacesCreated = places.Created;
        result.StoredFileCount = inlineFiles.Stored;
        result.ReusedFileCount = inlineFiles.Reused;
        result.StoredPhotoCount = inlinePhotos.Stored;
        result.ReusedPhotoCount = inlinePhotos.Reused;
        return result;
    }

    /// <summary>
    /// Reads the upload through the streaming pre-pass, rejecting anything that makes the file as a whole unusable —
    /// wrong content type, over the size cap, inline files or photos the caller may not import. These are
    /// whole-request failures; every per-block problem after this point is a skip, not a throw.
    /// </summary>
    private async Task<IcsAttachmentSpool> SpoolAsync(
        Stream icsFile, long contentLength, string? contentType, long maxFileBytes, bool allowFiles, bool allowPhotos,
        CancellationToken cancellationToken)
    {
        if (!IsAcceptedContentType(contentType))
        {
            throw new DomainValidationException("The uploaded file must be a calendar file (text/calendar).");
        }

        var cap = await limits.GetAsync(cancellationToken);
        var maxImportBytes = cap.JournalIcsMaxImportBytes;
        if (contentLength > maxImportBytes)
        {
            throw new DomainValidationException($"The .ics file exceeds the {maxImportBytes / (1024 * 1024)} MB limit.");
        }

        using var reader = ImportFileReader.OpenBoundedTextReader(icsFile, maxImportBytes, ".ics");
        return await IcsAttachmentSpooler.SpoolAsync(
            reader,
            new IcsAttachmentSpooler.Options(
                maxFileBytes, Math.Min(maxImportBytes, MaxCalendarTextChars),
                AllowAttachments: allowFiles, MaxAttachments: MaxInlineAttachments, AllowPhotos: allowPhotos),
            cancellationToken);
    }

    /// <summary>Parses the spooled calendar text, refusing an unparseable file or more VJOURNALs than the batch limit.</summary>
    private static List<IcalJournal> ParseJournals(IcsAttachmentSpool spool, int? maxEntries)
    {
        IcalCalendar? parsed;
        try
        {
            parsed = IcalCalendar.Load(new StringReader(spool.CalendarText));
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not DomainValidationException)
        {
            throw new DomainValidationException("The file could not be parsed as a valid iCalendar (.ics) file.");
        }

        if (parsed is null)
        {
            throw new DomainValidationException("The file could not be parsed as a valid iCalendar (.ics) file.");
        }

        var journals = parsed.Journals;
        var maxVJournals = maxEntries ?? int.MaxValue;
        if (journals.Count > maxVJournals)
        {
            throw new DomainValidationException($"The file contains more than {maxVJournals} journal entries (VJOURNAL).");
        }

        return [.. journals];
    }

    /// <summary>
    /// Everything the per-block loop needs to resolve a reference, fetched in one batch up front rather
    /// than per block: the entries this file could update, the tag vocabulary, and the contact/file/photo
    /// references the caller is allowed to link.
    /// </summary>
    private sealed record ImportLookups(
        Dictionary<string, JournalEntry> EntriesByUid,
        Dictionary<string, Guid> TagIdsByName,
        IReadOnlyDictionary<string, Guid> ContactIdsByUid,
        IReadOnlySet<Guid> ExistingAttachmentIds,
        IReadOnlyDictionary<Guid, Guid> PhotoIdsByReference);

    private async Task<ImportLookups> LoadLookupsAsync(
        List<IcalJournal> journals, string userId, bool canLinkFiles, bool canReadPhotos, bool canReadContacts,
        CancellationToken cancellationToken)
    {
        // Match targets by UID → ExternalUid. Only load entries whose (well-formed) UID appears in this file.
        var incomingUids = journals
            .Select(j => j.Uid)
            .Where(u => u is not null && u.Length <= MaxExternalUidLength && IsValidExternalUid(u))
            .Select(u => u!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var existing = incomingUids.Count == 0
            ? new List<JournalEntry>()
            : await context.JournalEntries
                .Include(e => e.EntryTags)
                .Include(e => e.Contacts)
                .Include(e => e.Photos)
                .Include(e => e.Attachments)
                .Where(e => incomingUids.Contains(e.ExternalUid))
                .ToListAsync(cancellationToken);

        // Every tag, archived ones included, indexed by case-insensitive exact name: the export writes an archived
        // tag's name too, so matching only active tags made a re-import strip it. On a duplicate name the first wins,
        // and an active tag is offered before an archived one.
        var tagRows = await context.JournalTags
            .Select(t => new { t.JournalTagId, t.Name, t.Archived })
            .ToListAsync(cancellationToken);
        var tagsByName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tagRows.OrderBy(t => t.Archived is null ? 0 : 1).ThenBy(t => t.JournalTagId))
        {
            tagsByName.TryAdd(tag.Name, tag.JournalTagId);
        }

        var references = CollectReferences(journals);
        var contactIdByUid = canReadContacts && references.ContactUids.Count > 0
            ? await contacts.ResolveIdsByExternalUidAsync(references.ContactUids, cancellationToken)
            : new Dictionary<string, Guid>(StringComparer.Ordinal);
        var existingAttachmentIds = canLinkFiles && references.AttachmentFileIds.Count > 0
            ? await files.ExistingIdsAsync(references.AttachmentFileIds, cancellationToken)
            : (IReadOnlySet<Guid>)new HashSet<Guid>();
        // Issue #428 §3.3: a reference resolves to an existing photo (by photo id, legacy file id or backfilled file
        // id) and never creates one. Linking photos needs photos.read.
        var photoIdByReference = canReadPhotos && references.PhotoFileIds.Count > 0
            ? await photos.ResolveReferencesAsync(references.PhotoFileIds, cancellationToken)
            : new Dictionary<Guid, Guid>();

        return new ImportLookups(
            existing.ToDictionary(e => e.ExternalUid, StringComparer.Ordinal),
            tagsByName,
            contactIdByUid,
            existingAttachmentIds,
            photoIdByReference);
    }

    /// <summary>The validated scalar fields of one VJOURNAL block, ready to write onto a row.</summary>
    private readonly record struct EntryFields(string? Uid, string Title, string Content, DateTime EntryDate);

    /// <summary>
    /// Field-level validation for one block. Returns null and records the reason when the block cannot be
    /// imported; every rule here is a per-block skip so one bad entry never fails the file.
    /// </summary>
    private static EntryFields? Validate(IcalJournal journal, ImportSkipCollector skipped)
    {
        var title = journal.Summary?.Trim();
        var sample = string.IsNullOrWhiteSpace(title) ? UntitledEntry : title;

        var rawUid = journal.Uid;

        // A UID longer than the column would, on a strict-mode MariaDB, surface as a non-duplicate
        // DbUpdateException the collision handler doesn't recognize — a whole-batch 500 that breaks the
        // skip-and-continue contract. Bound it here so an over-length UID is a clean per-block skip.
        // Checked before the pattern so the regex only ever runs over a bounded value.
        if (rawUid is { Length: > MaxExternalUidLength })
        {
            skipped.Add($"UID exceeds the maximum length of {MaxExternalUidLength} characters.", sample);
            return null;
        }

        if (rawUid is not null && !IsValidExternalUid(rawUid))
        {
            skipped.Add("Invalid UID: control characters or leading/trailing whitespace not allowed.", sample);
            return null;
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            skipped.Add("Entry is missing a title (SUMMARY).", sample);
            return null;
        }

        if (title.Length > MaxTitleLength)
        {
            skipped.Add($"Title exceeds the maximum length of {MaxTitleLength} characters.", sample);
            return null;
        }

        var content = journal.Description;
        if (string.IsNullOrWhiteSpace(content))
        {
            skipped.Add("Entry is missing content (DESCRIPTION).", sample);
            return null;
        }

        if (content.Length > MaxContentLength)
        {
            skipped.Add($"Description exceeds the maximum length of {MaxContentLength} characters.", sample);
            return null;
        }

        var entryDate = ToEntryDate(journal.Start);
        if (entryDate is null)
        {
            skipped.Add("Entry date (DTSTART) is required.", sample);
            return null;
        }

        if (entryDate.Value.Year is < 1900 or > 2100)
        {
            skipped.Add("Entry date must fall within the years 1900–2100.", sample);
            return null;
        }

        return new EntryFields(
            string.IsNullOrEmpty(rawUid) ? null : rawUid, title, content, entryDate.Value);
    }

    /// <summary>
    /// Finds the row this block writes to: an existing entry with the same UID, a row created earlier in
    /// this same file (last-write-wins on a duplicate UID), or a new entry.
    /// </summary>
    private (JournalEntry Target, bool IsNew) ResolveTarget(
        EntryFields fields, Dictionary<string, JournalEntry> entriesByUid,
        Dictionary<string, JournalEntry> createdByUid, string userId, DateTime now)
    {
        if (fields.Uid is { } uid)
        {
            if (entriesByUid.TryGetValue(uid, out var existingRow))
            {
                return (existingRow, false);
            }

            if (createdByUid.TryGetValue(uid, out var createdRow))
            {
                return (createdRow, false);
            }
        }

        var target = new JournalEntry
        {
            ExternalUid = fields.Uid ?? JournalEntryService.NewExternalUid(),
            Title = fields.Title,
            Content = fields.Content,
            CreatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.JournalEntries.Add(target);
        if (fields.Uid is { } newUid)
        {
            createdByUid[newUid] = target;
        }

        return (target, true);
    }

    private static JournalEntryIcsImportResult BuildResult(
        int imported, int updated, ImportSkipCollector skipped, LinkSkipCounts counts) => new()
    {
        ImportedCount = imported,
        UpdatedCount = updated,
        Skipped = skipped.ToGroups((reason, count, samples) => new JournalEntryImportSkipGroup
        {
            Reason = reason,
            Count = count,
            SampleTitles = samples,
        }),
        SkippedTagLinkCount = counts.Tags,
        SkippedContactLinkCount = counts.Contacts,
        SkippedAttachmentCount = counts.Attachments,
        SkippedPhotoCount = counts.Photos,
    };

    // ── Reference collection + batched resolution ───────────────────────────────

    private sealed record CollectedReferences(
        List<string> ContactUids, List<Guid> AttachmentFileIds, List<Guid> PhotoFileIds);

    private static CollectedReferences CollectReferences(IEnumerable<IcalJournal> journals)
    {
        var contactUids = new HashSet<string>(StringComparer.Ordinal);
        var attachmentFileIds = new HashSet<Guid>();
        var photoFileIds = new HashSet<Guid>();

        foreach (var journal in journals)
        {
            foreach (var uid in journal.Properties.GetMany<string>(ContactProperty))
            {
                if (!string.IsNullOrEmpty(uid))
                {
                    contactUids.Add(uid);
                }
            }

            foreach (var attachment in journal.Attachments)
            {
                if (TryParseAttachment(attachment, out var scheme, out var fileId) && fileId is { } id)
                {
                    if (scheme == FileUriScheme)
                    {
                        attachmentFileIds.Add(id);
                    }
                    else if (scheme == PhotoUriScheme)
                    {
                        photoFileIds.Add(id);
                    }
                }
            }
        }

        return new CollectedReferences(
            contactUids.ToList(), attachmentFileIds.ToList(), photoFileIds.ToList());
    }

    // ── Per-block field application ─────────────────────────────────────────────

    private static void ApplyLocation(JournalEntry entry, IcalJournal journal, IcsPlaces places)
    {
        var location = journal.Properties.Get<string>(LocationProperty);

        // Over-length is dropped: on create the field stays unset, on update the existing value is left
        // unchanged (§9 F5) — never cleared by an over-length import.
        if (location is { Length: > MaxLocationLength })
        {
            return;
        }

        var value = string.IsNullOrEmpty(location) ? null : location;
        if (places.Enabled)
        {
            (entry.LocationId, entry.Location) = places.Map(value);
        }
        else
        {
            // Without both location claims the link is left as it is: only the detail text is imported.
            entry.Location = value;
        }
    }

    private void ApplyStatus(JournalEntry entry, IcalJournal journal, bool isNew, DateTime now)
    {
        var intent = FromVJournalStatus(journal.Status);
        if (isNew)
        {
            entry.Archived = intent == StatusIntent.Cancelled ? now : null;
            return;
        }

        switch (intent)
        {
            case StatusIntent.Cancelled:
                // Archive; keep the original timestamp if already archived (idempotent re-import).
                entry.Archived ??= now;
                break;
            case StatusIntent.Active:
                entry.Archived = null;
                break;
            case StatusIntent.Absent:
                // Leave the current archived state untouched — an absent STATUS is not "un-archive" (§5).
                break;
        }
    }

    private static void ApplyTags(
        JournalEntry entry, IcalJournal journal, Dictionary<string, Guid> tagsByName, LinkSkipCounts counts,
        int maxLinksPerKind, ImportSkipCollector skipped)
    {
        var names = journal.Categories.Select(category => category?.Trim()).Where(name => !string.IsNullOrEmpty(name));
        var resolved = ImportLinks.Resolve(
            names, name => tagsByName.TryGetValue(name!, out var tagId) ? tagId : null, maxLinksPerKind,
            onUnresolved: () => counts.Tags++,
            onCapped: () => Capped(entry, maxLinksPerKind, skipped, () => counts.Tags++));

        ImportLinks.Replace(entry.EntryTags, resolved, link => link.JournalTagId,
            tagId => new JournalEntryTag { JournalTagId = tagId });
    }

    private static void ApplyContacts(
        JournalEntry entry, IcalJournal journal, bool canReadContacts,
        IReadOnlyDictionary<string, Guid> contactIdByUid, LinkSkipCounts counts,
        int maxLinksPerKind, ImportSkipCollector skipped)
    {
        var references = journal.Properties.GetMany<string>(ContactProperty)
            .Where(v => !string.IsNullOrEmpty(v)).ToList();

        // The collection is only replaced when the caller can resolve this kind of reference (§9, N1):
        // without contacts.read, every reference is skipped and the existing links are left entirely
        // untouched — a full-replace would otherwise silently wipe links the caller can't re-resolve.
        if (!canReadContacts)
        {
            counts.Contacts += references.Count;
            return;
        }

        var resolved = ImportLinks.Resolve(
            references, uid => contactIdByUid.TryGetValue(uid, out var contactId) ? contactId : null, maxLinksPerKind,
            onUnresolved: () => counts.Contacts++,
            onCapped: () => Capped(entry, maxLinksPerKind, skipped, () => counts.Contacts++));

        ImportLinks.Replace(entry.Contacts, resolved, link => link.ContactId,
            contactId => new JournalEntryContact { ContactId = contactId });
    }

    /// <summary>
    /// One block's attachment references, in file order, split by kind: an inline file or photo resolved (stored or
    /// matched) by its importer, an <c>odyssey-file:</c>/<c>odyssey-photo:</c> reference resolved against what already
    /// exists. Null where a reference did not resolve. Any other ATTACH (a third party's link) is ignored, never counted.
    /// </summary>
    private static async Task<(List<Guid?> Files, List<Guid?> Photos)> ResolveAttachmentsAsync(
        IcalJournal journal, IcsAttachmentSpool spool, InlineFileImporter inlineFiles, InlinePhotoImporter inlinePhotos,
        ImportLookups lookups, string sampleTitle, CancellationToken cancellationToken)
    {
        var files = new List<Guid?>();
        var photoRefs = new List<Guid?>();
        foreach (var attachment in journal.Attachments)
        {
            if (spool.Find(attachment.Uri) is { } spooled)
            {
                if (spooled.IsPhoto)
                {
                    photoRefs.Add(await inlinePhotos.ResolveAsync(spooled, sampleTitle, cancellationToken));
                }
                else
                {
                    files.Add(await inlineFiles.ResolveAsync(spooled, sampleTitle, cancellationToken));
                }
            }
            else if (TryParseAttachment(attachment, out var scheme, out var id))
            {
                if (scheme == FileUriScheme)
                {
                    files.Add(id is { } fileId && lookups.ExistingAttachmentIds.Contains(fileId) ? fileId : null);
                }
                else
                {
                    photoRefs.Add(id is { } reference && lookups.PhotoIdsByReference.TryGetValue(reference, out var photoId) ? photoId : null);
                }
            }
        }

        return (files, photoRefs);
    }

    private static void ApplyAttachments(
        JournalEntry entry, IReadOnlyList<Guid?> references, bool canLinkFiles,
        DateTime now, LinkSkipCounts counts, int maxLinksPerKind, ImportSkipCollector skipped)
    {
        // Without files.read every reference is skipped and the entry's existing links are left untouched (§9, N1). An
        // inline file never reaches here without files.read: the import is refused up front.
        if (!canLinkFiles)
        {
            counts.Attachments += references.Count;
            return;
        }

        var resolved = ImportLinks.Resolve(
            references, fileId => fileId, maxLinksPerKind,
            onUnresolved: () => counts.Attachments++,
            onCapped: () => Capped(entry, maxLinksPerKind, skipped, () => counts.Attachments++));

        ImportLinks.Replace(entry.Attachments, resolved, link => link.FileId,
            fileId => new JournalEntryAttachment { FileId = fileId, CreatedAt = now });
    }

    private static void ApplyPhotos(
        JournalEntry entry, IReadOnlyList<Guid?> references, bool canReadPhotos, DateTime now, LinkSkipCounts counts,
        int maxLinksPerKind, ImportSkipCollector skipped)
    {
        if (!canReadPhotos)
        {
            counts.Photos += references.Count;
            return;
        }

        // A bare reference no existing photo answers to → skip this link only; it never creates one (issue #428 §3.3).
        var resolved = ImportLinks.Resolve(
            references, photoId => photoId, maxLinksPerKind,
            onUnresolved: () => counts.Photos++,
            onCapped: () => Capped(entry, maxLinksPerKind, skipped, () => counts.Photos++));

        ReplacePhotos(entry, resolved, now);
    }

    private static void Capped(JournalEntry entry, int maxLinksPerKind, ImportSkipCollector skipped, Action count)
    {
        count();
        skipped.Add(LinksCappedReason(maxLinksPerKind), entry.Title);
    }

    private static void ReplacePhotos(JournalEntry entry, IReadOnlyList<Guid> desiredPhotoIds, DateTime now)
    {
        var desired = desiredPhotoIds.ToHashSet();
        foreach (var photo in entry.Photos.Where(p => !desired.Contains(p.PhotoId)).ToList())
        {
            entry.Photos.Remove(photo);
        }

        var existing = entry.Photos.ToDictionary(p => p.PhotoId);
        for (var i = 0; i < desiredPhotoIds.Count; i++)
        {
            var photoId = desiredPhotoIds[i];
            if (existing.TryGetValue(photoId, out var photo))
            {
                photo.Position = i;
            }
            else
            {
                entry.Photos.Add(new JournalEntryPhoto { PhotoId = photoId, Position = i, CreatedAt = now });
            }
        }
    }

    // ── Persistence with DB-authoritative collision handling ────────────────────

    // Persists the import in one SaveChanges. A caught unique-constraint violation (a genuine race with a
    // concurrent import choosing the same new UID) is translated into a per-block collision skip and the
    // save retried, so it never surfaces as a raw 500 / unhandled DbUpdateException (§5 step 3.2, AC 25).
    // Detection is provider-agnostic: after a failed save, any Added entry whose UID now exists in the DB
    // is the loser of a race. Returns the number of entries dropped as collisions.
    private async Task<int> SaveWithCollisionHandlingAsync(
        ImportSkipCollector skipped, IcsPlaces places, CancellationToken cancellationToken)
    {
        var droppedCollisions = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await places.SaveChangesAsync(context, cancellationToken);
                return droppedCollisions;
            }
            catch (DbUpdateException)
            {
                var added = context.ChangeTracker.Entries<JournalEntry>()
                    .Where(e => e.State == EntityState.Added)
                    .Select(e => e.Entity)
                    .ToList();
                if (added.Count == 0)
                {
                    throw;
                }

                var addedUids = added.Select(e => e.ExternalUid).ToList();
                var clashingUids = (await context.JournalEntries
                        .AsNoTracking()
                        .Where(e => addedUids.Contains(e.ExternalUid))
                        .Select(e => e.ExternalUid)
                        .ToListAsync(cancellationToken))
                    .ToHashSet(StringComparer.Ordinal);

                var clashing = added.Where(e => clashingUids.Contains(e.ExternalUid)).ToList();
                if (clashing.Count == 0)
                {
                    throw;
                }

                foreach (var entry in clashing)
                {
                    DetachEntryGraph(entry);
                    skipped.Add("External ID is already in use by another journal entry.", entry.Title);
                    droppedCollisions++;
                }
            }
        }

        throw new DomainConflictException("A concurrent import collision could not be resolved. Please retry.");
    }

    // Detaches the dropped entry and its link rows only. A file or photo the import stored for it stays staged and is
    // saved: it is a complete upload in its own right (a Files-store row or a library photo), the stored counts report
    // it truthfully, and a retry of the same file matches it by content instead of storing it twice. This is reached
    // only on a genuine UID race with another writer.
    private void DetachEntryGraph(JournalEntry entry)
    {
        foreach (var link in entry.EntryTags.ToList())
        {
            context.Entry(link).State = EntityState.Detached;
        }

        foreach (var link in entry.Contacts.ToList())
        {
            context.Entry(link).State = EntityState.Detached;
        }

        foreach (var photo in entry.Photos.ToList())
        {
            context.Entry(photo).State = EntityState.Detached;
        }

        foreach (var attachment in entry.Attachments.ToList())
        {
            context.Entry(attachment).State = EntityState.Detached;
        }

        context.Entry(entry).State = EntityState.Detached;
    }

    // ── Parsing helpers ─────────────────────────────────────────────────────────
    // Parses an ATTACH value. Returns false for a non-Odyssey scheme (a real third-party file link — ignored
    // entirely). Returns true with the lowercased scheme; fileId is null when the scheme matches but the
    // value isn't a GUID (a malformed odyssey reference — the caller counts it as a skipped link).
    private static bool TryParseAttachment(Attachment attachment, out string? scheme, out Guid? fileId)
    {
        scheme = null;
        fileId = null;
        var uri = attachment.Uri;
        if (uri is null)
        {
            return false;
        }

        var uriScheme = uri.Scheme.ToLowerInvariant();
        if (uriScheme != FileUriScheme && uriScheme != PhotoUriScheme)
        {
            return false;
        }

        scheme = uriScheme;
        var raw = uri.OriginalString;
        var value = raw.Length > uriScheme.Length + 1 ? raw[(uriScheme.Length + 1)..] : string.Empty;
        if (Guid.TryParse(value, out var parsed))
        {
            fileId = parsed;
        }

        return true;
    }

    private enum StatusIntent
    {
        Absent,
        Active,
        Cancelled,
    }

    private static StatusIntent FromVJournalStatus(string? raw)
    {
        var status = raw?.Trim().ToUpperInvariant();
        return status switch
        {
            null or "" => StatusIntent.Absent,
            "CANCELLED" => StatusIntent.Cancelled,
            // FINAL/DRAFT and any other recognized-active/unknown value keep the entry active (§9).
            _ => StatusIntent.Active,
        };
    }

    private static DateTime? ToEntryDate(CalDateTime? start)
    {
        if (start is null)
        {
            return null;
        }

        try
        {
            return DateTimeNormalization.NormalizeToUtc(start.Value.Date);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    // True when a caller-/import-supplied UID contains a control character or leading/trailing whitespace
    // (§5 step 3.2a). Rejecting edge whitespace keeps the ordinal in-memory match consistent with the
    // column's PAD SPACE (utf8mb4_bin) unique index.
    // The rule the create/update DTOs apply through [RegularExpression] (issue #287 L8), so the import
    // and the API accept the same UIDs. Two details mirror RegularExpressionAttribute rather than a bare
    // IsMatch: the match must span the whole value (.NET's '$' also matches before a trailing '\n'),
    // and "" is rejected here where the attribute lets it through, because an empty UID identifies
    // nothing to match on.
    private static readonly Regex ExternalUidRule = new(JournalEntryExternalUidRules.Pattern, RegexOptions.Compiled);

    internal static bool IsValidExternalUid(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        var match = ExternalUidRule.Match(value);
        return match.Success && match.Index == 0 && match.Length == value.Length;
    }

    /// <summary>Whether the multipart part's content type is acceptable for an <c>.ics</c> upload — the
    /// extension and the parse are the real gates. Public for edge gating in the controller.</summary>
    public static bool IsAcceptedContentType(string? contentType) =>
        ImportFileReader.IsAcceptedContentType(contentType, ImportLinks.CalendarContentTypes);

    private sealed class LinkSkipCounts
    {
        public int Tags;
        public int Contacts;
        public int Attachments;
        public int Photos;
    }
}

