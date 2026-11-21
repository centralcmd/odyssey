using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.Journal.Interop;
using Odyssey.Core.Locations;
using Odyssey.Core.FileStorage;
using IcalCalendar = Ical.Net.Calendar;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;
using ContextCalendarEvent = Odyssey.Context.CalendarEvent;
using IcsImportResult = Odyssey.Dtos.Journal.IcsImportResult;
using IcsImportSkipGroup = Odyssey.Dtos.Journal.IcsImportSkipGroup;

namespace Odyssey.Core.Journal;

public partial class CalendarIcsService
{
    // ---------------------------------------------------------------- Import

    private enum ItemKind
    {
        Standalone,
        Master,
        Override,
        Cancelled,
        CancelledOverride,
    }

    // A date read off the file (RECURRENCE-ID), resolved to a key only once the master's zone is known.
    private readonly record struct FileDate(DateTime Value, string? TzId, bool IsUtc, bool HasTime);

    // One VEVENT that passed validation, ready for its batch to resolve and apply.
    private sealed class ImportItem
    {
        public required ItemKind Kind { get; init; }

        public required string SampleTitle { get; init; }

        public required string? Uid { get; init; }

        public long Offset { get; init; }

        public bool HasRule { get; init; }

        public string Title { get; init; } = string.Empty;

        public string? Description { get; init; }

        public ImportedPlace Place { get; init; } = new(null, null, false);

        public DateTime Start { get; init; }

        public DateTime End { get; init; }

        public bool IsAllDay { get; init; }

        public string? ZoneId { get; init; }

        public bool Adjusted { get; init; }

        public RecurrencePattern? Pattern { get; init; }

        public List<GeneratedOccurrence> Occurrences { get; init; } = [];

        /// <summary>The master's <c>EXDATE</c> keys, in its own zone.</summary>
        public List<DateTime> ExDates { get; init; } = [];

        public FileDate? RecurrenceId { get; init; }

        /// <summary>Its kinded <c>ATTACH</c> lines, in file order (issue #521 §8.3); kindless ones are already counted.</summary>
        public List<CalendarAttachSlot> Attachments { get; set; } = [];
    }

    // What spans the whole import: the caps read once, the counters, the place cache and the occurrence budget. Nothing
    // here grows with the file (N1).
    private sealed class ImportRun
    {
        public required Guid CalendarId { get; init; }

        public required string UserId { get; init; }

        public required DateTime Now { get; init; }

        public required IcsPlaces Places { get; set; }

        public required LocationClaims Claims { get; init; }

        public required ImportSkipCollector Skipped { get; init; }

        public required bool AllowRemovals { get; init; }

        public required TimeZoneInfo? DefaultZone { get; init; }

        public required int MaxAggregateOccurrences { get; init; }

        public required int MaxEventDurationDays { get; init; }

        public required int MaxGeneratedOccurrences { get; init; }

        public required int MaxSamples { get; init; }

        public required CalendarAttachmentImporter Importer { get; init; }

        public required int MaxLinksPerKind { get; init; }

        /// <summary>Attachment skips decided while reading (phase A and classification), outside any attempt.</summary>
        public int SkippedAttachments { get; set; }

        /// <summary>Attachments on <c>RECURRENCE-ID</c> components, ignored while reading.</summary>
        public int IgnoredOccurrenceAttachments { get; set; }

        public void SkipAttachment(string reason)
        {
            Skipped.Add(reason, null);
            SkippedAttachments++;
        }

        public ImportTally Totals { get; } = new();

        public int CommittedBatches { get; set; }

        public int CommittedEvents { get; set; }
    }

    // One attempt's counts, merged into the run only when its batch commits, so a retried batch never counts twice.
    private sealed class ImportTally
    {
        public int Imported { get; set; }

        public int Updated { get; set; }

        public int Removed { get; set; }

        public List<string> RemovedSamples { get; } = [];

        public bool AnySeriesRegenerated { get; set; }

        public int Aggregate { get; set; }

        public int OverridesApplied { get; set; }

        public int ExclusionsApplied { get; set; }

        public int DroppedOverrides { get; set; }

        public int DroppedExclusions { get; set; }

        public int Gated { get; set; }

        public int Adjusted { get; set; }

        public List<(string Reason, string? Sample)> Skips { get; } = [];

        public int SkippedAttachments { get; set; }

        public int IgnoredOccurrenceAttachments { get; set; }

        public int ReplacedLinkSets { get; set; }

        public int RemovedLinks { get; set; }

        /// <summary>The attempt's attached and matched reservations, handed to the importer once the batch commits.</summary>
        public CalendarAttachmentImporter.Attempt Links { get; } = new();

        public List<Guid> RemovedEventIds { get; } = [];

