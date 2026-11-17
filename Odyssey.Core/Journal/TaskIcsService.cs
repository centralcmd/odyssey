using Odyssey.Core;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Odyssey.Core.FileStorage;
using Odyssey.Core.FileStorage.Files;
using Odyssey.Core.Finance;
using Odyssey.Context;
using Odyssey.Dtos.Journal;
using Odyssey.Core.Journal.Interop;
using Ical.Net.Serialization;
using IcalCalendar = Ical.Net.Calendar;
using IcalTodo = Ical.Net.CalendarComponents.Todo;
using CalDateTime = Ical.Net.DataTypes.CalDateTime;
using Attachment = Ical.Net.DataTypes.Attachment;
using FileMetadata = Odyssey.Context.FileMetadata;

namespace Odyssey.Core.Journal;

/// <summary>
/// VTODO/.ics (RFC 5545 §3.6.2) import/export for the shared to-do board (issue #337). Structurally
/// parallel to <c>CalendarIcsService</c>: export emits one <c>VTODO</c> per task in the caller's
/// current filter view; import parses <c>parsed.Todos</c>, matches each by <c>UID</c> against
/// <see cref="JournalTask.ExternalUid"/> (update in place) or creates a new row, and aggregates
/// per-component problems into a skip summary rather than failing the whole file. Reuses the module's
/// existing filter, status-derivation and status-timestamp logic from <see cref="JournalTaskService"/>.
/// </summary>
public class TaskIcsService
{
    private const int MaxTitleLength = ImportLimits.MaxTitleLength;
    private const int MaxContentLength = ImportLimits.MaxContentLength;
    private const string ProductId = "-//Odyssey//Tasks//EN";
    private const string FileUriScheme = ImportLimits.FileUriScheme;
    private const string UntitledTask = "(untitled)";

    /// <summary>
    /// Reported when a task carries more links of one kind than the cap allows (issue #434 §9-A). The
    /// number is interpolated because the cap is admin-editable — a literal would go stale the moment
    /// it changed. This path already counted its capped links numerically; the named reason is new, so
    /// both import summaries now describe the same thing the same way.
    /// </summary>
    internal static string LinksCappedReason(int maxLinksPerKind) => ImportLinks.LinksCappedReason("task", maxLinksPerKind);

    private readonly OdysseyContext context;
    private readonly IFileLookup files;
    private readonly IImportExportLimitsLookup limits;
    private readonly IJournalLimitsLookup journalLimits;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<TaskIcsService> logger;
    private readonly StoredFileContentSource? fileContent;
    private readonly StoredFileWriter? fileWriter;
    private readonly FileValidationService fileValidation;
    private readonly long transportFileCeiling;

    // journalLimits is a SECOND lookup, newly injected in issue #434 to fix defect A — see the same
    // note on JournalEntryIcsService: this service enforced a hardcoded 50 while the administrator's
    // JournalTaskMaxLinksPerKind setting was honoured on the create/update path and ignored here.
    // The file-store collaborators are optional so a unit test without a store still builds one; without them an
    // attached file exports as a bare odyssey-file: reference and an inline one cannot be imported.
    public TaskIcsService(
        OdysseyContext context, IFileLookup files, IImportExportLimitsLookup limits,
        IJournalLimitsLookup journalLimits, ILogger<TaskIcsService> logger, TimeProvider? timeProvider = null,
        StoredFileContentSource? fileContent = null, StoredFileWriter? fileWriter = null,
        FileValidationService? fileValidation = null, IOptions<FileStorageOptions>? fileStorageOptions = null)
    {
        this.context = context;
        this.files = files;
        this.limits = limits;
        this.journalLimits = journalLimits;
        this.logger = logger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.fileContent = fileContent;
        this.fileWriter = fileWriter;
        this.fileValidation = fileValidation ?? new FileValidationService();
        transportFileCeiling = (fileStorageOptions?.Value ?? new FileStorageOptions()).MaxFileSizeBytes;
    }

    // ---------------------------------------------------------------- Export

    /// <summary>Thrown once an export has started streaming and the next task would cross the byte cap. The
    /// response is already committed, so the caller aborts the connection: a browser then reports the download as
    /// failed, instead of saving a file that silently stops early.</summary>
    public sealed class ExportSizeExceededException(string message) : Exception(message);

    private sealed record ExportFile(Guid FileId, string Head, long EncodedBytes, long SizeBytes);

    private sealed record ExportPlan(
        IQueryable<JournalTask> BaseQuery, List<Guid> OrderedIds, IReadOnlyDictionary<Guid, ExportFile> Files,
        int AttachmentCount, long AttachmentBytes, long MaxBytes);

