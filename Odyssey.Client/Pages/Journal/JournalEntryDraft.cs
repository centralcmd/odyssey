using Odyssey.Client.Components;
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
    /// <summary>The Details text beside the place (issue #383 §3.2) — meeting links, room numbers.</summary>
    public string Location { get; set; } = string.Empty;

    /// <summary>The linked place: the stored link, the picker's value and a staged create (issue #383).</summary>
    public PlaceDraft Place { get; set; } = new();

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
        Place = PlaceDraft.For(e.LocationId, e.LocationRef),
        TagIds = [.. e.TagIds.Select(id => id.ToString())],
        ContactIds = [.. e.ContactIds.Select(id => id.ToString())],
        Photos = [.. photos],
        Attachments = [.. attachments],
    };
}

/// <summary>
/// A photo on a draft entry: its stored file (the server finds or creates the library photo), a label, and — when the
/// library photo is known — its id and thumbnail readiness, so the form strip can show the Small thumbnail (issue
/// #365 B2). A stale true costs one 404 and falls back to the original.
/// </summary>
public sealed record JournalDraftPhoto(Guid FileId, string Name, Guid? PhotoId = null, bool ThumbnailsReady = false);

/// <summary>
/// The one builder per journal write DTO (issue #383 §5.2). The server's <c>PUT</c> is a full replacement and
/// an omitted <c>locationId</c> clears an entry's place, so every client construction of
/// <see cref="NewJournalEntry"/> and <see cref="UpdateJournalEntry"/> goes through <see cref="New"/> and
/// <see cref="Update"/>, with the link as a REQUIRED <see cref="LocationLink"/> argument. A source-lint
/// (<c>LocationWriteBuilderLintTests</c>) fails on either DTO constructed anywhere else in the client.
/// </summary>
public static class JournalWrite
{
    private static Guid[] ToGuids(IReadOnlyCollection<string> ids) =>
        [.. ids.Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty)];

    /// <summary>The single construction of <see cref="NewJournalEntry"/> in the client.</summary>
    public static NewJournalEntry New(
        string title, string content, DateTime entryDate, string? details, Guid[] tagIds, Guid[] contactIds,
        Guid[] photoFileIds, Guid[] attachmentFileIds, LocationLink link) => new()
    {
        Title = title,
        Content = content,
        EntryDate = entryDate,
        Location = details,
        LocationId = link.Id,
        TagIds = tagIds,
        ContactIds = contactIds,
        PhotoFileIds = photoFileIds,
        AttachmentFileIds = attachmentFileIds,
    };

    /// <summary>The single construction of <see cref="UpdateJournalEntry"/> in the client.</summary>
    public static UpdateJournalEntry Update(
        string title, string content, DateTime entryDate, string? details, bool archived, Guid[] tagIds, Guid[] contactIds,
        Guid[] photoFileIds, Guid[] attachmentFileIds, LocationLink link) => new()
    {
        Title = title,
        Content = content,
        EntryDate = entryDate,
        Location = details,
        LocationId = link.Id,
        Archived = archived,
        TagIds = tagIds,
        ContactIds = contactIds,
        PhotoFileIds = photoFileIds,
        AttachmentFileIds = attachmentFileIds,
    };

    public static NewJournalEntry ToNew(JournalEntryDraft d, LocationLink link) => New(
        d.Title.Trim(),
        d.Content.Trim(),
        ToUtc(d.EntryDate!.Value),
        string.IsNullOrWhiteSpace(d.Location) ? null : d.Location.Trim(),
        ToGuids(d.TagIds),
        ToGuids(d.ContactIds),
        [.. d.Photos.Select(p => p.FileId)],
        [.. d.Attachments.Select(a => a.Id)],
        link);

    public static UpdateJournalEntry ToUpdate(JournalEntryDraft d, bool archived, LocationLink link) => Update(
        d.Title.Trim(),
        d.Content.Trim(),
        ToUtc(d.EntryDate!.Value),
        string.IsNullOrWhiteSpace(d.Location) ? null : d.Location.Trim(),
        archived,
        ToGuids(d.TagIds),
        ToGuids(d.ContactIds),
        [.. d.Photos.Select(p => p.FileId)],
        [.. d.Attachments.Select(a => a.Id)],
        link);

    /// <summary>Re-project a loaded entry into an update DTO with only <paramref name="archived"/> changed
    /// (used by the archive/unarchive row action — no fields edited, no re-upload). The stored place is KEPT.</summary>
    public static UpdateJournalEntry FromDetail(ExistingJournalEntry e, bool archived) => Update(
        e.Title,
        e.Content,
        e.EntryDate,
        e.Location,
        archived,
        [.. e.TagIds],
        [.. e.ContactIds],
        [.. e.Photos.OrderBy(p => p.Position).Select(p => p.FileId)],
        [.. e.Attachments.Select(a => a.FileId)],
        LocationLink.Keep(e.LocationId));

    /// <summary>Re-project a loaded entry with one ATTACHMENT link removed and everything else
    /// unchanged — the file row's "Remove from entry" action. Detaching edits the entry; the file stays
    /// in the files store, and the entry keeps its place.</summary>
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
