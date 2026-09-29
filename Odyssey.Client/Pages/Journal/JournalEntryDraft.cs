using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Journal;

/// <summary>
/// Editable working copy of a journal entry, shared by the create dialog and the inline-edit form
/// (JournalEntryFields). Links are held as scalar id sets (the §Security mass-assignment invariant).
/// Photos and attachments are already-stored files: the "Add photos" / "Attach documents" pickers
/// upload or pick them, so the draft only ever holds file ids plus what the rows display.
/// </summary>
public sealed class JournalEntryDraft
{
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime? EntryDate { get; set; } = DateTime.UtcNow.Date;
    public string Location { get; set; } = string.Empty;
    public IReadOnlyCollection<string> TagIds { get; set; } = [];
    public IReadOnlyCollection<string> ContactIds { get; set; } = [];
    public List<JournalDraftPhoto> Photos { get; set; } = [];
    public List<FileMetadataResponse> Attachments { get; set; } = [];

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
    /// are hydrated from file metadata by the caller.</summary>
    public static JournalEntryDraft From(ExistingJournalEntry e,
        IEnumerable<JournalDraftPhoto> photos, IEnumerable<FileMetadataResponse> attachments) => new()
    {
        Title = e.Title,
        Content = e.Content,
        EntryDate = e.EntryDate.ToLocalTime().Date,
        Location = e.Location ?? string.Empty,
        TagIds = [.. e.TagIds.Select(id => id.ToString())],
        ContactIds = [.. e.ContactIds.Select(id => id.ToString())],
        Photos = [.. photos],
        Attachments = [.. attachments],
    };
}

/// <summary>A photo on a draft entry: its stored file (the server finds or creates the library photo) and a label.</summary>
public sealed record JournalDraftPhoto(Guid FileId, string Name);

/// <summary>Builds the journal write DTOs from a draft.</summary>
public static class JournalWrite
{
    private static Guid[] ToGuids(IReadOnlyCollection<string> ids) =>
        [.. ids.Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty)];

    public static NewJournalEntry ToNew(JournalEntryDraft d) => new()
    {
        Title = d.Title.Trim(),
        Content = d.Content.Trim(),
        EntryDate = ToUtc(d.EntryDate!.Value),
        Location = string.IsNullOrWhiteSpace(d.Location) ? null : d.Location.Trim(),
        TagIds = ToGuids(d.TagIds),
        ContactIds = ToGuids(d.ContactIds),
        PhotoFileIds = [.. d.Photos.Select(p => p.FileId)],
        AttachmentFileIds = [.. d.Attachments.Select(a => a.Id)],
    };

    public static UpdateJournalEntry ToUpdate(JournalEntryDraft d, bool archived) => new()
    {
        Title = d.Title.Trim(),
        Content = d.Content.Trim(),
        EntryDate = ToUtc(d.EntryDate!.Value),
        Location = string.IsNullOrWhiteSpace(d.Location) ? null : d.Location.Trim(),
        Archived = archived,
        TagIds = ToGuids(d.TagIds),
        ContactIds = ToGuids(d.ContactIds),
        PhotoFileIds = [.. d.Photos.Select(p => p.FileId)],
        AttachmentFileIds = [.. d.Attachments.Select(a => a.Id)],
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

    /// <summary>Re-project a loaded entry with one ATTACHMENT link removed and everything else
    /// unchanged — the file row's "Remove from entry" action. Detaching edits the entry; the file stays
    /// in the files store.</summary>
    public static UpdateJournalEntry WithoutAttachment(ExistingJournalEntry e, Guid fileId)
    {
        var update = FromDetail(e, e.Archived is not null);
        update.AttachmentFileIds = [.. e.Attachments.Select(a => a.FileId).Where(id => id != fileId)];
        return update;
    }

    // EntryDate is a whole-day value the user picks in local time; store the start of that day as UTC.
    private static DateTime ToUtc(DateTime local) =>
        DateTime.SpecifyKind(local.Date, DateTimeKind.Utc);
}
