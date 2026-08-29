using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Journal;

/// <summary>
/// Editable working copy of a task, shared by the create + edit dialog. Status is chosen semantically
/// (JournalTaskStatus); the API maps it to the StartedAt/CompletedAt/Archived timestamps. Attachments
/// are held as <see cref="OdsUploadFile"/> so create + edit run one upload path.
/// </summary>
public sealed class JournalTaskDraft
{
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime? Deadline { get; set; }
    public JournalTaskStatus Status { get; set; } = JournalTaskStatus.Backlog;
    public IReadOnlyCollection<string> TagIds { get; set; } = [];
    public IReadOnlyList<OdsUploadFile> Attachments { get; set; } = [];

    public string? TitleError { get; set; }

    public bool Validate()
    {
        TitleError = string.IsNullOrWhiteSpace(Title) ? "Give the task a title." : null;
        return TitleError is null;
    }

    public static JournalTaskDraft From(ExistingJournalTask t, IReadOnlyList<OdsUploadFile> attachments) => new()
    {
        Title = t.Title,
        Content = t.Content ?? string.Empty,
        Deadline = t.Deadline?.ToDateTime(TimeOnly.MinValue),
        Status = t.Status,
        TagIds = [.. t.TagIds.Select(id => id.ToString())],
        Attachments = attachments,
    };
}

/// <summary>Builds the task write DTOs from a draft / loaded task, uploading new files first.</summary>
public static class JournalTaskWrite
{
    private static Guid[] ToGuids(IReadOnlyCollection<string> ids) =>
        [.. ids.Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty)];

    private static DateOnly? ToDateOnly(DateTime? dt) => dt is { } d ? DateOnly.FromDateTime(d) : null;

    public static async Task<NewJournalTask> ToNewAsync(JournalTaskDraft d, IFilesApiClient files, IUploadLimitsCache uploadLimits) => new()
    {
        Title = d.Title.Trim(),
        Content = string.IsNullOrWhiteSpace(d.Content) ? null : d.Content.Trim(),
        Deadline = ToDateOnly(d.Deadline),
        Status = d.Status,
        TagIds = ToGuids(d.TagIds),
        AttachmentFileIds = await JournalWrite.ResolveFileIdsAsync(files, uploadLimits, d.Attachments),
    };

    public static async Task<UpdateJournalTask> ToUpdateAsync(JournalTaskDraft d, IFilesApiClient files, IUploadLimitsCache uploadLimits) => new()
    {
        Title = d.Title.Trim(),
        Content = string.IsNullOrWhiteSpace(d.Content) ? null : d.Content.Trim(),
        Deadline = ToDateOnly(d.Deadline),
        Status = d.Status,
        Position = null,
        TagIds = ToGuids(d.TagIds),
        AttachmentFileIds = await JournalWrite.ResolveFileIdsAsync(files, uploadLimits, d.Attachments),
    };

    /// <summary>Re-project a loaded task into an update DTO changing only <paramref name="status"/> and/or
    /// <paramref name="position"/> (board move / status cycle / archive) — no fields edited, no re-upload.</summary>
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
}
