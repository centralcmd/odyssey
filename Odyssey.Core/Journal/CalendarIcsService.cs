using Odyssey.Core;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odyssey.Core.Finance;
using Odyssey.Context;
using Odyssey.Core.Journal.Interop;
using Odyssey.Core.FileStorage.Files;
using Odyssey.Core.Journal.Originals;
using Microsoft.Extensions.Options;
using CalendarEventsIcsExportQueryParams = Odyssey.Dtos.Journal.CalendarEventsIcsExportQueryParams;

namespace Odyssey.Core.Journal;

/// <summary>
/// What one calendar <c>.ics</c> export covers (issue #516 §2 Goal 1): one calendar, one event, one series, or the
/// filtered or unfiltered aggregate across calendars.
/// </summary>
public sealed record CalendarIcsExportScope
{
    private CalendarIcsExportScope()
    {
    }

    public Guid? CalendarId { get; private init; }

    public Guid? EventId { get; private init; }

    public Guid? PatternId { get; private init; }

    public CalendarEventsIcsExportQueryParams? Aggregate { get; private init; }

    public static CalendarIcsExportScope ForCalendar(Guid calendarId) => new() { CalendarId = calendarId };

    public static CalendarIcsExportScope ForEvent(Guid eventId) => new() { EventId = eventId };

    public static CalendarIcsExportScope ForPattern(Guid patternId) => new() { PatternId = patternId };

    public static CalendarIcsExportScope ForAggregate(CalendarEventsIcsExportQueryParams query) => new() { Aggregate = query };
}

/// <summary>
/// Whether an export carries its attachments, and what the caller may read (issue #521 §5.4). <c>Omit</c> writes no
/// <c>ATTACH</c> and needs neither claim; <c>Include</c> refuses with <c>403 calendar_ics_export_attachments_forbidden</c>
/// when the plan links a kind the caller cannot read.
/// </summary>
public sealed record CalendarIcsAttachmentAccess(Odyssey.Dtos.Journal.IcsAttachmentsMode Mode, bool CanReadFiles, bool CanReadPhotos)
{
    public static CalendarIcsAttachmentAccess Default { get; } = new(Odyssey.Dtos.Journal.IcsAttachmentsMode.Include, false, false);
}

/// <summary>The claims an import carrying attachments needs (issue #521 goal 6), evaluated once from the principal.</summary>
public sealed record CalendarIcsImportClaims(bool FilesRead, bool FilesCreate, bool PhotosRead, bool PhotosCreate)
{
    public static CalendarIcsImportClaims None { get; } = new(false, false, false, false);

    public static CalendarIcsImportClaims Trusted { get; } = new(true, true, true, true);
}

/// <summary>
/// ICS (RFC 5545) import/export for calendars (issues #330, #340, #516, #519).
/// </summary>
/// <remarks>
/// <para>
/// <b>Export</b> plans up front and streams (issue #516 §3). The planner projects keys and the few columns the collapse
/// decision needs — never whole entities — and captures the ordered ids of the standalone rows, the series that
/// collapse to one <c>RRULE</c>, and each collapsed series' exceptions. It is the single definition of what an export
/// contains, shared by the export and its summary. <b>No export opens a transaction</b> (B1).
/// </para>
/// <para>
/// A keyed series collapses when <see cref="CanExportWithExceptions"/> holds (issue #519 §8.3): one master with
/// <c>RRULE</c>, <c>DTSTART;TZID=…</c> and an <c>EXDATE</c> per exclusion, one <c>RECURRENCE-ID</c> component per row that
/// differs from what the rule generates, and one <c>VTIMEZONE</c> per planned zone in the head. An occurrence moved into
/// another calendar is an <c>EXDATE</c> in its series' calendar file and a standalone event in its own; the series
/// export carries it as a <c>RECURRENCE-ID</c>. The aggregate collapses only a series with no exceptions at all.
/// </para>
/// <para>
/// <b>Import</b> regroups the file by UID first (<see cref="IcsImportGrouper"/>, §3 component 6): pass 1 validates the
/// whole file and caps it before anything is applied; pass 2 yields one UID group at a time, so a master, its
/// <c>EXDATE</c>s and its overrides import as one series in any order. Groups are applied in batches of at most
/// <see cref="ImportBatchSize"/> groups or <see cref="ImportBatchEntityBudget"/> tracked rows, each in its own
/// transaction that resolves outside, locks the series and events it needs first (ascending), re-resolves under the
/// locks and retries if what it needs changed (v4 §2, v5 §2). A change that would remove a stored row or discard a
/// stored exception needs <c>allowRemovals</c> (and <c>calendar.delete</c>); without it the series is left unchanged
/// and counted in <c>gatedSeriesCount</c>.
/// </para>
/// </remarks>
public partial class CalendarIcsService
{
    /// <summary>UID groups applied per import batch (issue #516 §8.5, #519 v3 §E): a constant, not a setting.</summary>
    public const int ImportBatchSize = 500;

