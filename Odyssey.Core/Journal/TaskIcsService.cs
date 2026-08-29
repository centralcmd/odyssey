using Odyssey.Core;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odyssey.Core.Finance;
using Odyssey.Context;
using Odyssey.Dtos.Journal;
using Odyssey.Core.Journal.Interop;
using Ical.Net.Serialization;
using IcalCalendar = Ical.Net.Calendar;
using IcalTodo = Ical.Net.CalendarComponents.Todo;
using CalDateTime = Ical.Net.DataTypes.CalDateTime;
using Attachment = Ical.Net.DataTypes.Attachment;

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
    internal static string LinksCappedReason(int maxLinksPerKind) =>
        $"Links over the per-task cap of {maxLinksPerKind} were not imported.";

    private static readonly string[] AcceptedContentTypes =
        ["text/calendar", "application/octet-stream", "text/plain"];

    private readonly OdysseyContext context;
    private readonly IFileLookup files;
    private readonly IImportExportLimitsLookup limits;
    private readonly IJournalLimitsLookup journalLimits;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<TaskIcsService> logger;

    // journalLimits is a SECOND lookup, newly injected in issue #434 to fix defect A — see the same
    // note on JournalEntryIcsService: this service enforced a hardcoded 50 while the administrator's
    // JournalTaskMaxLinksPerKind setting was honoured on the create/update path and ignored here.
    public TaskIcsService(
        OdysseyContext context, IFileLookup files, IImportExportLimitsLookup limits,
        IJournalLimitsLookup journalLimits, ILogger<TaskIcsService> logger, TimeProvider? timeProvider = null)
    {
        this.context = context;
        this.files = files;
        this.limits = limits;
        this.journalLimits = journalLimits;
        this.logger = logger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    // ---------------------------------------------------------------- Export

    /// <summary>
    /// Streams the tasks matching <paramref name="query"/>'s search/tag/status filters directly to
    /// <paramref name="output"/> as a VTODO <c>.ics</c> document, in row-keyset chunks (issue #343 §5
    /// Goal 8), bounded by the configured <c>TaskIcsMaxExportTasks</c> cap (a follow-up to the original
    /// Non-Goal 2 — the board previously had no export row cap at all; it now matches the other three
    /// surfaces, including the "No limit" option). <paramref name="onReady"/> is called exactly once,
    /// with the file name and row count, after the pre-fetch count is known but before any byte is
    /// written — the caller's only chance to set response headers (including
    /// <c>X-Odyssey-Export-Rows</c>).
    /// <para>
    /// The configured <c>TaskIcsMaxExportMegabytes</c> cap is enforced differently: the total output
    /// size isn't knowable until it's generated, so once writing the next chunk would cross it, the
    /// stream stops (without writing that chunk) rather than rejecting up front — leaving the response
    /// with fewer rows than <c>X-Odyssey-Export-Rows</c> already promised, which the API client's
    /// existing completeness check already treats as a failed download.
    /// </para>
    /// </summary>
    public async Task ExportStreamingAsync(
        JournalTasksQueryParams query, Stream output, Action<string, int> onReady, CancellationToken cancellationToken = default)
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
        int count;
        if (cap is { } max)
        {
            orderedIds = await orderedIdsQuery.Take(max + 1).ToListAsync(cancellationToken);
            if (orderedIds.Count > max)
            {
                throw new DomainValidationException(
                    $"The filtered export would exceed {max} tasks — narrow the search, tag, or status filters.");
            }

            count = orderedIds.Count;
        }
        else
        {
            orderedIds = await orderedIdsQuery.ToListAsync(cancellationToken);
            count = orderedIds.Count;
        }

        var exportDate = timeProvider.GetUtcNow().UtcDateTime;
        onReady($"odyssey-tasks-{exportDate:yyyyMMdd-HHmmss}Z.ics", count);

        var (head, tail) = IcsChunkSerializer.BuildEnvelope(ProductId);
        await IcsChunkSerializer.WriteAsync(output, head, cancellationToken);
        var writtenBytes = (long)Encoding.UTF8.GetByteCount(head);

        var written = 0;
        foreach (var idBatch in orderedIds.Chunk(ExportChunking.ChunkSize))
        {
            var rows = await baseQuery
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

            var chunk = new IcalCalendar();
            foreach (var task in tasks)
            {
                chunk.Todos.Add(BuildVTodo(task, tagNames));
            }

            var chunkText = IcsChunkSerializer.SerializeComponents(chunk);
            var chunkBytes = Encoding.UTF8.GetByteCount(chunkText);
            if (writtenBytes + chunkBytes > maxExportBytes)
            {
                logger.LogWarning(
                    "Tasks .ics export truncated at {WrittenBytes} bytes (cap {MaxBytes}); " +
                    "{WrittenRows}/{TotalRows} tasks delivered.",
                    writtenBytes, maxExportBytes, written, count);
                break;
            }

            await IcsChunkSerializer.WriteAsync(output, chunkText, cancellationToken);
            writtenBytes += chunkBytes;
            written += tasks.Count;
        }

        await IcsChunkSerializer.WriteAsync(output, tail, cancellationToken);
    }

    private static IcalTodo BuildVTodo(JournalTask task, IReadOnlyDictionary<Guid, string> tagNames)
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

        foreach (var attachment in task.Attachments.OrderBy(a => a.CreatedAt))
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

    public async Task<TaskIcsImportResult> ImportAsync(
        Stream icsFile, long contentLength, string? contentType, string userId, bool canLinkFiles,
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

        // Non-archived tags, indexed by case-insensitive exact name (first wins on a duplicate name).
        var tagRows = await context.JournalTaskTags
            .Where(t => t.Archived == null)
            .Select(t => new { t.JournalTaskTagId, t.Name })
            .ToListAsync(cancellationToken);
        var tagsByName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tagRows)
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
            ApplyAttachments(
                target, todo, canLinkFiles, existingFileIds, now, maxLinksPerKind, skipped,
                ref skippedAttachments);
        }

        await context.SaveChangesAsync(cancellationToken);

        return new TaskIcsImportResult
        {
            ImportedCount = imported,
            UpdatedCount = updated,
            SkippedTagLinkCount = skippedTagLinks,
            SkippedAttachmentCount = skippedAttachments,
            Skipped = skipped.ToGroups((reason, count, samples) => new TaskImportSkipGroup
            {
                Reason = reason,
                Count = count,
                SampleTitles = samples,
            }),
        };
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
        var resolved = new List<Guid>();
        foreach (var category in todo.Categories)
        {
            var name = category?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (!tagsByName.TryGetValue(name, out var tagId))
            {
                skippedTagLinks++;
                continue;
            }

            if (resolved.Contains(tagId))
            {
                continue;
            }

            if (resolved.Count >= maxLinksPerKind)
            {
                skippedTagLinks++;
                skipped.Add(LinksCappedReason(maxLinksPerKind), target.Title);
                continue;
            }

            resolved.Add(tagId);
        }

        var desired = resolved.ToHashSet();
        foreach (var link in target.ItemTags.ToList())
        {
            if (!desired.Contains(link.JournalTaskTagId))
            {
                target.ItemTags.Remove(link);
            }
        }

        var current = target.ItemTags.Select(l => l.JournalTaskTagId).ToHashSet();
        foreach (var tagId in resolved)
        {
            if (current.Add(tagId))
            {
                target.ItemTags.Add(new JournalTaskTagLink { JournalTaskTagId = tagId });
            }
        }
    }

    private static void ApplyAttachments(
        JournalTask target, IcalTodo todo, bool canLinkFiles, IReadOnlySet<Guid> existingFileIds,
        DateTime now, int maxLinksPerKind, ImportSkipCollector skipped, ref int skippedAttachments)
    {
        var resolved = new List<Guid>();
        foreach (var attachment in todo.Attachments)
        {
            // An ATTACH using any scheme other than odyssey-file (a real linked/embedded file from a
            // third-party producer) is silently ignored — never skip-counted (§5 pipeline step 4.7).
            if (!TryParseFileId(attachment, out var fileId))
            {
                continue;
            }

            // Scheme matched but the value wasn't a parseable file id, or the caller can't link files, or
            // the file doesn't exist / isn't visible → skip that link only, count it, keep the task.
            if (fileId is not { } id || !canLinkFiles || !existingFileIds.Contains(id))
            {
                skippedAttachments++;
                continue;
            }

            if (resolved.Contains(id))
            {
                continue;
            }

            if (resolved.Count >= maxLinksPerKind)
            {
                skippedAttachments++;
                skipped.Add(LinksCappedReason(maxLinksPerKind), target.Title);
                continue;
            }

            resolved.Add(id);
        }

        var desired = resolved.ToHashSet();
        foreach (var link in target.Attachments.ToList())
        {
            if (!desired.Contains(link.FileId))
            {
                target.Attachments.Remove(link);
            }
        }

        var current = target.Attachments.Select(a => a.FileId).ToHashSet();
        foreach (var fileId in resolved)
        {
            if (current.Add(fileId))
            {
                target.Attachments.Add(new JournalTaskAttachment { FileId = fileId, CreatedAt = now });
            }
        }
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
        ImportFileReader.IsAcceptedContentType(contentType, AcceptedContentTypes);
}
