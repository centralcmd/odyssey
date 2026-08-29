using Odyssey.Core;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odyssey.Core.Finance;
using Odyssey.Context;
using Odyssey.Dtos.Journal;
using Odyssey.Core.Journal;
using Odyssey.Core.Journal.Interop;
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
    internal static string LinksCappedReason(int maxLinksPerKind) =>
        $"Links over the per-entry cap of {maxLinksPerKind} were not imported.";

    private static readonly string[] AcceptedContentTypes =
        ["text/calendar", "application/octet-stream", "text/plain"];

    private readonly OdysseyContext context;
    private readonly IContactLookup contacts;
    private readonly IFileLookup files;
    private readonly IPhotoLookup photos;
    private readonly IImportExportLimitsLookup limits;
    private readonly IJournalLimitsLookup journalLimits;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<JournalEntryIcsService> logger;

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
        TimeProvider? timeProvider = null)
    {
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

    /// <summary>Exports a single entry by id, or null when it does not exist (→ 404).</summary>
    public async Task<JournalEntryIcsExport?> ExportSingleAsync(
        Guid id, bool includeContacts, CancellationToken cancellationToken = default)
    {
        var entry = await context.JournalEntries
            .AsNoTracking()
            .Include(e => e.EntryTags)
            .Include(e => e.Contacts)
            .Include(e => e.Photos)
            .Include(e => e.Attachments)
            .FirstOrDefaultAsync(e => e.JournalEntryId == id, cancellationToken);
        if (entry is null)
        {
            return null;
        }

        var content = await SerializeAsync([entry], includeContacts, cancellationToken);
        var exportDate = timeProvider.GetUtcNow().UtcDateTime;
        return new JournalEntryIcsExport($"odyssey-journal-entry-{exportDate:yyyyMMdd-HHmmss}Z.ics", content);
    }

    /// <summary>
    /// Streams every entry matching <paramref name="query"/>'s filters directly to
    /// <paramref name="output"/>, in row-keyset chunks (issue #343 §5 Goal 8). Unlike the list endpoint,
    /// archived entries are <b>included</b> when no status is supplied (§5 — backup/migration use case).
    /// Throws <see cref="ExportLimitExceededException"/> (→ 400) if the matched set exceeds the
    /// configured <c>JournalIcsMaxExportRows</c> cap — before any row is fetched, and before
    /// <paramref name="onReady"/> is invoked. <paramref name="onReady"/> is called exactly once, with
    /// the file name and row count, before the first chunk is written.
    /// </summary>
    public async Task ExportStreamingAsync(
        JournalEntriesQueryParams query, bool includeContacts, Stream output, Action<string, int> onReady,
        CancellationToken cancellationToken = default)
    {
        var baseQuery = JournalEntryService.ApplyFilters(
            context.JournalEntries.AsNoTracking(),
            query.Search, query.TagIds, query.ContactIds, query.From, query.To);

        baseQuery = query.Status switch
        {
            ArchivalStatus.Active => baseQuery.Where(e => e.Archived == null),
            ArchivalStatus.Archived => baseQuery.Where(e => e.Archived != null),
            _ => baseQuery, // omitted → all statuses, including archived (deliberate divergence from the list default)
        };

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
        var count = orderedIds.Count;
        if (maxExportRows is { } max && count > max)
        {
            throw new ExportLimitExceededException(max);
        }

        var exportDate = timeProvider.GetUtcNow().UtcDateTime;
        var suffix = HasAnyFilter(query) ? "-filtered" : string.Empty;
        onReady($"odyssey-journal-entries{suffix}-{exportDate:yyyyMMdd-HHmmss}Z.ics", count);

        var (head, tail) = IcsChunkSerializer.BuildEnvelope(ProductId);
        await IcsChunkSerializer.WriteAsync(output, head, cancellationToken);
        var writtenBytes = (long)Encoding.UTF8.GetByteCount(head);

        var written = 0;
        foreach (var idBatch in orderedIds.Chunk(ExportChunking.ChunkSize))
        {
            var rows = await baseQuery
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

            var chunkCalendar = await BuildCalendarAsync(entries, includeContacts, cancellationToken);
            var chunkText = IcsChunkSerializer.SerializeComponents(chunkCalendar);
            var chunkBytes = Encoding.UTF8.GetByteCount(chunkText);

            // Unlike the row-count cap above, the byte-size cap can't be rejected up front (total
            // output size isn't knowable until it's generated) — once writing this chunk would cross
            // it, stop without writing it. X-Odyssey-Export-Rows already promised the full row count,
            // so the API client's existing completeness check treats this as a failed download.
            if (writtenBytes + chunkBytes > maxExportBytes)
            {
                logger.LogWarning(
                    "Journal entries .ics export truncated at {WrittenBytes} bytes (cap {MaxBytes}); " +
                    "{WrittenRows}/{TotalRows} entries delivered.",
                    writtenBytes, maxExportBytes, written, count);
                break;
            }

            await IcsChunkSerializer.WriteAsync(output, chunkText, cancellationToken);
            writtenBytes += chunkBytes;
            written += entries.Count;
        }

        await IcsChunkSerializer.WriteAsync(output, tail, cancellationToken);
    }

    private static bool HasAnyFilter(JournalEntriesQueryParams query) =>
        !string.IsNullOrWhiteSpace(query.Search)
        || query.TagIds is { Length: > 0 }
        || query.ContactIds is { Length: > 0 }
        || query.From is not null
        || query.To is not null
        || query.Status is not null;

    private async Task<string> SerializeAsync(
        IReadOnlyList<JournalEntry> entries, bool includeContacts, CancellationToken cancellationToken)
    {
        var ical = await BuildCalendarAsync(entries, includeContacts, cancellationToken);
        ical.ProductId = ProductId;
        return new CalendarSerializer(ical).SerializeToString() ?? string.Empty;
    }

    // Batches the tag/photo/(optionally) contact lookups for exactly the entries passed in — reused
    // both by the single-shot SerializeAsync above and, per-chunk, by the streaming export (issue #343
    // §5 Goal 8), so each chunk's lookups stay scoped to that chunk rather than the whole matched set.
    private async Task<IcalCalendar> BuildCalendarAsync(
        IReadOnlyList<JournalEntry> entries, bool includeContacts, CancellationToken cancellationToken)
    {
        var tagIds = entries.SelectMany(e => e.EntryTags.Select(t => t.JournalTagId)).Distinct().ToList();
        var tagNames = tagIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await context.JournalTags
                .Where(t => tagIds.Contains(t.JournalTagId))
                .ToDictionaryAsync(t => t.JournalTagId, t => t.Name, cancellationToken);

        var photoIds = entries.SelectMany(e => e.Photos.Select(p => p.PhotoId)).Distinct().ToList();
        var photoFileIds = photoIds.Count == 0
            ? new Dictionary<Guid, Guid>()
            : await photos.ResolveFileIdsAsync(photoIds, cancellationToken);

        IReadOnlyDictionary<Guid, string>? contactUids = null;
        if (includeContacts)
        {
            var contactIds = entries
                .SelectMany(e => e.Contacts.Select(c => c.ContactId)).Distinct().ToList();
            contactUids = contactIds.Count == 0
                ? new Dictionary<Guid, string>()
                : await contacts.ResolveExternalUidsAsync(contactIds, cancellationToken);
        }

        var ical = new IcalCalendar();
        foreach (var entry in entries)
        {
            ical.Journals.Add(BuildVJournal(entry, tagNames, photoFileIds, contactUids));
        }

        return ical;
    }

    private static IcalJournal BuildVJournal(
        JournalEntry entry,
        IReadOnlyDictionary<Guid, string> tagNames,
        IReadOnlyDictionary<Guid, Guid> photoFileIds,
        IReadOnlyDictionary<Guid, string>? contactUids)
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

        if (!string.IsNullOrEmpty(entry.Location))
        {
            journal.Properties.Add(new CalendarProperty(LocationProperty, entry.Location));
        }

        foreach (var link in entry.EntryTags)
        {
            if (tagNames.TryGetValue(link.JournalTagId, out var name))
            {
                journal.Categories.Add(name);
            }
        }

        // X-ODYSSEY-CONTACT is emitted only when the caller holds contacts.read (§9/§10.2).
        if (contactUids is not null)
        {
            foreach (var link in entry.Contacts)
            {
                if (contactUids.TryGetValue(link.ContactId, out var uid))
                {
                    journal.Properties.Add(new CalendarProperty(ContactProperty, uid));
                }
            }
        }

        // Photos carry the underlying FileId (the internal PhotoId is instance-local, §9). A link whose
        // library Photo no longer resolves is dropped, mirroring the read DTO.
        foreach (var photo in entry.Photos.OrderBy(p => p.Position))
        {
            if (photoFileIds.TryGetValue(photo.PhotoId, out var fileId))
            {
                journal.Attachments.Add(new Attachment { Uri = new Uri($"{PhotoUriScheme}:{fileId}") });
            }
        }

        foreach (var attachment in entry.Attachments.OrderBy(a => a.CreatedAt))
        {
            journal.Attachments.Add(new Attachment { Uri = new Uri($"{FileUriScheme}:{attachment.FileId}") });
        }

        return journal;
    }

    // ---------------------------------------------------------------- Import

    public async Task<JournalEntryIcsImportResult> ImportAsync(
        Stream icsFile, long contentLength, string? contentType, string userId,
        bool canLinkFiles, bool canReadContacts, CancellationToken cancellationToken = default)
    {
        var journals = await ParseJournalsAsync(icsFile, contentLength, contentType, cancellationToken);
        var lookups = await LoadLookupsAsync(journals, userId, canLinkFiles, canReadContacts, cancellationToken);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        // One snapshot of each record for the whole import, so a concurrent admin write cannot split
        // one file across two values of the same setting.
        var importLimits = await limits.GetAsync(cancellationToken);
        var maxLinksPerKind = (await journalLimits.GetAsync(cancellationToken)).JournalEntryMaxLinksPerKind;
        var skipped = new ImportSkipCollector(importLimits.ImportMaxSamplesPerSkipReason);
        var counts = new LinkSkipCounts();
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
            ApplyLocation(target, journal);
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
            ApplyAttachments(
                target, journal, canLinkFiles, lookups.ExistingAttachmentIds, now, counts, maxLinksPerKind, skipped);
            ApplyPhotos(
                target, journal, canLinkFiles, lookups.ImageFileIds, lookups.PhotoIdsByFileId, now, counts,
                maxLinksPerKind, skipped);
        }

        var collisions = await SaveWithCollisionHandlingAsync(skipped, cancellationToken);
        imported -= collisions;

        return BuildResult(imported, updated, skipped, counts);
    }

    /// <summary>
    /// Reads and parses the upload, rejecting anything that makes the file as a whole unusable — wrong
    /// content type, over the size cap, unparseable, or more VJOURNALs than the batch limit. These are
    /// whole-request failures; every per-block problem after this point is a skip, not a throw.
    /// </summary>
    private async Task<List<IcalJournal>> ParseJournalsAsync(
        Stream icsFile, long contentLength, string? contentType, CancellationToken cancellationToken)
    {
        if (!IsAcceptedContentType(contentType))
        {
            throw new DomainValidationException("The uploaded file must be a calendar file (text/calendar).");
        }

        var cap = await limits.GetAsync(cancellationToken);
        var maxImportBytes = cap.JournalIcsMaxImportBytes;
        var maxVJournals = cap.JournalIcsMaxImportEntries ?? int.MaxValue;

        if (contentLength > maxImportBytes)
        {
            throw new DomainValidationException($"The .ics file exceeds the {maxImportBytes / (1024 * 1024)} MB limit.");
        }

        using var reader = ImportFileReader.OpenBoundedTextReader(icsFile, maxImportBytes, ".ics");

        IcalCalendar? parsed;
        try
        {
            parsed = IcalCalendar.Load(reader);
        }
        // DomainValidationException excluded: OpenBoundedTextReader's byte-cap check now runs lazily,
        // inside this parse, rather than fully before it — catching it here would replace the specific
        // "exceeds the N MB limit" message with the generic parse-failure one (issue #343 §5).
        catch (Exception ex) when (ex is not OperationCanceledException and not DomainValidationException)
        {
            throw new DomainValidationException("The file could not be parsed as a valid iCalendar (.ics) file.");
        }

        if (parsed is null)
        {
            throw new DomainValidationException("The file could not be parsed as a valid iCalendar (.ics) file.");
        }

        var journals = parsed.Journals;
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
        IReadOnlySet<Guid> ImageFileIds,
        IReadOnlyDictionary<Guid, Guid> PhotoIdsByFileId);

    private async Task<ImportLookups> LoadLookupsAsync(
        List<IcalJournal> journals, string userId, bool canLinkFiles, bool canReadContacts,
        CancellationToken cancellationToken)
    {
        // Match targets by UID → ExternalUid. Only load entries whose (well-formed) UID appears in this file.
        var incomingUids = journals
            .Select(j => j.Uid)
            .Where(u => u is not null && !HasControlOrEdgeWhitespace(u) && u.Length <= MaxExternalUidLength)
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

        // Non-archived tags, indexed by case-insensitive exact name (first wins on a duplicate name).
        var tagRows = await context.JournalTags
            .Where(t => t.Archived == null)
            .Select(t => new { t.JournalTagId, t.Name })
            .ToListAsync(cancellationToken);
        var tagsByName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tagRows)
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
        var (imageFileIds, photoIdByFileId) = await ResolvePhotoReferencesAsync(
            references.PhotoFileIds, canLinkFiles, userId, cancellationToken);

        return new ImportLookups(
            existing.ToDictionary(e => e.ExternalUid, StringComparer.Ordinal),
            tagsByName,
            contactIdByUid,
            existingAttachmentIds,
            imageFileIds,
            photoIdByFileId);
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
        if (rawUid is not null && HasControlOrEdgeWhitespace(rawUid))
        {
            skipped.Add("Invalid UID: control characters or leading/trailing whitespace not allowed.", sample);
            return null;
        }

        // A UID longer than the column would, on a strict-mode MariaDB, surface as a non-duplicate
        // DbUpdateException the collision handler doesn't recognize — a whole-batch 500 that breaks the
        // skip-and-continue contract. Bound it here so an over-length UID is a clean per-block skip.
        if (rawUid is { Length: > MaxExternalUidLength })
        {
            skipped.Add($"UID exceeds the maximum length of {MaxExternalUidLength} characters.", sample);
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

    // Pre-filters referenced photo file ids to image types, then batch find-or-creates library Photos for
    // them (§5 step 3.7). A non-image or unknown reference is left out of both sets, so the per-block apply
    // counts it as a skipped photo link rather than reaching PhotoLookup's throw-on-non-image path.
    private async Task<(IReadOnlySet<Guid> ImageFileIds, IReadOnlyDictionary<Guid, Guid> PhotoIdByFileId)>
        ResolvePhotoReferencesAsync(
            IReadOnlyCollection<Guid> photoFileIds, bool canLinkFiles, string userId, CancellationToken cancellationToken)
    {
        if (!canLinkFiles || photoFileIds.Count == 0)
        {
            return (new HashSet<Guid>(), new Dictionary<Guid, Guid>());
        }

        var imageFileIds = await files.ExistingImageIdsAsync(photoFileIds.ToList(), cancellationToken);
        if (imageFileIds.Count == 0)
        {
            return (imageFileIds, new Dictionary<Guid, Guid>());
        }

        var photoIdByFileId = await photos.FindOrCreatePhotoIdsForFilesAsync(
            imageFileIds.ToList(), userId, cancellationToken);
        return (imageFileIds, photoIdByFileId);
    }

    // ── Per-block field application ─────────────────────────────────────────────

    private static void ApplyLocation(JournalEntry entry, IcalJournal journal)
    {
        var location = journal.Properties.Get<string>(LocationProperty);

        // Over-length is dropped: on create the field stays unset, on update the existing value is left
        // unchanged (§9 F5) — never cleared by an over-length import.
        if (location is { Length: > MaxLocationLength })
        {
            return;
        }

        entry.Location = string.IsNullOrEmpty(location) ? null : location;
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
        var resolved = new List<Guid>();
        foreach (var category in journal.Categories)
        {
            if (resolved.Count >= maxLinksPerKind)
            {
                counts.Tags++;
                skipped.Add(LinksCappedReason(maxLinksPerKind), entry.Title);
                continue;
            }

            var name = category?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (!tagsByName.TryGetValue(name, out var tagId))
            {
                counts.Tags++;
                continue;
            }

            if (!resolved.Contains(tagId))
            {
                resolved.Add(tagId);
            }
        }

        var desired = resolved.ToHashSet();
        foreach (var link in entry.EntryTags.Where(t => !desired.Contains(t.JournalTagId)).ToList())
        {
            entry.EntryTags.Remove(link);
        }

        var current = entry.EntryTags.Select(t => t.JournalTagId).ToHashSet();
        foreach (var tagId in resolved.Where(id => current.Add(id)))
        {
            entry.EntryTags.Add(new JournalEntryTag { JournalTagId = tagId });
        }
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

        var resolved = new List<Guid>();
        foreach (var uid in references)
        {
            if (resolved.Count >= maxLinksPerKind)
            {
                counts.Contacts++;
                skipped.Add(LinksCappedReason(maxLinksPerKind), entry.Title);
                continue;
            }

            if (!contactIdByUid.TryGetValue(uid, out var contactId))
            {
                counts.Contacts++;
                continue;
            }

            if (!resolved.Contains(contactId))
            {
                resolved.Add(contactId);
            }
        }

        ReplaceContacts(entry, resolved);
    }

    private static void ReplaceContacts(JournalEntry entry, IReadOnlyList<Guid> desiredIds)
    {
        var desired = desiredIds.ToHashSet();
        foreach (var link in entry.Contacts.Where(c => !desired.Contains(c.ContactId)).ToList())
        {
            entry.Contacts.Remove(link);
        }

        var current = entry.Contacts.Select(c => c.ContactId).ToHashSet();
        foreach (var contactId in desiredIds.Where(id => current.Add(id)))
        {
            entry.Contacts.Add(new JournalEntryContact { ContactId = contactId });
        }
    }

    private static void ApplyAttachments(
        JournalEntry entry, IcalJournal journal, bool canLinkFiles, IReadOnlySet<Guid> existingFileIds,
        DateTime now, LinkSkipCounts counts, int maxLinksPerKind, ImportSkipCollector skipped)
    {
        var references = SchemedFileIds(journal, FileUriScheme);
        if (!canLinkFiles)
        {
            counts.Attachments += references.Count;
            return;
        }

        var resolved = new List<Guid>();
        foreach (var fileId in references)
        {
            if (resolved.Count >= maxLinksPerKind)
            {
                counts.Attachments++;
                skipped.Add(LinksCappedReason(maxLinksPerKind), entry.Title);
                continue;
            }

            if (!existingFileIds.Contains(fileId))
            {
                counts.Attachments++;
                continue;
            }

            if (!resolved.Contains(fileId))
            {
                resolved.Add(fileId);
            }
        }

        ReplaceAttachments(entry, resolved, now);
    }

    private static void ReplaceAttachments(JournalEntry entry, IReadOnlyList<Guid> desiredFileIds, DateTime now)
    {
        var desired = desiredFileIds.ToHashSet();
        foreach (var link in entry.Attachments.Where(a => !desired.Contains(a.FileId)).ToList())
        {
            entry.Attachments.Remove(link);
        }

        var current = entry.Attachments.Select(a => a.FileId).ToHashSet();
        foreach (var fileId in desiredFileIds.Where(id => current.Add(id)))
        {
            entry.Attachments.Add(new JournalEntryAttachment { FileId = fileId, CreatedAt = now });
        }
    }

    private static void ApplyPhotos(
        JournalEntry entry, IcalJournal journal, bool canLinkFiles, IReadOnlySet<Guid> imageFileIds,
        IReadOnlyDictionary<Guid, Guid> photoIdByFileId, DateTime now, LinkSkipCounts counts,
        int maxLinksPerKind, ImportSkipCollector skipped)
    {
        var references = SchemedFileIds(journal, PhotoUriScheme);
        if (!canLinkFiles)
        {
            counts.Photos += references.Count;
            return;
        }

        var resolved = new List<Guid>();
        foreach (var fileId in references)
        {
            if (resolved.Count >= maxLinksPerKind)
            {
                counts.Photos++;
                skipped.Add(LinksCappedReason(maxLinksPerKind), entry.Title);
                continue;
            }

            // Not an image (or unknown), or the library Photo couldn't be resolved → skip this link only.
            if (!imageFileIds.Contains(fileId) || !photoIdByFileId.TryGetValue(fileId, out var photoId))
            {
                counts.Photos++;
                continue;
            }

            if (!resolved.Contains(photoId))
            {
                resolved.Add(photoId);
            }
        }

        ReplacePhotos(entry, resolved, now);
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
    private async Task<int> SaveWithCollisionHandlingAsync(ImportSkipCollector skipped, CancellationToken cancellationToken)
    {
        var droppedCollisions = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await context.SaveChangesAsync(cancellationToken);
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

    private static List<Guid> SchemedFileIds(IcalJournal journal, string scheme)
    {
        var ids = new List<Guid>();
        foreach (var attachment in journal.Attachments)
        {
            if (TryParseAttachment(attachment, out var attachmentScheme, out var fileId)
                && attachmentScheme == scheme && fileId is { } id)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

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
    private static bool HasControlOrEdgeWhitespace(string value) =>
        value.Length == 0
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
        || value.Any(char.IsControl);

    /// <summary>Whether the multipart part's content type is acceptable for an <c>.ics</c> upload — the
    /// extension and the parse are the real gates. Public for edge gating in the controller.</summary>
    public static bool IsAcceptedContentType(string? contentType) =>
        ImportFileReader.IsAcceptedContentType(contentType, AcceptedContentTypes);

    private sealed class LinkSkipCounts
    {
        public int Tags;
        public int Contacts;
        public int Attachments;
        public int Photos;
    }
}

public sealed record JournalEntryIcsExport(string FileName, string Content);