    private IQueryable<FileMetadata> VisibleFiles => context.FileMetadata.Where(FileReferenceCounter.VisibleInFilesStore(context));

    /// <summary>
    /// What <see cref="ExportStreamingAsync"/> would produce for <paramref name="query"/>, refusing exactly where it
    /// would: over the task cap, over the byte cap (judged from the attached files' stored sizes before any byte is
    /// encoded), or — with <paramref name="canReadFiles"/> false — when any exported task carries an attached file.
    /// </summary>
    public async Task<TaskIcsExportSummary> SummarizeExportAsync(
        JournalTasksQueryParams query, bool canReadFiles, CancellationToken cancellationToken = default)
    {
        var plan = await PrepareExportAsync(query, canReadFiles, cancellationToken);
        return new TaskIcsExportSummary
        {
            TaskCount = plan.OrderedIds.Count,
            AttachmentCount = plan.AttachmentCount,
            AttachmentBytes = plan.AttachmentBytes,
        };
    }

    private async Task<ExportPlan> PrepareExportAsync(
        JournalTasksQueryParams query, bool canReadFiles, CancellationToken cancellationToken)
    {
        var baseQuery = JournalTaskService
            .ApplyFilters(context.JournalTasks.AsNoTracking(), query.Search, query.TagIds, query.Statuses);

        // Optional id restriction (issue #337): the row-menu "Export as iCalendar" action exports a single
        // task by id. Applied on top of the other filters (which the row menu pairs with all statuses so an
        // archived task still exports).
        if (query.Ids is { Length: > 0 } ids)
        {
            baseQuery = baseQuery.Where(i => ids.Contains(i.JournalTaskId));
        }

        var orderedIdsQuery = baseQuery
            .OrderBy(i => i.Position).ThenBy(i => i.JournalTaskId)
            .Select(i => i.JournalTaskId);

        var effectiveLimits = await limits.GetAsync(cancellationToken);
        var cap = effectiveLimits.TaskIcsMaxExportTasks;
        var maxExportBytes = effectiveLimits.TaskIcsMaxExportBytes;

        // No explicit transaction: OdysseyContext enables EnableRetryOnFailure() in production, which
        // forbids a bare Database.BeginTransactionAsync unless the ENTIRE unit of work runs inside one
        // CreateExecutionStrategy().ExecuteAsync call (verified against real MariaDB) — that doesn't
        // compose with a chunked read that yields output as it goes. Instead, the ordered
        // JournalTaskId set is captured in one cheap up-front read (bounded to max+1 when a cap is
        // configured), and each chunk is fetched independently by a fixed id batch — see
        // ExportChunking.ReorderToSnapshot for the consistency trade-off this makes relative to the
        // RepeatableRead snapshot this replaced (PR #403 review fix).
        List<Guid> orderedIds;
        if (cap is { } max)
        {
            orderedIds = await orderedIdsQuery.Take(max + 1).ToListAsync(cancellationToken);
            if (orderedIds.Count > max)
            {
                throw new DomainValidationException(
                    $"The filtered export would exceed {max} tasks — narrow the search, tag, or status filters.");
            }
        }
        else
        {
            orderedIds = await orderedIdsQuery.ToListAsync(cancellationToken);
        }

        // Attached files travel inline. Only a file the Files store shows is exported with its content — a photo's
        // own original is not readable under files.read — and one that no longer resolves keeps its bare reference.
        var linkedFileIds = await baseQuery.SelectMany(t => t.Attachments).Select(a => a.FileId).ToListAsync(cancellationToken);
        var exportFiles = new Dictionary<Guid, ExportFile>();
        if (linkedFileIds.Count > 0 && fileContent is not null)
        {
            var distinct = linkedFileIds.Distinct().ToList();
            var rows = await VisibleFiles.AsNoTracking()
                .Where(f => distinct.Contains(f.Id))
                .Select(f => new { f.Id, f.FileName, f.ContentType, f.Sha256Hash, f.SizeBytes })
                .ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                var head = IcsBinaryAttachmentWriter.BuildHead(
                    new IcsBinaryAttachmentHeader(row.Id, row.FileName, row.ContentType, row.Sha256Hash, row.SizeBytes));
                exportFiles[row.Id] = new ExportFile(row.Id, head, IcsBinaryAttachmentWriter.EncodedLength(head, row.SizeBytes), row.SizeBytes);
            }
        }

        var inlineLinks = linkedFileIds.Where(exportFiles.ContainsKey).ToList();
        if (inlineLinks.Count > 0 && !canReadFiles)
        {
            throw new DomainForbiddenException(
                $"These tasks have {inlineLinks.Count} attached {(inlineLinks.Count == 1 ? "file" : "files")}, and exporting "
                + "attached files needs permission to read files.");
        }