        public List<Guid> RemovedSeriesIds { get; } = [];

        public List<DateTime> DroppedOverrideKeys { get; } = [];

        public List<DateTime> DroppedExclusionKeys { get; } = [];

        public void MergeInto(ImportRun run)
        {
            var totals = run.Totals;
            totals.Imported += Imported;
            totals.Updated += Updated;
            totals.Removed += Removed;
            foreach (var sample in RemovedSamples)
            {
                if (totals.RemovedSamples.Count < run.MaxSamples)
                {
                    totals.RemovedSamples.Add(sample);
                }
            }

            totals.AnySeriesRegenerated |= AnySeriesRegenerated;
            totals.Aggregate += Aggregate;
            totals.OverridesApplied += OverridesApplied;
            totals.ExclusionsApplied += ExclusionsApplied;
            totals.DroppedOverrides += DroppedOverrides;
            totals.DroppedExclusions += DroppedExclusions;
            totals.Gated += Gated;
            totals.Adjusted += Adjusted;
            totals.SkippedAttachments += SkippedAttachments;
            totals.IgnoredOccurrenceAttachments += IgnoredOccurrenceAttachments;
            totals.ReplacedLinkSets += ReplacedLinkSets;
            totals.RemovedLinks += RemovedLinks;
            foreach (var (reason, sample) in Skips)
            {
                run.Skipped.Add(reason, sample);
            }
        }
    }

    // One UID's components, reduced: what a batch applies together.
    private sealed record ImportGroup(string? Uid, List<ImportItem> Items, int Components)
    {
        public bool IsSeries => Items.Any(i => i.Kind is ItemKind.Master or ItemKind.Override or ItemKind.CancelledOverride
            || (i.Kind == ItemKind.Cancelled && i.HasRule));

        // Link rows and staged store reservations count toward the entity budget (issue #521 v4 §5).
        public int EstimatedRows => Items.Count + Items.Sum(i => i.Occurrences.Count + (2 * i.Attachments.Count));
    }

