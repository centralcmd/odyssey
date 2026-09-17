using Odyssey.Client.Pages.Journal;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// JournalWrite re-projects a LOADED entry into an update DTO. That shape is the whole point: the
/// journal PUT takes full link sets, so detaching one file means resending every other field exactly
/// as it was. A helper that dropped a field would silently clear it on the server — which is why these
/// pin what is carried through, not just what is removed.
/// </summary>
public class JournalWriteTests
{
    private static readonly DateTime Created = new(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);

    private static JournalEntryAttachmentDto Attachment(Guid fileId) => new()
    {
        JournalEntryAttachmentId = Guid.NewGuid(),
        FileId = fileId,
        CreatedAt = Created,
    };

    private static ExistingJournalEntry Entry(params Guid[] attachmentFileIds) => new()
    {
        JournalEntryId = Guid.NewGuid(),
        Title = "Moved apartments",
        Content = "Move-in day.",
        EntryDate = new DateTime(2026, 5, 25, 0, 0, 0, DateTimeKind.Utc),
        Location = "New apartment",
        CreatedAt = Created,
        UpdatedAt = Created,
        TagIds = [Guid.NewGuid(), Guid.NewGuid()],
        ContactIds = [Guid.NewGuid()],
        Photos =
        [
            new() { JournalEntryPhotoId = Guid.NewGuid(), PhotoId = Guid.NewGuid(), FileId = Guid.NewGuid(), Position = 1, CreatedAt = Created },
            new() { JournalEntryPhotoId = Guid.NewGuid(), PhotoId = Guid.NewGuid(), FileId = Guid.NewGuid(), Position = 0, CreatedAt = Created },
        ],
        Attachments = [.. attachmentFileIds.Select(Attachment)],
    };

    [Fact]
    public void WithoutAttachment_removes_only_the_named_file()
    {
        var keptA = Guid.NewGuid();
        var removed = Guid.NewGuid();
        var keptB = Guid.NewGuid();
        var entry = Entry(keptA, removed, keptB);

        var update = JournalWrite.WithoutAttachment(entry, removed);

        Assert.Equal([keptA, keptB], update.AttachmentFileIds);
    }

    // The file is detached from the ENTRY; every other link the entry holds has to survive the write
    // untouched, or a detach would quietly strip the entry's tags, contacts or photos.
    [Fact]
    public void WithoutAttachment_carries_every_other_field_through_unchanged()
    {
        var removed = Guid.NewGuid();
        var entry = Entry(removed);

        var update = JournalWrite.WithoutAttachment(entry, removed);

        Assert.Empty(update.AttachmentFileIds);
        Assert.Equal(entry.Title, update.Title);
        Assert.Equal(entry.Content, update.Content);
        Assert.Equal(entry.EntryDate, update.EntryDate);
        Assert.Equal(entry.Location, update.Location);
        Assert.Equal(entry.TagIds, update.TagIds);
        Assert.Equal(entry.ContactIds, update.ContactIds);
    }

    // Photos are re-sent in Position order, not in whatever order the read happened to hold them —
    // the PUT rewrites the gallery from this list, so a detach must not reshuffle it.
    [Fact]
    public void WithoutAttachment_resends_photos_in_position_order()
    {
        var removed = Guid.NewGuid();
        var entry = Entry(removed);

        var update = JournalWrite.WithoutAttachment(entry, removed);

        Assert.Equal(entry.Photos.OrderBy(p => p.Position).Select(p => p.FileId), update.PhotoFileIds);
    }

    // An id that is not attached is a no-op rather than an error: the UI can only offer files it just
    // rendered, so a miss means the list moved under the caller, and clearing the set would be worse.
    [Fact]
    public void WithoutAttachment_leaves_the_set_alone_when_the_file_is_not_attached()
    {
        var attached = Guid.NewGuid();
        var entry = Entry(attached);

        var update = JournalWrite.WithoutAttachment(entry, Guid.NewGuid());

        Assert.Equal([attached], update.AttachmentFileIds);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithoutAttachment_preserves_the_archival_state(bool archived)
    {
        var removed = Guid.NewGuid();
        var entry = Entry(removed);
        entry.Archived = archived ? Created : null;

        var update = JournalWrite.WithoutAttachment(entry, removed);

        Assert.Equal(archived, update.Archived);
    }
}