    /// <summary>The rows a batch may track before it is applied early (#519 v3 §E).</summary>
    public const int ImportBatchEntityBudget = 5000;

    /// <summary>
    /// The component characters a batch may span before it is applied early (PR #524 security finding 1). Each
    /// component is parsed and reduced to its bounded <c>ImportItem</c> as it is read, so no parsed <c>Ical.Net</c> graph
    /// outlives its own component.
    /// </summary>
    public const int ImportBatchCharBudget = 16 * 1024 * 1024;

    /// <summary>The overrides one UID group may carry (issue #519 §3 component 6).</summary>
    public const int MaxOverridesPerSeries = 10_000;

    /// <summary>How often one batch re-resolves under its locks before it gives up (v5 §2).</summary>
    public const int MaxBatchAttempts = 3;

    /// <summary>The <c>400</c> code for a file with more components than its size allows (v5 §1).</summary>
    public const string TooManyComponentsCode = IcsImportGrouper.TooManyComponentsCode;

    public const string CancelledUnknownReason = "Event is cancelled.";

    public const string CancelledKeptReason =
        "Event is cancelled in the file; kept, because removing cancelled events was not requested.";

    public const string UidTooLongReason = "UID exceeds the maximum length of 255 characters.";

    public const string GatedReason =
        "Importing this series would remove events or discard separately edited occurrences; tick ‘Allow this import to "
        + "remove events or discard edits’ to apply it.";

    public const string UnknownTimeZoneReason = "Uses a time zone Odyssey doesn't know.";

    public const string OverrideWithoutSeriesReason = "Modified occurrence of a series that is not in this calendar or this file.";

    public const string OverrideMismatchReason = "Modified occurrence does not match the series.";

    public const string ThisAndFutureReason = "Modified ‘this and future’ occurrences are not supported.";

    public const string StrayExclusionReason = "Excluded date is not an occurrence of the series.";

    public const string PastRemovalReason = "Excluded or cancelled occurrence is in the past; it was kept.";

    public const string OverrideCapReason = "Series has more modified occurrences than one import accepts.";

    public const string DuplicateMasterReason = "Series appears more than once in the file; only the last one was applied.";

    public const string SharedUidReason = "Event shares its UID with a recurring series in the file.";

    public const string RecurrenceDatesReason = "Recurrence dates (RDATE) are not supported.";

    /// <summary>Removed ids per audit line, so one batch's removal of a large series never becomes one huge entry.</summary>
    internal const int AuditIdsPerLine = 500;

    private const int MaxUidLength = 255;

    private const string SyntheticUidSuffix = "@odyssey.local";