    /// <summary>
    /// Imports an <c>.ics</c> file into <paramref name="targetCalendarId"/> (issues #516 §3, #519 §3). The whole file is
    /// validated and regrouped by UID before anything is applied; groups then commit in batches. A failure after a batch
    /// committed is a <see cref="CalendarIcsImportPartiallyAppliedException"/> carrying the original status; re-running
    /// the same file is idempotent and completes it. A cancellation stops at the next batch boundary and is never wrapped.
    /// </summary>
    /// <param name="claims">
    /// The importer's location claims (issue #380 §8.4): places are linked or created only with both
    /// <c>locations.read</c> and <c>locations.create</c>. Null imports every place as detail text.
    /// </param>
    /// <param name="allowRemovals">
    /// Whether the import may remove what a cancelled component matches, and apply a series change that removes a stored
    /// row or discards a stored exception (issue #519 §5.7, the renamed and broadened opt-in).
    /// </param>
    /// <param name="canDelete">
    /// Whether the importer holds <c>calendar.delete</c>, evaluated once from the request principal. With
    /// <paramref name="allowRemovals"/> and without it the whole import is refused before anything is read.
    /// </param>
    /// <param name="defaultTimeZoneId">The zone a floating time is read in (§5.7); absent, floating times stay floating.</param>
    public async Task<IcsImportResult> ImportAsync(
        Guid targetCalendarId, Stream icsFile, long contentLength, string? contentType, string userId,
        CancellationToken cancellationToken = default, LocationClaims? claims = null,
        bool allowRemovals = false, bool canDelete = false, string? defaultTimeZoneId = null,
        CalendarIcsImportClaims? attachmentClaims = null)
    {
        if (!IsAcceptedContentType(contentType))
        {
            throw new DomainValidationException("The uploaded file must be a calendar file (text/calendar).");
        }

        if (allowRemovals && !canDelete)
        {
            throw new DomainForbiddenException("Removing events or discarding edits needs permission to delete calendar events.");
        }

        var defaultZone = CalendarTimeZones.ResolveRequest(defaultTimeZoneId, "defaultTimeZoneId");
        var cap = await limits.GetAsync(cancellationToken);
        var journalCap = await journalLimits.GetAsync(cancellationToken);
        var maxImportBytes = cap.CalendarIcsMaxImportBytes;
        var maxImportEvents = cap.CalendarIcsMaxImportEvents;

        if (contentLength > maxImportBytes)
        {
            throw new DomainValidationException($"The .ics file exceeds the {maxImportBytes / (1024 * 1024)} MB limit.");
        }

        if (!await context.Calendars.AnyAsync(c => c.CalendarId == targetCalendarId, cancellationToken))
        {
            throw new DomainNotFoundException($"Calendar ID {targetCalendarId} not found.");
        }

        // One snapshot of every cap, taken before any component is processed, so a concurrent admin write can never
        // split one import across two values of the same setting.
        var run = new ImportRun
        {
            CalendarId = targetCalendarId,
            UserId = userId,
            Now = timeProvider.GetUtcNow().UtcDateTime,
            Places = await IcsPlaces.ForImportAsync(context, claims ?? LocationClaims.None, userId, timeProvider, cancellationToken),
            Claims = claims ?? LocationClaims.None,
            Skipped = new ImportSkipCollector(cap.ImportMaxSamplesPerSkipReason),
            AllowRemovals = allowRemovals,
            DefaultZone = defaultZone,
            MaxAggregateOccurrences = cap.CalendarIcsMaxAggregateOccurrences,
            MaxEventDurationDays = journalCap.CalendarMaxEventDurationDays,
            MaxGeneratedOccurrences = journalCap.RecurrenceMaxGeneratedOccurrences,
            MaxSamples = cap.ImportMaxSamplesPerSkipReason,
            MaxLinksPerKind = journalCap.CalendarEventMaxLinksPerKind,
            Importer = new CalendarAttachmentImporter(
                context, fileValidation, fileWriter, photoService, userId,
                Math.Min(Math.Min(transportFileCeiling, maxImportBytes),
                    uploadLimits is null ? long.MaxValue : (await uploadLimits.GetAsync(cancellationToken)).MaxUploadBytes),
                SpillDirectory, TempFreeSpace, logger)
            {
                Claims = attachmentClaims ?? CalendarIcsImportClaims.None,
                ReservedBytesCeiling = BatchReservedBytesCeiling,
            },
        };

        // A crashed process's runs and upload copies are never reached by their own finally; the next import sweeps them.
        ImportSpool.SweepStale(SpillDirectory, IcsImportGrouper.SpillFilePrefix + "*");
        await using var upload = await SeekableUpload.OpenAsync(icsFile, maxImportBytes, SpillDirectory, cancellationToken);
        using var reservation = IcsSpillReservation.Reserve(SpillDirectory, upload.Stream.Length);
        await using var grouper = await IcsImportGrouper.BuildAsync(
            upload.Stream, SpillDirectory, maxImportEvents, cancellationToken, GroupingRecordsPerRun, GroupingKey);

        // Decided by KIND, not encoding, on pass 1's file-level flags, before any batch is applied (issue #521 goal 6,
        // SF1, A2). An ATTACH on a component that will turn out to be ignored still counts: pass 1 cannot know the target.
        EnsureAttachmentClaims(grouper.AttachmentKinds, attachmentClaims ?? CalendarIcsImportClaims.None);

        var batch = new List<ImportGroup>(ImportBatchGroups);
        var batchComponents = 0;
        var batchRows = 0;
        long batchChars = 0;
        try
        {
            await foreach (var refs in grouper.GroupsAsync(cancellationToken))
            {
                var (groups, chars) = await ReduceAsync(grouper, refs, run, cancellationToken);
                batchChars += chars;
                batchComponents += refs.Count;
                foreach (var group in groups)
                {
                    batch.Add(group);
                    batchRows += group.EstimatedRows;
                }

                if (batch.Count >= ImportBatchGroups || batchRows >= ImportBatchEntityBudget || batchChars >= ImportBatchCharBudget
                    || run.Importer.BatchReservedBytes >= BatchReservedBytesCeiling)
                {
                    await ApplyBatchAndClearAsync(batch, batchComponents, run, cancellationToken);
                    batch.Clear();
                    batchComponents = 0;
                    batchRows = 0;
                    batchChars = 0;
                }
            }

            if (batchComponents > 0)
            {
                await ApplyBatchAndClearAsync(batch, batchComponents, run, cancellationToken);
            }
        }
        catch (Exception ex) when (run.CommittedBatches > 0 && ex is not OperationCanceledException)
        {
            var status = ex is DomainException domain ? domain.StatusCode : StatusCodes500(ex);
            if (ex is not DomainException)
            {
                logger.LogError(ex,
                    "Calendar .ics import into {CalendarId} by {UserId} failed after {CommittedBatches} committed batch(es).",
                    targetCalendarId, userId, run.CommittedBatches);
            }

            // Groups commit in UID-hash order (AR3), so the committed events are an arbitrary subset of the file.
            var reason = ex is DomainException ? ex.Message : "The import failed part-way.";
            throw new CalendarIcsImportPartiallyAppliedException(
                $"{reason} {run.CommittedEvents} events of the file were already imported; importing the same "
                + "file again is safe and completes it.",
                status, run.CommittedBatches, run.CommittedEvents, ex);
        }

        var totals = run.Totals;
        return new IcsImportResult
        {
            ImportedCount = totals.Imported,
            UpdatedCount = totals.Updated,
            AnySeriesRegenerated = totals.AnySeriesRegenerated,
            PlacesLinked = run.Places.Linked,
            PlacesCreated = run.Places.Created,
            RemovedCount = totals.Removed,
            RemovedSampleTitles = totals.RemovedSamples,
            OverridesApplied = totals.OverridesApplied,
            ExclusionsApplied = totals.ExclusionsApplied,
            DroppedOverrideCount = totals.DroppedOverrides,
            DroppedExclusionCount = totals.DroppedExclusions,
            GatedSeriesCount = totals.Gated,
            AdjustedCount = totals.Adjusted,
            StoredFileCount = run.Importer.StoredFiles,
            ReusedFileCount = run.Importer.ReusedFiles,
            StoredPhotoCount = run.Importer.StoredPhotos,
            ReusedPhotoCount = run.Importer.ReusedPhotos,
            SkippedAttachmentCount = run.SkippedAttachments + totals.SkippedAttachments,
            IgnoredOccurrenceAttachmentCount = run.IgnoredOccurrenceAttachments + totals.IgnoredOccurrenceAttachments,
            ReplacedLinkSetCount = totals.ReplacedLinkSets,
            RemovedLinkCount = totals.RemovedLinks,
            Skipped = run.Skipped.ToGroups((reason, count, samples) => new IcsImportSkipGroup
            {
                Reason = reason,
                Count = count,
                SampleTitles = samples,
            }),
        };
    }

