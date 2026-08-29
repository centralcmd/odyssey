using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Journal;

/// <summary>
/// Editable working copy of a journal entry, shared by the create dialog and the inline-edit form
/// (JournalEntryFields). Links are held as scalar id sets (the §Security mass-assignment invariant);
/// photos/attachments are held as <see cref="OdsUploadFile"/> so one OdsFileUpload seeds the existing
/// files and captures new ones — create + edit run a single path.
/// </summary>
public sealed class JournalEntryDraft
{
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime? EntryDate { get; set; } = DateTime.UtcNow.Date;
    public string Location { get; set; } = string.Empty;
    public IReadOnlyCollection<string> TagIds { get; set; } = [];
    public IReadOnlyCollection<string> ContactIds { get; set; } = [];
    public IReadOnlyList<OdsUploadFile> Photos { get; set; } = [];
    public IReadOnlyList<OdsUploadFile> Attachments { get; set; } = [];

    public string? TitleError { get; set; }
    public string? ContentError { get; set; }
    public string? EntryDateError { get; set; }

    /// <summary>Client-side field validation mirroring the DTO annotations. Sets the *Error fields.</summary>
    public bool Validate()
    {
        TitleError = string.IsNullOrWhiteSpace(Title) ? "Give the entry a title." : null;
        ContentError = string.IsNullOrWhiteSpace(Content) ? "Write something for the entry." : null;
        EntryDateError = EntryDate is null ? "Choose the entry date." : null;
        return TitleError is null && ContentError is null && EntryDateError is null;
    }

    /// <summary>Seed an edit draft from a loaded entry. <paramref name="photos"/>/<paramref name="attachments"/>
    /// are the hydrated upload records (built from file metadata by the caller).</summary>
    public static JournalEntryDraft From(ExistingJournalEntry e,
        IReadOnlyList<OdsUploadFile> photos, IReadOnlyList<OdsUploadFile> attachments) => new()
    {
        Title = e.Title,
        Content = e.Content,
        EntryDate = e.EntryDate.ToLocalTime().Date,
        Location = e.Location ?? string.Empty,
        TagIds = [.. e.TagIds.Select(id => id.ToString())],
        ContactIds = [.. e.ContactIds.Select(id => id.ToString())],
        Photos = photos,
        Attachments = attachments,
    };
}

/// <summary>Builds the journal write DTOs from a draft, uploading any newly-attached files first.</summary>
public static class JournalWrite
{
    /// <summary>Upload the new files in <paramref name="files"/> (those carrying an <c>IBrowserFile</c> source),
    /// keep the already-stored ones (whose <c>Uid</c> is the file id), and return the resulting id list in
    /// display order. Throws if any upload fails (the caller reports it and aborts the save).</summary>
    public static async Task<Guid[]> ResolveFileIdsAsync(
        IFilesApiClient files, IUploadLimitsCache uploadLimits, IReadOnlyList<OdsUploadFile> uploads)
    {
        // Resolved once per save rather than per file, and read from the cache rather than compiled in:
        // the cap is admin-editable (issue #421 Wave 4), and this helper used to pass
        // FilesApiClient.DefaultMaxFileSizeBytes — a constant that ignored a lowered cap entirely.
        var limits = await uploadLimits.GetAsync();

        var ids = new List<Guid>(uploads.Count);
        foreach (var f in uploads)
        {
            if (f.Source is not null)
            {
                var stored = await files.UploadAsync(f.Source.ToApiUpload(limits.MaxUploadBytes));
                ids.Add(stored.Id);
            }
            else if (Guid.TryParse(f.Uid, out var existing))
            {
                ids.Add(existing);
            }
        }
        return [.. ids];
    }

    private static Guid[] ToGuids(IReadOnlyCollection<string> ids) =>
        [.. ids.Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty)];

    public static async Task<NewJournalEntry> ToNewAsync(JournalEntryDraft d, IFilesApiClient files, IUploadLimitsCache uploadLimits) => new()
    {
        Title = d.Title.Trim(),
        Content = d.Content.Trim(),
        EntryDate = ToUtc(d.EntryDate!.Value),
        Location = string.IsNullOrWhiteSpace(d.Location) ? null : d.Location.Trim(),
        TagIds = ToGuids(d.TagIds),
        ContactIds = ToGuids(d.ContactIds),
        PhotoFileIds = await ResolveFileIdsAsync(files, uploadLimits, d.Photos),
        AttachmentFileIds = await ResolveFileIdsAsync(files, uploadLimits, d.Attachments),
    };

    public static async Task<UpdateJournalEntry> ToUpdateAsync(JournalEntryDraft d, IFilesApiClient files, IUploadLimitsCache uploadLimits, bool archived) => new()
    {
        Title = d.Title.Trim(),
        Content = d.Content.Trim(),
        EntryDate = ToUtc(d.EntryDate!.Value),
        Location = string.IsNullOrWhiteSpace(d.Location) ? null : d.Location.Trim(),
        Archived = archived,
        TagIds = ToGuids(d.TagIds),
        ContactIds = ToGuids(d.ContactIds),
        PhotoFileIds = await ResolveFileIdsAsync(files, uploadLimits, d.Photos),
        AttachmentFileIds = await ResolveFileIdsAsync(files, uploadLimits, d.Attachments),
    };

    /// <summary>Re-project a loaded entry into an update DTO with only <paramref name="archived"/> changed
    /// (used by the archive/unarchive row action — no fields edited, no re-upload).</summary>
    public static UpdateJournalEntry FromDetail(ExistingJournalEntry e, bool archived) => new()
    {
        Title = e.Title,
        Content = e.Content,
        EntryDate = e.EntryDate,
        Location = e.Location,
        Archived = archived,
        TagIds = [.. e.TagIds],
        ContactIds = [.. e.ContactIds],
        PhotoFileIds = [.. e.Photos.OrderBy(p => p.Position).Select(p => p.FileId)],
        AttachmentFileIds = [.. e.Attachments.Select(a => a.FileId)],
    };

    // EntryDate is a whole-day value the user picks in local time; store the start of that day as UTC.
    private static DateTime ToUtc(DateTime local) =>
        DateTime.SpecifyKind(local.Date, DateTimeKind.Utc);
}