    // How a UID is compared in memory, matching how the database compares it: the ExternalUid and ImportSourceUid
    // columns use the server's default case- and accent-insensitive collation, so a keyed query returns a case variant
    // and the in-memory pick must accept it too, or it loads the row and then creates a duplicate (architect nit 7).
    // An exact match is still preferred among the candidates.
    internal static readonly StringComparer UidComparer =
        StringComparer.Create(System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace);
    private const string UntitledEvent = "(untitled)";
    private const string ProductId = "-//Odyssey//Calendar//EN";
    private const int MaxLocationLength = 300;

    private readonly OdysseyContext context;
    private readonly ILogger<CalendarIcsService> logger;
    private readonly IImportExportLimitsLookup limits;
    private readonly IJournalLimitsLookup journalLimits;
    private readonly TimeProvider timeProvider;
    private readonly StoredFileContentSource? fileContent;
    private readonly PhotoOriginalSource? photoContent;
    private readonly StoredFileWriter? fileWriter;
    private readonly FileValidationService fileValidation;
    private readonly IUploadLimitsLookup? uploadLimits;
    private readonly PhotoService? photoService;
    private readonly long transportFileCeiling;

    // journalLimits is a SECOND lookup, newly injected in issue #434. The occurrence cap (key 11) and
    // the event-duration bound (key 7) live on JournalLimits because their other consumers are
    // RecurrencePatternService and CalendarEventService, and a setting must have exactly one owner,
    // one cache key, one eviction and one degraded rule. Mirroring them onto ImportExportLimits to
    // save a lookup was tried and abandoned: a descriptor can evict only one cache entry, so a
    // mirrored value would go stale on one of its two readers for up to 30s.
    public CalendarIcsService(
        OdysseyContext context, ILogger<CalendarIcsService> logger, IImportExportLimitsLookup limits,
        IJournalLimitsLookup journalLimits, TimeProvider? timeProvider = null,
        StoredFileContentSource? fileContent = null, PhotoOriginalSource? photoContent = null,
        StoredFileWriter? fileWriter = null, FileValidationService? fileValidation = null,
        IUploadLimitsLookup? uploadLimits = null, PhotoService? photoService = null,
        IOptions<FileStorageOptions>? fileStorageOptions = null)
    {
        // The file and photo collaborators are optional so a test without a store still builds one (the journal's
        // shape): without them linked content exports as bare references and inline content cannot be imported.
        this.fileContent = fileContent;
        this.photoContent = photoContent;
        this.fileWriter = fileWriter;
        this.fileValidation = fileValidation ?? new FileValidationService();
        this.uploadLimits = uploadLimits;
        this.photoService = photoService;
        transportFileCeiling = (fileStorageOptions?.Value ?? new FileStorageOptions()).MaxFileSizeBytes;
        this.context = context;
        this.logger = logger;
        this.limits = limits;
        this.journalLimits = journalLimits;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// The directory the import spill and any upload copy are written to: <see cref="ImportSpool.Directory"/> — the
    /// <c>import_spool</c> volume behind <c>ASPNETCORE_TEMP</c> in the images, where the upload itself is buffered —
    /// unless a test points it elsewhere. Never the OS temp directory by default, which can be a RAM-backed tmpfs.
    /// </summary>
    internal string SpillDirectory { get; set; } = ImportSpool.Directory();

    /// <summary>A fixed grouping key, for a test that must know the order groups commit in; random per import otherwise.</summary>
    internal byte[]? GroupingKey { get; set; }

    /// <summary>The grouper's records per run, so a test can force spilled runs and merge rounds on a small file.</summary>
    internal int GroupingRecordsPerRun { get; set; } = IcsImportGrouper.RunBufferBytes / IcsImportGrouper.RecordSize;

    /// <summary>
    /// The groups per import batch: always <see cref="ImportBatchSize"/> in production. Only a test lowers it, so a
    /// multi-batch scenario needs a handful of groups rather than thousands.
    /// </summary>
    internal int ImportBatchGroups { get; set; } = ImportBatchSize;

    // A VEVENT's UID that is itself one of Odyssey's own synthesized "{primary-key}@odyssey.local"
    // forms (issue #345 security review, F1) must never be persisted as ExternalUid: an export writes
    // ExternalUid verbatim, so a row in calendar B carrying A's synthetic UID would put A's key in B's
    // file, and importing that file into A would overwrite A's unrelated row in place. Such a UID is
    // matched by key only within the target calendar, and kept as ImportSourceUid (issue #516 §8.2),
    // which is never exported and never read as a key.
    private static bool IsSyntheticUid(string uid) =>
        uid.EndsWith(SyntheticUidSuffix, StringComparison.Ordinal)
        && Guid.TryParse(uid[..^SyntheticUidSuffix.Length], out _);

    // The value to persist as ExternalUid for a created row — null when the incoming UID is absent or
    // synthetic; the UID unchanged otherwise.
    private static string? PersistableExternalUid(string? uid) => uid is null || IsSyntheticUid(uid) ? null : uid;

    // The value to persist as ImportSourceUid for a created row: the UID only when it is synthetic (rule 4).
    private static string? ImportSourceUidFor(string? uid) => uid is not null && IsSyntheticUid(uid) ? uid : null;

    /// <summary>Whether the multipart part's content type is acceptable for an <c>.ics</c> upload. The
    /// <c>.ics</c> extension and the parse itself are the real validity gates; the content type is only
    /// a hint, so we accept what browsers/OSes routinely send for calendar files (many map <c>.ics</c>
    /// to <c>application/octet-stream</c> or <c>text/plain</c>, and some omit it) and only reject a
    /// clearly-wrong declared type like <c>application/json</c>. Public so the controller can gate at
    /// the edge (defense-in-depth for direct service callers keeps it here too).</summary>
    public static bool IsAcceptedContentType(string? contentType) =>
        ImportFileReader.IsAcceptedContentType(contentType, ImportLinks.CalendarContentTypes);

    // Builds the download filename "yyyyMMdd_<calendar name>.ics" (date = export date), stripping quotes
    // and control characters from the name for a safe Content-Disposition value.
    private static string BuildFileName(string name, DateTime exportDate)
    {
        var cleaned = new string(name.Where(c => !char.IsControl(c) && c is not ('"' or '\\' or '/')).ToArray()).Trim();
        if (string.IsNullOrEmpty(cleaned))
        {
            cleaned = "calendar";
        }

        return $"{exportDate:yyyyMMdd}_{cleaned}.ics";
    }
}

/// <summary>
/// The upload as the grouper needs it: seekable, with a known length (issue #519 implementation note 5). The buffered
/// form file already is; anything else is copied, under the byte cap, to a temporary file that is deleted on dispose.
/// </summary>
internal sealed class SeekableUpload : IAsyncDisposable
{
    private readonly string? tempPath;

    private SeekableUpload(Stream stream, string? tempPath)
    {
        Stream = stream;
        this.tempPath = tempPath;
    }

    public Stream Stream { get; }

    public static async Task<SeekableUpload> OpenAsync(Stream source, long maxBytes, string directory, CancellationToken cancellationToken)
    {
        if (source.CanSeek)
        {
            if (source.Length > maxBytes)
            {
                throw new DomainValidationException($"The .ics file exceeds the {maxBytes / (1024 * 1024)} MB limit.");
            }

            return new SeekableUpload(source, null);
        }

        var path = Path.Combine(directory, $"{IcsImportGrouper.SpillFilePrefix}{Guid.NewGuid():N}.upload");
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    throw new DomainValidationException($"The .ics file exceeds the {maxBytes / (1024 * 1024)} MB limit.");
                }

                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            file.Seek(0, SeekOrigin.Begin);
            return new SeekableUpload(file, path);
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (tempPath is not null)
        {
            await Stream.DisposeAsync();
        }
    }
}