        var estimatedBytes = inlineLinks.Sum(id => exportFiles[id].EncodedBytes);
        if (estimatedBytes > maxExportBytes)
        {
            throw new DomainValidationException(
                $"The export would be at least {estimatedBytes / (1024 * 1024)} MB with its attached files, over the "
                + $"{maxExportBytes / (1024 * 1024)} MB export limit — narrow the search, tag, or status filters.");
        }

        return new ExportPlan(
            baseQuery, orderedIds, exportFiles, inlineLinks.Count, inlineLinks.Sum(id => exportFiles[id].SizeBytes), maxExportBytes);
    }

    /// <summary>
    /// Streams the tasks matching <paramref name="query"/>'s search/tag/status filters directly to
    /// <paramref name="output"/> as a VTODO <c>.ics</c> document, in row-keyset chunks (issue #343 §5
    /// Goal 8), bounded by the configured <c>TaskIcsMaxExportTasks</c> cap. Each attached file travels inline as a
    /// base64 <c>ATTACH</c>, streamed from the file store and never held in memory whole.
    /// <paramref name="onReady"/> is called exactly once, with the file name and row count, after the pre-fetch count
    /// is known but before any byte is written — the caller's only chance to set response headers (including
    /// <c>X-Odyssey-Export-Rows</c>).
    /// <para>
    /// The configured <c>TaskIcsMaxExportMegabytes</c> cap is checked twice: up front against the attached files'
    /// encoded size, which is known exactly from their stored sizes, and again before each task is written. A task
    /// that would cross it throws <see cref="ExportSizeExceededException"/>; by then the response has started, so the
    /// caller aborts the connection rather than delivering a file that stops early.
    /// </para>
    /// </summary>
    public async Task ExportStreamingAsync(
        JournalTasksQueryParams query, bool canReadFiles, Stream output, Action<string, int> onReady,
        CancellationToken cancellationToken = default)
    {
        var plan = await PrepareExportAsync(query, canReadFiles, cancellationToken);
        var count = plan.OrderedIds.Count;

        var exportDate = timeProvider.GetUtcNow().UtcDateTime;
        onReady($"odyssey-tasks-{exportDate:yyyyMMdd-HHmmss}Z.ics", count);

        var (head, tail) = IcsChunkSerializer.BuildEnvelope(ProductId);
        await IcsChunkSerializer.WriteAsync(output, head, cancellationToken);
        var writtenBytes = (long)Encoding.UTF8.GetByteCount(head) + Encoding.UTF8.GetByteCount(tail);

        var written = 0;
        foreach (var idBatch in plan.OrderedIds.Chunk(ExportChunking.ChunkSize))
        {
            var rows = await plan.BaseQuery
                .Where(i => idBatch.Contains(i.JournalTaskId))
                .Include(i => i.ItemTags)
                .Include(i => i.Attachments)
                .ToListAsync(cancellationToken);
            var tasks = ExportChunking.ReorderToSnapshot(idBatch, rows, i => i.JournalTaskId);
            if (tasks.Count == 0)
            {
                continue; // every id in this batch was deleted between the snapshot and this fetch
            }

            var referencedTagIds = tasks.SelectMany(t => t.ItemTags.Select(l => l.JournalTaskTagId)).Distinct().ToList();
            var tagNames = referencedTagIds.Count == 0
                ? new Dictionary<Guid, string>()
                : await context.JournalTaskTags
                    .Where(t => referencedTagIds.Contains(t.JournalTaskTagId))
                    .ToDictionaryAsync(t => t.JournalTaskTagId, t => t.Name, cancellationToken);

            foreach (var task in tasks)
            {
                var inline = task.Attachments
                    .OrderBy(a => a.CreatedAt)
                    .Select(a => plan.Files.GetValueOrDefault(a.FileId))
                    .OfType<ExportFile>()
                    .ToList();

                var calendar = new IcalCalendar();
                calendar.Todos.Add(BuildVTodo(task, tagNames, plan.Files));
                var text = IcsChunkSerializer.SerializeComponents(calendar);
                var split = text.LastIndexOf("END:VTODO", StringComparison.Ordinal);
                var (prefix, suffix) = split < 0 ? (text, string.Empty) : (text[..split], text[split..]);

                var taskBytes = Encoding.UTF8.GetByteCount(text) + inline.Sum(f => f.EncodedBytes);
                if (writtenBytes + taskBytes > plan.MaxBytes)
                {
                    logger.LogWarning(
                        "Tasks .ics export stopped at {WrittenBytes} bytes (cap {MaxBytes}); {WrittenRows}/{TotalRows} tasks delivered.",
                        writtenBytes, plan.MaxBytes, written, count);
                    throw new ExportSizeExceededException(
                        $"The export grew past the {plan.MaxBytes / (1024 * 1024)} MB export limit while it was being written.");
                }

                await IcsChunkSerializer.WriteAsync(output, prefix, cancellationToken);
                foreach (var file in inline)
                {
                    await WriteInlineAttachmentAsync(output, file, cancellationToken);
                }

                await IcsChunkSerializer.WriteAsync(output, suffix, cancellationToken);
                writtenBytes += taskBytes;
                written++;
            }
        }

        await IcsChunkSerializer.WriteAsync(output, tail, cancellationToken);
    }

    private async Task WriteInlineAttachmentAsync(Stream output, ExportFile file, CancellationToken cancellationToken)
    {
        var content = await fileContent!.OpenAsync(VisibleFiles.Where(f => f.Id == file.FileId), cancellationToken);
        if (content is null)
        {
            // Removed since the plan was made: keep the reference rather than drop the link silently.
            await IcsChunkSerializer.WriteAsync(output, $"ATTACH:{FileUriScheme}:{file.FileId}\r\n", cancellationToken);
            return;
        }

        await using var stream = content.Content;
        await IcsBinaryAttachmentWriter.WriteAsync(output, file.Head, stream, cancellationToken);
    }

    private static IcalTodo BuildVTodo(
        JournalTask task, IReadOnlyDictionary<Guid, string> tagNames, IReadOnlyDictionary<Guid, ExportFile> inlineFiles)
    {
        var status = JournalTaskService.DeriveStatus(task.Archived, task.CompletedAt, task.StartedAt);
        var todo = new IcalTodo
        {
            Uid = task.ExternalUid,
            Summary = task.Title,
            Description = task.Content,
            Status = ToVTodoStatus(status),
            // Non-authoritative interop courtesy (§2 Non-Goal 7): 100 for Done, else 0. Never read back.
            PercentComplete = status is JournalTaskStatus.Done ? 100 : 0,
        };

        if (task.StartedAt is { } startedAt)
        {
            todo.Start = ToUtcCalDateTime(startedAt);
        }

        if (task.Deadline is { } deadline)
        {
            // RFC 5545 §3.8.2.3: DUE's value type MUST match DTSTART's. Odyssey deadlines carry no time
            // of day, so DUE is a VALUE=DATE — except when DTSTART (a DATE-TIME, from StartedAt) is also
            // emitted, in which case DUE is written as a DATE-TIME (midnight UTC) to keep the pair valid.
            todo.Due = task.StartedAt is null
                ? new CalDateTime(deadline.Year, deadline.Month, deadline.Day)
                : ToUtcCalDateTime(new DateTime(deadline.Year, deadline.Month, deadline.Day, 0, 0, 0, DateTimeKind.Utc));
        }

        if (task.CompletedAt is { } completedAt)
        {
            todo.Completed = ToUtcCalDateTime(completedAt);
        }

        foreach (var link in task.ItemTags)
        {
            if (tagNames.TryGetValue(link.JournalTaskTagId, out var name))
            {
                todo.Categories.Add(name);
            }
        }

        // An attached file whose content travels inline is written by the caller; only one that cannot keeps the
        // bare odyssey-file: reference, which a re-import into the same instance can still resolve.
        foreach (var attachment in task.Attachments.Where(a => !inlineFiles.ContainsKey(a.FileId)).OrderBy(a => a.CreatedAt))
        {
            todo.Attachments.Add(new Attachment { Uri = new Uri($"{FileUriScheme}:{attachment.FileId}") });
        }

        return todo;
    }

    private static string ToVTodoStatus(JournalTaskStatus status) => status switch
    {
        JournalTaskStatus.Doing => "IN-PROCESS",
        JournalTaskStatus.Done => "COMPLETED",
        JournalTaskStatus.Archived => "CANCELLED",
        _ => "NEEDS-ACTION",
    };

    private static CalDateTime ToUtcCalDateTime(DateTime utc) =>
        new(DateTime.SpecifyKind(utc, DateTimeKind.Utc), hasTime: true);

    // ---------------------------------------------------------------- Import

    /// <summary>Upper bound on the calendar's own text once inline files are lifted out — what <c>Ical.Net</c> parses in
    /// memory. Attached files are bounded by the byte cap and disk, never by this.</summary>
    internal const long MaxCalendarTextChars = 256L * 1024 * 1024;

    /// <summary>
    /// Imports a VTODO <c>.ics</c>. Inline (base64) attachments are spooled to disk by a streaming pre-pass, then each
    /// is matched to a stored file — first the file it was exported from (same id and same SHA-256), then any visible
    /// file with the same SHA-256 and size — and stored as a new Journal file only when neither matches.
    /// <paramref name="canLinkFiles"/> is <c>files.read</c>, <paramref name="canCreateFiles"/> <c>files.create</c>; a file
    /// carrying inline attachments needs both, and is refused whole (403) without them.
    /// </summary>
    public async Task<TaskIcsImportResult> ImportAsync(
        Stream icsFile, long contentLength, string? contentType, string userId, bool canLinkFiles, bool canCreateFiles,
        CancellationToken cancellationToken = default)
    {
        if (!IsAcceptedContentType(contentType))
        {
            throw new DomainValidationException("The uploaded file must be a calendar file (text/calendar).");
        }

        var cap = await limits.GetAsync(cancellationToken);
        var maxImportBytes = cap.TaskIcsMaxImportBytes;
        var maxVTodos = cap.TaskIcsMaxImportTasks ?? int.MaxValue;

        if (contentLength > maxImportBytes)
        {
            throw new DomainValidationException($"The .ics file exceeds the {maxImportBytes / (1024 * 1024)} MB limit.");
        }

        // One inline file can be no larger than the whole import, nor than what the file store accepts at all.
        var maxFileBytes = Math.Min(maxImportBytes, transportFileCeiling);
        using var reader = ImportFileReader.OpenBoundedTextReader(icsFile, maxImportBytes, ".ics");
        using var spool = await IcsAttachmentSpooler.SpoolAsync(
            reader, maxFileBytes, Math.Min(maxImportBytes, MaxCalendarTextChars), cancellationToken);

        if (spool.Count > 0 && !(canLinkFiles && canCreateFiles))
        {
            throw new DomainForbiddenException(
                $"This file carries {spool.Count} attached {(spool.Count == 1 ? "file" : "files")}, and importing attached "
                + "files needs permission to read and upload files.");
        }

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

        var todos = parsed.Todos;
        if (todos.Count > maxVTodos)
        {
            throw new DomainValidationException($"The file contains more than {maxVTodos} to-do items (VTODO).");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Match targets by UID → ExternalUid. Only load the tasks whose UID appears in this file.
        var incomingUids = todos
            .Select(t => Normalize(t.Uid))
            .Where(u => u is not null)
            .Select(u => u!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var existing = incomingUids.Count == 0
            ? new List<JournalTask>()
            : await context.JournalTasks
                .Include(i => i.ItemTags)
                .Include(i => i.Attachments)
                .Where(i => incomingUids.Contains(i.ExternalUid))
                .ToListAsync(cancellationToken);
        var byUid = existing.ToDictionary(i => i.ExternalUid, StringComparer.Ordinal);
        var createdByUid = new Dictionary<string, JournalTask>(StringComparer.Ordinal);

        // Every tag, archived ones included, indexed by case-insensitive exact name: the export writes an archived
        // tag's name too, so matching only active tags made a re-import strip it. On a duplicate name the first wins,
        // and an active tag is offered before an archived one.
        var tagRows = await context.JournalTaskTags
            .Select(t => new { t.JournalTaskTagId, t.Name, t.Archived })
            .ToListAsync(cancellationToken);
        var tagsByName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tagRows.OrderBy(t => t.Archived is null ? 0 : 1).ThenBy(t => t.JournalTaskTagId))
        {
            tagsByName.TryAdd(tag.Name, tag.JournalTaskTagId);
        }

        // Validate all candidate odyssey-file attachment ids once (only if the caller may link files).
        var existingFileIds = await ResolveLinkableFileIds(todos, canLinkFiles, cancellationToken);

        var nextPosition = new Dictionary<JournalTaskStatus, int>();
        // One snapshot of each record for the whole import, so a concurrent admin write cannot split
        // one file across two values of the same setting.
        var importLimits = await limits.GetAsync(cancellationToken);
        var maxLinksPerKind = (await journalLimits.GetAsync(cancellationToken)).JournalTaskMaxLinksPerKind;
        var skipped = new ImportSkipCollector(importLimits.ImportMaxSamplesPerSkipReason);
        var inlineFiles = new InlineFileResolver(this, userId, maxFileBytes, skipped);
        var imported = 0;
        var updated = 0;
        var skippedTagLinks = 0;
        var skippedAttachments = 0;

        foreach (var todo in todos)
        {
            var title = todo.Summary?.Trim();
            var sample = string.IsNullOrWhiteSpace(title) ? UntitledTask : title;

            if (todo.RecurrenceRule is not null)
            {
                skipped.Add("Recurring VTODO not supported", sample);
                continue;
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                skipped.Add("Task is missing a title (SUMMARY).", sample);
                continue;
            }

            if (title.Length > MaxTitleLength)
            {
                skipped.Add($"Title exceeds the maximum length of {MaxTitleLength} characters.", sample);
                continue;
            }

            var content = todo.Description;
            if (content is { Length: > MaxContentLength })
            {
                skipped.Add($"Description exceeds the maximum length of {MaxContentLength} characters.", sample);
                continue;
            }

            var status = FromVTodoStatus(todo.Status);
            var deadline = ToDeadline(todo.Due);
            var uid = Normalize(todo.Uid);

            JournalTask target;
            bool isNew;
            if (uid is not null && byUid.TryGetValue(uid, out var existingRow))
            {
                target = existingRow;
                isNew = false;
            }
            else if (uid is not null && createdByUid.TryGetValue(uid, out var createdRow))
            {
                // Same UID appearing twice in one file: last-write-wins onto the row the first occurrence
                // created (§5 pipeline step 4.3) — not a collision, not a second create.
                target = createdRow;
                isNew = false;
            }
            else
            {
                target = new JournalTask
                {
                    ExternalUid = uid ?? JournalTaskService.NewExternalUid(),
                    Title = title,
                    CreatedByUserId = userId,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                context.JournalTasks.Add(target);
                if (uid is not null)
                {
                    createdByUid[uid] = target;
                }

                isNew = true;
            }

            var oldStatus = isNew ? (JournalTaskStatus?)null
                : JournalTaskService.DeriveStatus(target.Archived, target.CompletedAt, target.StartedAt);

            target.Title = title;
            target.Content = content;
            target.Deadline = deadline;
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

            // Re-derive the lifecycle timestamps from STATUS only when the derived status actually changes
            // (matching JournalTaskService.Update's semantics) — re-importing an unchanged task must not
            // re-stamp StartedAt/CompletedAt, so a round-trip stays idempotent bar UpdatedAt/UpdatedBy.
            // Position is never in the wire format; recompute it on the same condition — append to the end
            // of the derived status column on create or a status change, leave it untouched otherwise (AC #14).
            if (isNew || oldStatus != status)
            {
                JournalTaskService.ApplyStatusTimestamps(target, status, now);
                target.Position = await NextPositionAsync(status, nextPosition, cancellationToken);
            }

            ApplyTags(target, todo, tagsByName, maxLinksPerKind, skipped, ref skippedTagLinks);

            var references = new List<Guid?>();
            var fileReferences = 0;
            foreach (var attachment in todo.Attachments)
            {
                if (spool.Find(attachment.Uri) is { } spooled)
                {
                    references.Add(await inlineFiles.ResolveAsync(spooled, title, cancellationToken));
                }
                else if (TryParseFileId(attachment, out var fileId))
                {
                    // An ATTACH using any other scheme (a real linked file from a third-party producer) is silently
                    // ignored — never skip-counted (§5 pipeline step 4.7).
                    fileReferences++;
                    references.Add(fileId is { } id && existingFileIds.Contains(id) ? id : null);
                }
            }

            // Without the right to link files every reference is skipped and the existing links are left
            // untouched, as on the journal-entry import (§9, N1). This path used to replace them with the
            // empty set, so re-importing a task without files.read deleted its attachments (issue #287 M8).
            // An inline file never reaches here without files.read: the import is refused up front.
            if (!canLinkFiles)
            {
                skippedAttachments += fileReferences;
                continue;
            }

            ApplyAttachments(target, references, now, maxLinksPerKind, skipped, ref skippedAttachments);
        }

        await context.SaveChangesAsync(cancellationToken);

        return new TaskIcsImportResult
        {
            ImportedCount = imported,
            UpdatedCount = updated,
            SkippedTagLinkCount = skippedTagLinks,
            SkippedAttachmentCount = skippedAttachments,
            StoredFileCount = inlineFiles.Stored,
            ReusedFileCount = inlineFiles.Reused,
            Skipped = skipped.ToGroups((reason, count, samples) => new TaskImportSkipGroup
            {
                Reason = reason,
                Count = count,
                SampleTitles = samples,
            }),
        };
    }

    /// <summary>
    /// Turns spooled inline attachments into file ids, once per attachment and once per distinct content: a file
    /// stored earlier in the same import is reused by its hash, before it has even been saved.
    /// </summary>
    private sealed class InlineFileResolver(TaskIcsService owner, string userId, long maxFileBytes, ImportSkipCollector skipped)
    {
        private readonly Dictionary<string, Guid?> byReference = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Guid> storedByContent = new(StringComparer.Ordinal);

        public int Stored { get; private set; }

        public int Reused { get; private set; }

        public async Task<Guid?> ResolveAsync(SpooledAttachment attachment, string taskTitle, CancellationToken cancellationToken)
        {
            if (byReference.TryGetValue(attachment.Reference, out var known))
            {
                return known;
            }

            var resolved = await ResolveOnceAsync(attachment, taskTitle, cancellationToken);
            byReference[attachment.Reference] = resolved;
            return resolved;
        }

        private async Task<Guid?> ResolveOnceAsync(SpooledAttachment attachment, string taskTitle, CancellationToken cancellationToken)
        {
            if (attachment.Problem is { } problem)
            {
                skipped.Add(ProblemReason(problem), taskTitle);
                return null;
            }

            var hash = attachment.Sha256Hash!;
            var contentKey = $"{hash}:{attachment.Size}";
            if (storedByContent.TryGetValue(contentKey, out var storedId))
            {
                Reused++;
                return storedId;
            }

            if (await owner.FindExistingFileAsync(attachment.OriginalFileId, hash, attachment.Size, cancellationToken) is { } existingId)
            {
                Reused++;
                storedByContent[contentKey] = existingId;
                IcsAttachmentSpool.Delete(attachment);
                return existingId;
            }

            var path = attachment.Path!;
            var fileName = owner.fileValidation.SanitizeFileName(attachment.FileName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = "attachment";
            }

            if (fileName.Length > FileValidationService.MaxFileNameLength)
            {
                fileName = fileName[..FileValidationService.MaxFileNameLength].TrimEnd();
            }

            var contentType = attachment.ContentType ?? "application/octet-stream";
            try
            {
                // The same checks an upload passes — allow-list, magic bytes, name — so an import is never a way
                // round them. The size cap here is the import's own: the file already arrived inside it.
                owner.fileValidation.ValidateFile(new FileUpload(fileName, contentType, attachment.Size, () => File.OpenRead(path)), maxFileBytes);
            }
            catch (DomainValidationException)
            {
                skipped.Add("Attached file is of a type uploads do not accept, or its content does not match its type.", taskTitle);
                return null;
            }

            if (owner.fileWriter is null)
            {
                throw new FileStorageUnavailableException("File storage is not set up on this server.");
            }

            FileMetadata stored;
            await using (var stream = File.OpenRead(path))
            {
                stored = await owner.fileWriter.StageNewFileAsync(
                    owner.context, StoredFileArea.Journal, stream, fileName, contentType, userId, description: null,
                    Math.Max(1, attachment.Size), cancellationToken);
            }

            IcsAttachmentSpool.Delete(attachment);
            Stored++;
            storedByContent[contentKey] = stored.Id;
            return stored.Id;
        }

        private string ProblemReason(string problem) => problem switch
        {
            IcsAttachmentSpooler.TooLargeProblem => $"Attached file is larger than the {maxFileBytes / (1024 * 1024)} MB limit.",
            IcsAttachmentSpooler.ChecksumProblem => "Attached file does not match its checksum.",
            IcsAttachmentSpooler.EmptyProblem => "Attached file is empty.",
            _ => "Attached file is damaged (not valid base64).",
        };
    }

    /// <summary>The file an inline attachment duplicates, if one is stored and visible: the one it was exported from
    /// (same id, same content), else the oldest with the same SHA-256 and size.</summary>
    private async Task<Guid?> FindExistingFileAsync(Guid? originalId, string sha256, long size, CancellationToken cancellationToken)
    {
        if (originalId is { } id)
        {
            var original = await VisibleFiles.AsNoTracking()
                .Where(f => f.Id == id && f.SizeBytes == size && f.Sha256Hash.ToLower() == sha256)
                .Select(f => (Guid?)f.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (original is not null)
            {
                return original;
            }
        }

        return await VisibleFiles.AsNoTracking()
            .Where(f => f.SizeBytes == size && f.Sha256Hash.ToLower() == sha256)
            .OrderBy(f => f.UploadedAtUtc).ThenBy(f => f.Id)
            .Select(f => (Guid?)f.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<IReadOnlySet<Guid>> ResolveLinkableFileIds(
        IEnumerable<IcalTodo> todos, bool canLinkFiles, CancellationToken cancellationToken)
    {
        if (!canLinkFiles)
        {
            return new HashSet<Guid>();
        }

        var candidates = new HashSet<Guid>();
        foreach (var todo in todos)
        {
            foreach (var attachment in todo.Attachments)
            {
                if (TryParseFileId(attachment, out var fileId) && fileId is { } id)
                {
                    candidates.Add(id);
                }
            }
        }

        return candidates.Count == 0 ? candidates : await files.ExistingIdsAsync(candidates.ToList(), cancellationToken);
    }

    private async Task<int> NextPositionAsync(
        JournalTaskStatus status, Dictionary<JournalTaskStatus, int> counters, CancellationToken cancellationToken)
    {
        if (!counters.TryGetValue(status, out var next))
        {
            var max = await JournalTaskService.InColumn(context.JournalTasks, status)
                .Select(i => (int?)i.Position)
                .MaxAsync(cancellationToken);
            next = (max ?? -1) + 1;
        }

        counters[status] = next + 1;
        return next;
    }

    private static void ApplyTags(
        JournalTask target, IcalTodo todo, Dictionary<string, Guid> tagsByName, int maxLinksPerKind,
        ImportSkipCollector skipped, ref int skippedTagLinks)
    {
        var skippedCount = 0;
        var names = todo.Categories.Select(category => category?.Trim()).Where(name => !string.IsNullOrEmpty(name));
        var resolved = ImportLinks.Resolve(
            names, name => tagsByName.TryGetValue(name!, out var tagId) ? tagId : null, maxLinksPerKind,
            onUnresolved: () => skippedCount++,
            onCapped: () =>
            {
                skippedCount++;
                skipped.Add(LinksCappedReason(maxLinksPerKind), target.Title);
            });
        skippedTagLinks += skippedCount;

        ImportLinks.Replace(target.ItemTags, resolved, link => link.JournalTaskTagId,
            tagId => new JournalTaskTagLink { JournalTaskTagId = tagId });
    }

    // references: one entry per odyssey-file: reference or inline file, in file order — null where it did not resolve
    // (a malformed or invisible reference, or an inline file that was refused, which is already reported by reason).
    private static void ApplyAttachments(
        JournalTask target, IReadOnlyList<Guid?> references, DateTime now, int maxLinksPerKind,
        ImportSkipCollector skipped, ref int skippedAttachments)
    {
        var skippedCount = 0;
        var resolved = ImportLinks.Resolve(
            references, fileId => fileId, maxLinksPerKind,
            onUnresolved: () => skippedCount++,
            onCapped: () =>
            {
                skippedCount++;
                skipped.Add(LinksCappedReason(maxLinksPerKind), target.Title);
            });
        skippedAttachments += skippedCount;

        ImportLinks.Replace(target.Attachments, resolved, link => link.FileId,
            fileId => new JournalTaskAttachment { FileId = fileId, CreatedAt = now });
    }

    // Parses an ATTACH value under the odyssey-file scheme. Returns false when the scheme is something
    // else (ignore entirely); returns true with a null fileId when the scheme matches but the value is
    // not a valid GUID (a malformed odyssey reference — the caller counts it as a skipped attachment).
    private static bool TryParseFileId(Attachment attachment, out Guid? fileId)
    {
        fileId = null;
        var uri = attachment.Uri;
        if (uri is null || !string.Equals(uri.Scheme, FileUriScheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var raw = uri.OriginalString;
        var value = raw.Length > FileUriScheme.Length + 1 ? raw[(FileUriScheme.Length + 1)..] : string.Empty;
        if (Guid.TryParse(value, out var parsed))
        {
            fileId = parsed;
        }

        return true;
    }

    private static JournalTaskStatus FromVTodoStatus(string? raw) => raw?.Trim().ToUpperInvariant() switch
    {
        "IN-PROCESS" => JournalTaskStatus.Doing,
        "COMPLETED" => JournalTaskStatus.Done,
        "CANCELLED" => JournalTaskStatus.Archived,
        // NEEDS-ACTION and any unrecognized value default to Backlog per RFC 5545's default-status guidance.
        _ => JournalTaskStatus.Backlog,
    };

    private static DateOnly? ToDeadline(CalDateTime? due)
    {
        if (due is null)
        {
            return null;
        }

        try
        {
            return DateOnly.FromDateTime(due.Value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static string? Normalize(string? uid) => string.IsNullOrWhiteSpace(uid) ? null : uid.Trim();

    /// <summary>Whether the multipart part's content type is acceptable for an <c>.ics</c> upload — the
    /// extension and the parse are the real gates, so we accept what browsers/OSes send for calendar
    /// files and only reject a clearly-wrong declared type. Public for edge gating in the controller.</summary>
    public static bool IsAcceptedContentType(string? contentType) =>
        ImportFileReader.IsAcceptedContentType(contentType, ImportLinks.CalendarContentTypes);
}