    // A plain component's bytes: 4 Mi characters of at most 4 UTF-8 bytes each, plus its line breaks.
    private const long MaxPlainComponentBytes = (4L * IcsComponentSplitter.MaxComponentChars) + (1024 * 1024);

    /// <summary>Inline attachments this service has decoded, for a test proving a retried batch decodes nothing again.</summary>
    internal int AttachmentsDecoded { get; private set; }

    /// <summary>The staged-reservation ceiling a batch closes at; a test lowers it to split batches at a small scale.</summary>
    internal long BatchReservedBytesCeiling { get; set; } = CalendarAttachmentImporter.BatchReservedBytesCeiling;

    /// <summary>Runs after a batch's phase A and before its first attempt: a test's seam into that window.</summary>
    internal Func<Task>? BeforeBatchApply { get; set; }

    /// <summary>Batches the last import committed, for a test of where they split.</summary>
    internal int LastImportCommittedBatches { get; private set; }

    /// <summary>Free bytes under a directory, for the per-attachment disk check; a test points it elsewhere.</summary>
    internal Func<string, long> TempFreeSpace { get; set; } = DirectoryFreeSpace;

    private static long DirectoryFreeSpace(string directory)
    {
        try
        {
            return new DriveInfo(Path.GetFullPath(directory)).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return long.MaxValue;
        }
    }

    // The goal 6 table: any link needs the kind's read claim, inline content also its create claim.
    private static void EnsureAttachmentClaims(IcsAttachmentKinds kinds, CalendarIcsImportClaims claims)
    {
        var missing = new List<string>();
        if ((kinds.HasFileRef || kinds.HasFileInline) && !claims.FilesRead)
        {
            missing.Add(Odyssey.Dtos.Authorization.PermissionClaims.FilesRead);
        }

        if (kinds.HasFileInline && !claims.FilesCreate)
        {
            missing.Add(Odyssey.Dtos.Authorization.PermissionClaims.FilesCreate);
        }

        if ((kinds.HasPhotoRef || kinds.HasPhotoInline) && !claims.PhotosRead)
        {
            missing.Add(Odyssey.Dtos.Authorization.PermissionClaims.PhotosRead);
        }

        if (kinds.HasPhotoInline && !claims.PhotosCreate)
        {
            missing.Add(Odyssey.Dtos.Authorization.PermissionClaims.PhotosCreate);
        }

        if (missing.Count > 0)
        {
            throw CalendarAttachmentsException.ImportForbidden(missing);
        }
    }

    // A database outage reads as 503, anything else as 500.
    private static int StatusCodes500(Exception ex) =>
        ex is DbUpdateException or System.Data.Common.DbException or TimeoutException ? 503 : 500;

