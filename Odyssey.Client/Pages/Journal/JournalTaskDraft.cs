using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Journal;

/// <summary>
/// Editable working copy of a task, shared by the create + edit dialog. Status is chosen semantically
/// (JournalTaskStatus); the API maps it to the StartedAt/CompletedAt/Archived timestamps. Attachments
/// are not edited here — the task row's "Attach documents" and "Remove from task" own them — so the
/// draft carries the loaded ids through unchanged, because the update replaces the whole set.
/// </summary>
public sealed class JournalTaskDraft
{
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime? Deadline { get; set; }
    public JournalTaskStatus Status { get; set; } = JournalTaskStatus.Backlog;
    public IReadOnlyCollection<string> TagIds { get; set; } = [];
    public IReadOnlyList<Guid> AttachmentFileIds { get; set; } = [];

    public string? TitleError { get; set; }

    public bool Validate()
    {
        TitleError = string.IsNullOrWhiteSpace(Title) ? "Give the task a title." : null;
        return TitleError is null;
    }

    public static JournalTaskDraft From(ExistingJournalTask t) => new()
    {
        Title = t.Title,
        Content = t.Content ?? string.Empty,
        Deadline = t.Deadline?.ToDateTime(TimeOnly.MinValue),
        Status = t.Status,
        TagIds = [.. t.TagIds.Select(id => id.ToString())],
        AttachmentFileIds = [.. t.Attachments.Select(a => a.FileId)],
    };
}

/// <summary>Builds the task write DTOs from a draft / loaded task.</summary>
public static class JournalTaskWrite
{
    private static Guid[] ToGuids(IReadOnlyCollection<string> ids) =>
        [.. ids.Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty)];

    private static DateOnly? ToDateOnly(DateTime? dt) => dt is { } d ? DateOnly.FromDateTime(d) : null;

    public static NewJournalTask ToNew(JournalTaskDraft d) => new()
    {
        Title = d.Title.Trim(),
        Content = string.IsNullOrWhiteSpace(d.Content) ? null : d.Content.Trim(),
        Deadline = ToDateOnly(d.Deadline),
        Status = d.Status,
        TagIds = ToGuids(d.TagIds),
        AttachmentFileIds = [.. d.AttachmentFileIds],
    };

    public static UpdateJournalTask ToUpdate(JournalTaskDraft d) => new()
    {
        Title = d.Title.Trim(),
        Content = string.IsNullOrWhiteSpace(d.Content) ? null : d.Content.Trim(),
        Deadline = ToDateOnly(d.Deadline),
        Status = d.Status,
        Position = null,
        TagIds = ToGuids(d.TagIds),
        AttachmentFileIds = [.. d.AttachmentFileIds],
    };

    /// <summary>Re-project a loaded task into an update DTO changing only <paramref name="status"/> and/or
    /// <paramref name="position"/> (board move / status cycle / archive) — no fields edited.</summary>
    public static UpdateJournalTask FromDetail(ExistingJournalTask t, JournalTaskStatus? status = null, int? position = null) => new()
    {
        Title = t.Title,
        Content = t.Content,
        Deadline = t.Deadline,
        Status = status,
        Position = position,
        TagIds = [.. t.TagIds],
        AttachmentFileIds = [.. t.Attachments.Select(a => a.FileId)],
    };

    /// <summary>Re-project a loaded task with its attachment set replaced and everything else unchanged —
    /// the row's "Attach documents" (appended ids) and "Remove from task" (one id dropped). The file
    /// stays in the files store either way.</summary>
    public static UpdateJournalTask WithAttachments(ExistingJournalTask t, IEnumerable<Guid> fileIds)
    {
        var update = FromDetail(t);
        update.AttachmentFileIds = [.. fileIds.Distinct()];
        return update;
    }
}

/// <summary>
/// The task row's attachment writes — "Attach documents" and "Remove from task". A task's attachment set
/// is written whole, so each re-reads the task as the server holds it NOW and re-projects from that,
/// never from a detail the page cached earlier: a stale copy would silently undo a concurrent edit to
/// the title, tags or the attachments themselves.
/// </summary>
public static class JournalTaskAttachments
{
    /// <summary>Appends <paramref name="fileIds"/> (duplicates of linked files dropped). The task is null when it could not be read.</summary>
    public static Task<(ApiResult Result, ExistingJournalTask? Task)> AddAsync(
        ITaskApiClient tasks, Guid taskId, IReadOnlyCollection<Guid> fileIds) =>
        WriteAsync(tasks, taskId, current => current.Concat(fileIds));

    /// <summary>Drops one file from the task. The file stays in Files.</summary>
    public static Task<(ApiResult Result, ExistingJournalTask? Task)> RemoveAsync(
        ITaskApiClient tasks, Guid taskId, Guid fileId) =>
        WriteAsync(tasks, taskId, current => current.Where(id => id != fileId));

    private static async Task<(ApiResult, ExistingJournalTask?)> WriteAsync(
        ITaskApiClient tasks, Guid taskId, Func<IEnumerable<Guid>, IEnumerable<Guid>> change)
    {
        // The read's own outcome travels back unchanged: a 404 is "the task is gone", but a 403 or an
        // unreachable server is not, and reporting either as a deletion would misdescribe it.
        var read = await tasks.GetResultAsync(taskId);
        if (read.Value is not { } fresh)
            return (new ApiResult { Status = read.Status, Problem = read.Problem ?? new ApiProblem { Detail = "The task could not be read." } }, null);

        var update = JournalTaskWrite.WithAttachments(fresh, change(fresh.Attachments.Select(a => a.FileId)));
        return (await tasks.UpdateAsync(taskId, update), fresh);
    }
}
