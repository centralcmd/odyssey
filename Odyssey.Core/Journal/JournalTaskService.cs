using Odyssey.Core;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Odyssey.Core.Finance;
using Odyssey.Core.Pagination;
using Odyssey.Context;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos;

namespace Odyssey.Core.Journal;

/// <summary>
/// CRUD, lifecycle (status), and manual ordering for the shared to-do board. Tags are validated against
/// the module-local <see cref="JournalTaskTag"/> set; attachments are soft references into the Finance Files store
/// (validated via <see cref="IFileLookup"/>). Link sets are diffed on update to keep row identity stable.
/// </summary>
public class JournalTaskService
{

    private readonly OdysseyContext context;
    private readonly IFileLookup files;
    private readonly IJournalLimitsLookup journalLimits;
    private readonly TimeProvider timeProvider;

    public JournalTaskService(
        OdysseyContext context,
        IFileLookup files,
        IJournalLimitsLookup journalLimits,
        TimeProvider? timeProvider = null)
    {
        this.context = context;
        this.files = files;
        this.journalLimits = journalLimits;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Server-side paged list (issue #277): Title/Content search + tag filter + status filter + allowlisted sort.</summary>
    public async Task<PagedResult<JournalTaskSummary>> ListAsync(
        JournalTasksQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var q = ApplyFilters(context.JournalTasks.AsQueryable(), query.Search, query.TagIds, query.Statuses);

        var ascending = ListQuery.Ascending(query.SortDir, naturalDefaultAscending: query.SortBy is not JournalTaskSortBy.CreatedAt);
        IOrderedQueryable<JournalTask> sorted = query.SortBy switch
        {
            JournalTaskSortBy.Deadline => ascending ? q.OrderBy(i => i.Deadline) : q.OrderByDescending(i => i.Deadline),
            JournalTaskSortBy.Title => ascending ? q.OrderBy(i => i.Title) : q.OrderByDescending(i => i.Title),
            // Sort by the derived status rank (Backlog < Doing < Done < Archived). The ternary is inlined
            // (not a helper method) so EF can translate it to a SQL CASE — a private CLR method here throws
            // at query time on the relational provider.
            JournalTaskSortBy.Status => ascending
                ? q.OrderBy(i => i.Archived != null ? 3 : i.CompletedAt != null ? 2 : i.StartedAt != null ? 1 : 0)
                : q.OrderByDescending(i => i.Archived != null ? 3 : i.CompletedAt != null ? 2 : i.StartedAt != null ? 1 : 0),
            JournalTaskSortBy.CreatedAt => ascending ? q.OrderBy(i => i.CreatedAt) : q.OrderByDescending(i => i.CreatedAt),
            _ => ascending ? q.OrderBy(i => i.Position) : q.OrderByDescending(i => i.Position),
        };
        q = sorted.ThenBy(i => i.JournalTaskId);

        var rows = q.Select(i => new
        {
            i.JournalTaskId,
            i.Title,
            i.Content,
            i.Deadline,
            i.Position,
            i.CreatedByUserId,
            i.StartedAt,
            i.CompletedAt,
            i.Archived,
            TagIds = i.ItemTags.Select(t => t.JournalTaskTagId).ToList(),
            AttachmentCount = i.Attachments.Count,
        });

        return await rows.ToPagedResultAsync(query.Offset, query.Limit, row => new JournalTaskSummary
        {
            JournalTaskId = row.JournalTaskId,
            Title = row.Title,
            Snippet = JournalText.Truncate(row.Content, 200),
            Deadline = row.Deadline,
            Status = DeriveStatus(row.Archived, row.CompletedAt, row.StartedAt),
            Position = row.Position,
            CreatedByUserId = row.CreatedByUserId,
            StartedAt = row.StartedAt,
            CompletedAt = row.CompletedAt,
            Archived = row.Archived,
            TagIds = row.TagIds,
            AttachmentCount = row.AttachmentCount,
        }, cancellationToken);
    }

    public async Task<ExistingJournalTask?> Get(Guid id, CancellationToken cancellationToken = default)
    {
        var item = await LoadWithDetails(id, cancellationToken);
        return item is null ? null : ToDto(item);
    }

    public async Task<ExistingJournalTask> Create(NewJournalTask request, string userId, CancellationToken cancellationToken = default)
    {
        var tagIds = request.TagIds.Distinct().ToList();
        var attachmentIds = request.AttachmentFileIds.Distinct().ToList();
        var maxLinksPerKind = (await journalLimits.GetAsync(cancellationToken)).JournalTaskMaxLinksPerKind;
        EnsureWithinCap(tagIds, "tags", maxLinksPerKind);
        EnsureWithinCap(attachmentIds, "attachments", maxLinksPerKind);

        await EnsureTagsExist(tagIds, cancellationToken);
        await EnsureFilesExist(attachmentIds, cancellationToken);

        var externalUid = NormalizeExternalUid(request.ExternalUid) ?? NewExternalUid();
        await EnsureExternalUidAvailable(externalUid, excludeId: null, cancellationToken);

        var now = timeProvider.GetUtcNow().UtcDateTime;

        var item = new JournalTask
        {
            ExternalUid = externalUid,
            Title = request.Title,
            Content = request.Content,
            Deadline = request.Deadline,
            Position = await NextPosition(request.Status, cancellationToken),
            CreatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
            StartedAt = request.Status is JournalTaskStatus.Doing ? now : null,
            CompletedAt = request.Status is JournalTaskStatus.Done ? now : null,
            Archived = request.Status is JournalTaskStatus.Archived ? now : null,
        };

        foreach (var tagId in tagIds)
        {
            item.ItemTags.Add(new JournalTaskTagLink { JournalTaskTagId = tagId });
        }

        foreach (var fileId in attachmentIds)
        {
            item.Attachments.Add(new JournalTaskAttachment { FileId = fileId, CreatedAt = now });
        }

        context.JournalTasks.Add(item);
        await context.SaveChangesAsync(cancellationToken);

        return ToDto(item);
    }

    public async Task<ExistingJournalTask?> Update(Guid id, UpdateJournalTask request, string userId, CancellationToken cancellationToken = default)
    {
        var item = await LoadWithDetailsForUpdate(id, cancellationToken);
        if (item is null)
        {
            return null;
        }

        var tagIds = request.TagIds.Distinct().ToList();
        var attachmentIds = request.AttachmentFileIds.Distinct().ToList();
        var maxLinksPerKind = (await journalLimits.GetAsync(cancellationToken)).JournalTaskMaxLinksPerKind;
        EnsureWithinCap(tagIds, "tags", maxLinksPerKind);
        EnsureWithinCap(attachmentIds, "attachments", maxLinksPerKind);

        await EnsureTagsExist(tagIds, cancellationToken);
        await EnsureFilesExist(attachmentIds, cancellationToken);

        // An optional ExternalUid replaces the stored identity; a null leaves it untouched. A value that
        // already belongs to a different task is rejected (400) before any DB unique-constraint fires.
        var newExternalUid = NormalizeExternalUid(request.ExternalUid);
        if (newExternalUid is not null && newExternalUid != item.ExternalUid)
        {
            await EnsureExternalUidAvailable(newExternalUid, item.JournalTaskId, cancellationToken);
            item.ExternalUid = newExternalUid;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        item.Title = request.Title;
        item.Content = request.Content;
        item.Deadline = request.Deadline;
        item.UpdatedByUserId = userId;
        item.UpdatedAt = now;

        DiffTags(item, tagIds);
        DiffAttachments(item, attachmentIds, now);

        // Lifecycle: an optional Status moves the task between columns by mutating the derived
        // timestamps; a null Status leaves the current state untouched (so a plain field edit is safe).
        var oldStatus = DeriveStatus(item.Archived, item.CompletedAt, item.StartedAt);
        var newStatus = request.Status ?? oldStatus;
        if (newStatus != oldStatus)
        {
            ApplyStatusTimestamps(item, newStatus, now);

            // Appending to the target column keeps ordering gap-free without a full re-sequence.
            if (newStatus is JournalTaskStatus.Backlog or JournalTaskStatus.Doing)
            {
                item.Position = await NextPosition(newStatus, cancellationToken);
            }
        }

        await context.SaveChangesAsync(cancellationToken);

        // Close the gap the departing item leaves in its former ordered column (after the move persists).
        if (newStatus != oldStatus && oldStatus is JournalTaskStatus.Backlog or JournalTaskStatus.Doing)
        {
            await ResequenceColumn(oldStatus, item.JournalTaskId, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
        }

        // Manual ordering: an optional Position reorders the task within its (now persisted) column;
        // ignored for the unordered Done/Archived columns.
        if (request.Position is { } targetPosition && newStatus is JournalTaskStatus.Backlog or JournalTaskStatus.Doing)
        {
            await ReorderWithinColumn(item, newStatus, targetPosition, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
        }

        return ToDto(item);
    }

    public async Task<bool> Delete(Guid id, CancellationToken cancellationToken = default)
    {
        var item = await context.JournalTasks.FirstOrDefaultAsync(i => i.JournalTaskId == id, cancellationToken);
        if (item is null)
        {
            return false;
        }

        context.JournalTasks.Remove(item);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>The shape of a new external identity when the caller doesn't supply one — a URN UUID,
    /// matching the RFC 6350/5545 convention used across the ICS/vCard interop features (issue #337 §6).</summary>
    internal static string NewExternalUid() => $"urn:uuid:{Guid.NewGuid()}";

    private static string? NormalizeExternalUid(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task EnsureExternalUidAvailable(string externalUid, Guid? excludeId, CancellationToken cancellationToken)
    {
        var clash = await context.JournalTasks
            .AnyAsync(i => i.ExternalUid == externalUid && (excludeId == null || i.JournalTaskId != excludeId), cancellationToken);
        if (clash)
        {
            throw new DomainValidationException("External ID is already in use by another task.");
        }
    }

    // Applies the search / tag / status filter surface shared by the list (#277) and the .ics export
    // (#337). Status is derived from the timestamps: an explicit set filters to exactly those derived
    // columns (OR of per-column predicates); otherwise Archived is hidden by default.
    internal static IQueryable<JournalTask> ApplyFilters(
        IQueryable<JournalTask> q, string? search, Guid[]? tagIds, JournalTaskStatus[]? statuses)
    {
        var term = ListQuery.NormalizeSearch(search);
        if (term is not null)
        {
            var pattern = ListQuery.ContainsPattern(term);
            q = q.Where(i => EF.Functions.Like(i.Title, pattern)
                || (i.Content != null && EF.Functions.Like(i.Content, pattern)));
        }

        if (tagIds is { Length: > 0 } ids)
        {
            q = q.Where(i => i.ItemTags.Any(t => ids.Contains(t.JournalTaskTagId)));
        }

        if (statuses is { Length: > 0 } set)
        {
            var wanted = set.ToHashSet();
            bool wantBacklog = wanted.Contains(JournalTaskStatus.Backlog);
            bool wantDoing = wanted.Contains(JournalTaskStatus.Doing);
            bool wantDone = wanted.Contains(JournalTaskStatus.Done);
            bool wantArchived = wanted.Contains(JournalTaskStatus.Archived);
            q = q.Where(i =>
                (wantBacklog && i.Archived == null && i.CompletedAt == null && i.StartedAt == null) ||
                (wantDoing && i.Archived == null && i.CompletedAt == null && i.StartedAt != null) ||
                (wantDone && i.Archived == null && i.CompletedAt != null) ||
                (wantArchived && i.Archived != null));
        }
        else
        {
            q = q.Where(i => i.Archived == null);
        }

        return q;
    }

    // Update is the only caller that writes to what it loads; every other one turns the row straight
    // into a DTO, so it reads through the untracked overload.
    private async Task<JournalTask?> LoadWithDetails(Guid id, CancellationToken cancellationToken) =>
        await WithDetails(context.JournalTasks.AsNoTracking())
            .FirstOrDefaultAsync(i => i.JournalTaskId == id, cancellationToken);

    private async Task<JournalTask?> LoadWithDetailsForUpdate(Guid id, CancellationToken cancellationToken) =>
        await WithDetails(context.JournalTasks)
            .FirstOrDefaultAsync(i => i.JournalTaskId == id, cancellationToken);

    private static IQueryable<JournalTask> WithDetails(IQueryable<JournalTask> tasks) => tasks
        .Include(i => i.ItemTags)
        .Include(i => i.Attachments);

    // Translate a requested lifecycle state into the underlying timestamps. Archived supersedes the
    // others; Done keeps StartedAt; moving to Doing stamps a start if unset.
    internal static void ApplyStatusTimestamps(JournalTask item, JournalTaskStatus newStatus, DateTime now)
    {
        switch (newStatus)
        {
            case JournalTaskStatus.Backlog:
                item.StartedAt = null;
                item.CompletedAt = null;
                item.Archived = null;
                break;
            case JournalTaskStatus.Doing:
                item.StartedAt ??= now;
                item.CompletedAt = null;
                item.Archived = null;
                break;
            case JournalTaskStatus.Done:
                item.CompletedAt = now;
                item.Archived = null;
                break;
            case JournalTaskStatus.Archived:
                item.Archived = now;
                break;
        }
    }

    // Gap-free reorder of the task within its (already-persisted) column: pull the ordered siblings,
    // move the item to the clamped target slot, and renumber 0..n-1.
    private async Task ReorderWithinColumn(JournalTask item, JournalTaskStatus status, int targetPosition, CancellationToken cancellationToken)
    {
        var siblings = await InColumn(context.JournalTasks, status)
            .OrderBy(i => i.Position)
            .ThenBy(i => i.JournalTaskId)
            .ToListAsync(cancellationToken);

        siblings.RemoveAll(i => i.JournalTaskId == item.JournalTaskId);
        var target = Math.Clamp(targetPosition, 0, siblings.Count);
        siblings.Insert(target, item);

        for (var index = 0; index < siblings.Count; index++)
        {
            siblings[index].Position = index;
        }
    }

    // Renumber a derived status column to contiguous 0..n-1 positions, excluding one item (e.g. one that
    // has just moved out). Excluding by id — not by the tracked timestamps — keeps this correct on the
    // relational provider, where the moved item's new timestamps are not yet flushed when this runs.
    private async Task ResequenceColumn(JournalTaskStatus status, Guid excludeId, CancellationToken cancellationToken)
    {
        var siblings = await InColumn(context.JournalTasks, status)
            .Where(i => i.JournalTaskId != excludeId)
            .OrderBy(i => i.Position)
            .ThenBy(i => i.JournalTaskId)
            .ToListAsync(cancellationToken);

        for (var index = 0; index < siblings.Count; index++)
        {
            siblings[index].Position = index;
        }
    }

    private async Task<int> NextPosition(JournalTaskStatus status, CancellationToken cancellationToken)
    {
        var max = await InColumn(context.JournalTasks, status)
            .Select(i => (int?)i.Position)
            .MaxAsync(cancellationToken);
        return (max ?? -1) + 1;
    }

    // The set of items whose DERIVED status is <paramref name="status"/>. Backlog/Doing are the ordered
    // columns Position applies to; Done/Archived are matched too for completeness.
    internal static IQueryable<JournalTask> InColumn(IQueryable<JournalTask> query, JournalTaskStatus status) => status switch
    {
        JournalTaskStatus.Backlog => query.Where(i => i.Archived == null && i.CompletedAt == null && i.StartedAt == null),
        JournalTaskStatus.Doing => query.Where(i => i.Archived == null && i.CompletedAt == null && i.StartedAt != null),
        JournalTaskStatus.Done => query.Where(i => i.Archived == null && i.CompletedAt != null),
        _ => query.Where(i => i.Archived != null),
    };

    // Single source of truth for the derived kanban status. Archived supersedes Done supersedes Doing.
    internal static JournalTaskStatus DeriveStatus(DateTime? archived, DateTime? completedAt, DateTime? startedAt) =>
        archived != null ? JournalTaskStatus.Archived
        : completedAt != null ? JournalTaskStatus.Done
        : startedAt != null ? JournalTaskStatus.Doing
        : JournalTaskStatus.Backlog;

    private static void EnsureWithinCap(IReadOnlyCollection<Guid> ids, string kind, int maxLinksPerKind)
    {
        if (ids.Count > maxLinksPerKind)
        {
            throw new DomainUnprocessableException($"A task may link at most {maxLinksPerKind} {kind}.");
        }
    }

    private async Task EnsureTagsExist(IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken)
    {
        if (tagIds.Count == 0)
        {
            return;
        }

        var found = await context.JournalTaskTags
            .Where(t => tagIds.Contains(t.JournalTaskTagId) && t.Archived == null)
            .Select(t => t.JournalTaskTagId)
            .ToListAsync(cancellationToken);

        var missing = tagIds.Except(found).ToList();
        if (missing.Count > 0)
        {
            throw new DomainUnprocessableException(
                $"Unknown or archived task tag(s): {string.Join(", ", missing)}.");
        }
    }

    private async Task EnsureFilesExist(IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken)
    {
        if (fileIds.Count == 0)
        {
            return;
        }

        var found = await files.ExistingIdsAsync(fileIds, cancellationToken);
        var missing = fileIds.Where(id => !found.Contains(id)).ToList();
        if (missing.Count > 0)
        {
            throw new DomainUnprocessableException(
                $"Unknown attachment file(s): {string.Join(", ", missing)}.");
        }
    }

    private void DiffTags(JournalTask item, IReadOnlyCollection<Guid> desiredTagIds)
    {
        var desired = desiredTagIds.ToHashSet();
        foreach (var link in item.ItemTags.ToList())
        {
            if (!desired.Contains(link.JournalTaskTagId))
            {
                item.ItemTags.Remove(link);
            }
        }

        var current = item.ItemTags.Select(l => l.JournalTaskTagId).ToHashSet();
        foreach (var tagId in desiredTagIds)
        {
            if (current.Add(tagId))
            {
                item.ItemTags.Add(new JournalTaskTagLink { JournalTaskId = item.JournalTaskId, JournalTaskTagId = tagId });
            }
        }
    }

    private void DiffAttachments(JournalTask item, IReadOnlyCollection<Guid> desiredFileIds, DateTime now)
    {
        var desired = desiredFileIds.ToHashSet();
        foreach (var attachment in item.Attachments.ToList())
        {
            if (!desired.Contains(attachment.FileId))
            {
                item.Attachments.Remove(attachment);
            }
        }

        var current = item.Attachments.Select(a => a.FileId).ToHashSet();
        foreach (var fileId in desiredFileIds)
        {
            if (current.Add(fileId))
            {
                item.Attachments.Add(new JournalTaskAttachment
                {
                    JournalTaskId = item.JournalTaskId,
                    FileId = fileId,
                    CreatedAt = now,
                });
            }
        }
    }

    private static ExistingJournalTask ToDto(JournalTask item)
    {
        var dto = item.Adapt<ExistingJournalTask>();
        dto.Status = DeriveStatus(item.Archived, item.CompletedAt, item.StartedAt);
        dto.TagIds = item.ItemTags.Select(t => t.JournalTaskTagId).ToList();
        dto.Attachments = item.Attachments
            .OrderBy(a => a.CreatedAt)
            .Select(a => new JournalTaskAttachmentDto
            {
                JournalTaskAttachmentId = a.JournalTaskAttachmentId,
                FileId = a.FileId,
                CreatedAt = a.CreatedAt,
            })
            .ToList();
        return dto;
    }
}