    // Reads, parses and reduces one hash group's components, then splits a residual hash collision by the UID itself.
    private async Task<(List<ImportGroup> Groups, long Chars)> ReduceAsync(
        IcsImportGrouper grouper, IReadOnlyList<ComponentRef> refs, ImportRun run, CancellationToken cancellationToken)
    {
        var items = new List<ImportItem>();
        var overrides = 0;
        long chars = 0;
        foreach (var component in refs)
        {
            if (component.IsOverride && ++overrides > MaxOverridesPerSeries)
            {
                run.Skipped.Add(OverrideCapReason, UntitledEvent);
                continue;
            }

            var text = component.HasInline
                ? await ReadInlineComponentAsync(grouper, component, run, cancellationToken)
                : await ReadPlainComponentAsync(grouper, component, run, cancellationToken);
            chars += text.Length;
            var ev = ParseComponent(text);
            if (Classify(ev, run, component.Offset) is { } item)
            {
                item.Attachments = ReadAttachments(ev!, item, run);
                items.Add(item);
            }

            run.Importer.ComponentSlots.Clear();
        }

        var groups = items
            .GroupBy(i => i.Uid is null ? null : IcsImportGrouper.FoldUid(i.Uid))
            .SelectMany(g => g.Key is null
                ? g.Select(i => new ImportGroup(null, [i], 1))
                : [new ImportGroup(g.First().Uid, g.OrderBy(i => i.Offset).ToList(), g.Count())])
            .ToList();
        return (groups, chars);
    }

    // A component with no inline attachment of its own: read whole, as before, but with more ATTACH lines than an event
    // may link dropped before parsing (§7.4: at most 2 × the cap are parsed). Pass 1 held such a component to the 4 Mi
    // character cap — it skims only a VEVENT's own inline ATTACH — and its length is checked again before the read
    // allocates; an inline value nested in a sub-component is never decoded here (PR #529 review S2).
    private async Task<string> ReadPlainComponentAsync(
        IcsImportGrouper grouper, ComponentRef component, ImportRun run, CancellationToken cancellationToken)
    {
        if (component.Length > MaxPlainComponentBytes)
        {
            throw new DomainValidationException(IcsComponentSplitter.TooLargeMessage);
        }

        var text = await grouper.ReadComponentAsync(component, cancellationToken);
        if (text.Length < 32 || !text.Contains("ATTACH", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        using var reader = new StringReader(text);
        using var spool = await IcsAttachmentSpooler.SpoolAsync(reader, new IcsAttachmentSpooler.Options(
            run.Importer.MaxDecodeBytes, IcsComponentSplitter.MaxComponentChars + (64 * 1024),
            MaxAttachLines: 2 * run.MaxLinksPerKind, SpoolDirectory: SpillDirectory, SkipDecode: _ => true), cancellationToken);
        for (var i = 0; i < spool.DroppedAttachLines; i++)
        {
            run.SkipAttachment(CalendarAttachmentImporter.TooManyLinesReason);
        }

        return spool.CalendarText;
    }

    // Phase A for one component (issue #521 v5 §1): its inline attachments are lifted out one at a time — decoded,
    // resolved, reserved when new, the temp file deleted — and replaced by placeholders in the text that is parsed.
    private async Task<string> ReadInlineComponentAsync(
        IcsImportGrouper grouper, ComponentRef component, ImportRun run, CancellationToken cancellationToken)
    {
        var importer = run.Importer;
        Task<bool>? occurrence = null;
        Func<string>? textSoFar = null;
        void Skip(string reason) => run.SkipAttachment(reason);

        using var reader = new StreamReader(grouper.OpenComponent(component), Encoding.UTF8, false, 1 << 16, leaveOpen: true);
        var options = new IcsAttachmentSpooler.Options(
            importer.MaxDecodeBytes, IcsComponentSplitter.MaxComponentChars + (64 * 1024),
            MaxAttachLines: 2 * run.MaxLinksPerKind, SpoolDirectory: SpillDirectory, TopLevelOnly: true,
            // A RECURRENCE-ID component's attachments are stored nowhere, so they are never decoded (note 4).
            SkipDecode: a => component.IsOverride || importer.CanSkip(a),
            BeforeDecode: importer.EnsureTempSpace,
            OnSpooled: async (attachment, text, token) =>
            {
                AttachmentsDecoded++;
                textSoFar = text;
                importer.ComponentSlots[attachment.Reference] = await importer.ResolveInlineAsync(
                    attachment, () => occurrence ??= IsOccurrenceTargetAsync(textSoFar!(), run.CalendarId, token), Skip, token);
            });
        using var spool = await IcsAttachmentSpooler.SpoolAsync(reader, options, cancellationToken);
        foreach (var attachment in spool.Attachments.Where(a => a.Skimmed && !component.IsOverride))
        {
            importer.ComponentSlots[attachment.Reference] = importer.Skimmed(attachment, Skip);
        }

        for (var i = 0; i < spool.DroppedAttachLines; i++)
        {
            run.SkipAttachment(CalendarAttachmentImporter.TooManyLinesReason);
        }

        return IcsImportGrouper.ComponentEnvelope + spool.CalendarText + "END:VCALENDAR\r\n";
    }

    // Whether the component read so far names a generated occurrence in the target calendar (implementation note 4):
    // read-only, from the UID the text carries before its attachments. A component whose UID comes later is not known
    // here; a reservation made for it is abandoned after the batch commits, or swept.
    private async Task<bool> IsOccurrenceTargetAsync(string text, Guid calendarId, CancellationToken cancellationToken)
    {
        var unfolded = text.Replace("\r\n ", string.Empty, StringComparison.Ordinal).Replace("\r\n\t", string.Empty, StringComparison.Ordinal);
        string? uid = null;
        foreach (var line in unfolded.Split("\r\n"))
        {
            if (line.StartsWith("RRULE", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (uid is null && (line.StartsWith("UID:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("UID;", StringComparison.OrdinalIgnoreCase)))
            {
                uid = line[(line.IndexOf(':') + 1)..].Trim();
            }
        }

        if (string.IsNullOrEmpty(uid) || uid.Length > MaxUidLength)
        {
            return false;
        }

        var resolver = await ResolveAsync(calendarId, [uid], tracked: false, cancellationToken);
        return MatchUntracked(uid, resolver.Events)?.RecurrencePatternId is not null;
    }

    // Match's rules for rows this context does not track.
    private static ContextCalendarEvent? MatchUntracked(string uid, List<ContextCalendarEvent> candidates)
    {
        if (IsSyntheticUid(uid) && Guid.TryParse(uid[..^SyntheticUidSuffix.Length], out var key)
            && candidates.FirstOrDefault(c => c.CalendarEventId == key) is { } byKey)
        {
            return byKey;
        }

        return candidates.FirstOrDefault(c => string.Equals(c.ExternalUid, uid, StringComparison.Ordinal))
            ?? candidates.FirstOrDefault(c => c.ExternalUid is { } stored && UidComparer.Equals(stored, uid))
            ?? (IsSyntheticUid(uid) ? candidates.FirstOrDefault(c => c.ImportSourceUid is { } s && UidComparer.Equals(s, uid)) : null);
    }

    private const string SpoolScheme = IcsAttachmentSpool.Scheme + ":";

    // A component's ATTACH lines as slots (§8.3, v3 §A.1): a placeholder is phase A's slot, an odyssey-file: or
    // odyssey-photo: URI a reference, any other URI kindless (counted, no claim, no replace). On a modified occurrence
    // every kinded one is ignored and counted.
    private static List<CalendarAttachSlot> ReadAttachments(IcalEvent ev, ImportItem item, ImportRun run)
    {
        var slots = new List<CalendarAttachSlot>();
        if (item.Kind is ItemKind.Cancelled or ItemKind.CancelledOverride || ev.Attachments.Count == 0)
        {
            return slots;
        }

        void Skip(string reason) => run.SkipAttachment(reason);
        foreach (var attachment in ev.Attachments)
        {
            if (attachment.Uri is not { } uri)
            {
                continue;
            }

            var raw = uri.OriginalString.Trim();
            CalendarAttachSlot? slot = null;
            if (raw.StartsWith(SpoolScheme, StringComparison.OrdinalIgnoreCase))
            {
                slot = run.Importer.ComponentSlots.GetValueOrDefault(raw[SpoolScheme.Length..]);
                if (slot is null && item.Kind == ItemKind.Override)
                {
                    run.IgnoredOccurrenceAttachments++;
                    continue;
                }
            }
            else if (raw.StartsWith(CalendarExportAttachments.PhotoUriScheme + ":", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith(ImportLimits.FileUriScheme + ":", StringComparison.OrdinalIgnoreCase))
            {
                var photo = raw.StartsWith(CalendarExportAttachments.PhotoUriScheme + ":", StringComparison.OrdinalIgnoreCase);
                if (item.Kind == ItemKind.Override)
                {
                    run.IgnoredOccurrenceAttachments++;
                    continue;
                }

                slot = CalendarAttachmentImporter.Reference(
                    photo ? CalendarLinkKind.Photo : CalendarLinkKind.File, raw[(raw.IndexOf(':') + 1)..],
                    attachment.Parameters.Get("X-ODYSSEY-SHA256"), attachment.Parameters.Get("SIZE"), Skip);
            }
            else
            {
                run.SkipAttachment(CalendarAttachmentImporter.UriReason);
                continue;
            }

            // A skipped slot is already counted, and a skip never replaces anything (v3 §A.2).
            if (slot is null || slot.State == CalendarAttachSlotState.Skipped)
            {
                continue;
            }

            if (item.Kind == ItemKind.Override)
            {
                run.IgnoredOccurrenceAttachments++;
                continue;
            }

            slots.Add(slot);
        }

        return slots;
    }

    // A component Ical.Net cannot read is that event's problem, not the file's: it is skipped (null here).
    private static IcalEvent? ParseComponent(string component)
    {
        try
        {
            return IcalCalendar.Load(component)?.Events.FirstOrDefault();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    // The tracker is cleared whether the batch committed or not, so a failed batch's half-applied entities never
    // outlive it (architect nit 9).
    private async Task ApplyBatchAndClearAsync(List<ImportGroup> groups, int components, ImportRun run, CancellationToken cancellationToken)
    {
        try
        {
            await ApplyBatchAsync(groups, components, run, cancellationToken);
        }
        finally
        {
            context.ChangeTracker.Clear();
        }
    }

    // The ids one batch needs locked, by what its UIDs resolve to.
    private sealed record BatchLocks(IReadOnlySet<Guid> PatternIds, IReadOnlySet<Guid> EventIds)
    {
        public bool SameAs(BatchLocks other) => PatternIds.SetEquals(other.PatternIds) && EventIds.SetEquals(other.EventIds);
    }

    // A DomainException only so the hierarchy guard admits it: it never leaves the batch loop, which retries on it and
    // turns the last one into 409 calendar-series-busy.
    private sealed class ReResolveNeeded(BatchLocks locks) : DomainException("The batch's rows changed before they were locked.")
    {
        public BatchLocks Locks { get; } = locks;

        public override int StatusCode => 409;
    }

    // components counts every VEVENT the batch spans, skipped ones included, for the partial-failure counts.
    private async Task ApplyBatchAsync(List<ImportGroup> groups, int components, ImportRun run, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uids = groups.Select(g => g.Uid).OfType<string>().Distinct(UidComparer).ToList();

        // Resolve outside the transaction, only to learn what to lock (#519 v4 §2 step 1).
        var locks = LocksOf(await ResolveAsync(run.CalendarId, uids, tracked: false, cancellationToken));

        // The batch's references, read-only and outside the locks: one IN query per kind (issue #521 §7.4).
        await run.Importer.ResolveReferencesAsync(
            groups.SelectMany(g => g.Items).SelectMany(i => i.Attachments).ToList(), run.SkipAttachment, cancellationToken);
        if (BeforeBatchApply is { } seam)
        {
            await seam();
        }

        // Every run of the batch's work - a re-resolve below, or the execution strategy retrying a deadlock or a lock wait
        // (PR #528 review A1) - starts from the state the batch began with: a rolled-back attempt may have created places
        // the index still names and counted them, so the index is reloaded and the counts put back. The tally is per run.
        var placesSnapshot = run.Places.Snapshot();
        var runs = 0;
        async Task BeforeRunAsync()
        {
            if (++runs > 1)
            {
                run.Places = await IcsPlaces.ForImportAsync(context, run.Claims, run.UserId, timeProvider, cancellationToken);
                run.Places.Restore(placesSnapshot);
            }
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var tally = await RunBatchAttemptAsync(groups, uids, locks, run, BeforeRunAsync, cancellationToken);
                tally.MergeInto(run);
                await run.Importer.CommitBatchAsync(tally.Links, cancellationToken);
                run.CommittedBatches++;
                LastImportCommittedBatches = run.CommittedBatches;
                run.CommittedEvents += components;
                logger.LogInformation(
                    "Calendar .ics import into {CalendarId}: batch committed ({Batch}), {Groups} group(s), {Components} component(s).",
                    run.CalendarId, run.CommittedBatches, groups.Count, components);
                AuditBatch(run, tally);
                return;
            }
            catch (ReResolveNeeded changed) when (attempt < MaxBatchAttempts)
            {
                locks = changed.Locks;
            }
            catch (ReResolveNeeded)
            {
                throw CalendarSeriesConflictException.SeriesBusy();
            }
        }
    }

    // One attempt: inside the execution strategy, the locks first (series, then standalone events, each ascending), then
    // the re-resolve under them, then the groups, then one save (#519 v4 §2, v5 §2). The strategy is the interactive one
    // (two retries of a 5 s statement wait), so one batch's worst case stays near 15 s, as an interactive write's does.
    private async Task<ImportTally> RunBatchAttemptAsync(
        List<ImportGroup> groups, List<string> uids, BatchLocks locks, ImportRun run, Func<Task> beforeRun,
        CancellationToken cancellationToken)
    {
        async Task<ImportTally> Work()
        {
            await beforeRun();
            await CalendarSeriesTransactions.LockSeriesAsync(context, locks.PatternIds, cancellationToken);
            await CalendarSeriesTransactions.LockEventsAsync(context, locks.EventIds, cancellationToken);
            var resolver = await ResolveAsync(run.CalendarId, uids, tracked: true, cancellationToken);
            var underLock = LocksOf(resolver);
            if (!underLock.SameAs(locks))
            {
                throw new ReResolveNeeded(underLock);
            }

            var tally = new ImportTally();
            var attempt = new BatchAttempt(resolver, tally);
            foreach (var group in groups)
            {
                await ApplyGroupAsync(group, run, attempt, cancellationToken);
            }

            try
            {
                await run.Places.SaveChangesAsync(context, cancellationToken);
            }
            catch (DbUpdateException ex) when (ReservationExpired(ex))
            {
                // The abandoned-write sweep removed a reservation older than an hour (implementation note 5): the
                // partial-failure path, never the busy 409.
                throw new FileStorageUnavailableException(CalendarAttachmentImporter.ExpiredReservationMessage);
            }

            return tally;
        }

        if (!context.Database.IsRelational())
        {
            return await Work();
        }

        var strategy = OdysseyDatabaseRetry.InteractiveStrategy(context);
        try
        {
            return await strategy.ExecuteAsync(() => CalendarSeriesTransactions.RunAttemptAsync(context, Work, cancellationToken));
        }
        catch (Exception ex) when (ex is not ReResolveNeeded && CalendarSeriesTransactions.IsBusy(ex))
        {
            throw CalendarSeriesConflictException.SeriesBusy();
        }
    }

    // A reservation the sweep already removed surfaces two ways, whichever statement reaches it first: the referencing
    // row's RESTRICT key to StoredObjects (1452), or the attach's State-token UPDATE matching no row.
    private static bool ReservationExpired(DbUpdateException exception) =>
        exception is DbUpdateConcurrencyException concurrency
            ? concurrency.Entries.Any(e => e.Entity is StoredObject)
            : exception.InnerException is MySqlConnector.MySqlException { Number: 1452 } mysql
                && mysql.Message.Contains("REFERENCES `StoredObjects`", StringComparison.Ordinal);

    private static BatchLocks LocksOf(BatchResolver resolver) => new(
        resolver.Patterns.Select(p => p.RecurrencePatternId)
            .Concat(resolver.Events.Where(e => e.RecurrencePatternId is not null).Select(e => e.RecurrencePatternId!.Value))
            .ToHashSet(),
        resolver.Events.Select(e => e.CalendarEventId).ToHashSet());

    // After each committed batch that removed or discarded anything (§7.7): ids, keys and counts only, never a title or
    // place. The ids are split across lines of at most AuditIdsPerLine, because one removed series can carry thousands
    // of future rows.
    private void AuditBatch(ImportRun run, ImportTally tally)
    {
        var removedCount = tally.RemovedEventIds.Count + tally.RemovedSeriesIds.Count;
        var parts = AuditChunks(tally.RemovedSeriesIds, tally.RemovedEventIds, AuditIdsPerLine);
        for (var part = 0; part < parts.Count; part++)
        {
            logger.LogInformation(
                "Calendar .ics import by {UserId} into calendar {CalendarId} removed {RemovedCount} item(s), "
                + "part {Part} of {Parts}: series {RemovedSeriesIds}; events {RemovedEventIds}.",
                run.UserId, run.CalendarId, removedCount, part + 1, parts.Count, parts[part].SeriesIds, parts[part].EventIds);
        }

        if (tally.DroppedOverrideKeys.Count + tally.DroppedExclusionKeys.Count > 0)
        {
            logger.LogInformation(
                "Calendar .ics import by {UserId} into calendar {CalendarId} discarded {DroppedOverrideCount} override(s) "
                + "and {DroppedExclusionCount} exclusion(s): override keys {DroppedOverrideKeys}; exclusion keys {DroppedExclusionKeys}.",
                run.UserId, run.CalendarId, tally.DroppedOverrideKeys.Count, tally.DroppedExclusionKeys.Count,
                tally.DroppedOverrideKeys.Take(AuditIdsPerLine).Select(k => k.ToString("s")),
                tally.DroppedExclusionKeys.Take(AuditIdsPerLine).Select(k => k.ToString("s")));
        }
    }

    /// <summary>Splits a batch's removed ids into audit lines of at most <paramref name="perLine"/> ids, series first.</summary>
    internal static List<(List<Guid> SeriesIds, List<Guid> EventIds)> AuditChunks(
        IReadOnlyList<Guid> seriesIds, IReadOnlyList<Guid> eventIds, int perLine)
    {
        var lines = new List<(List<Guid> SeriesIds, List<Guid> EventIds)>();
        var all = seriesIds.Select(id => (Series: true, Id: id)).Concat(eventIds.Select(id => (Series: false, Id: id)));
        foreach (var chunk in all.Chunk(perLine))
        {
            lines.Add((chunk.Where(c => c.Series).Select(c => c.Id).ToList(), chunk.Where(c => !c.Series).Select(c => c.Id).ToList()));
        }

        return lines;
    }
}
