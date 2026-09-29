using System.Net;
using Moq;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Pages.Journal;
using Odyssey.Dtos.Finance;
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

    // ── Draft writes: the pickers stage stored ids, so a save posts ids and uploads nothing ─────────

    private static FileMetadataResponse Meta(Guid id) =>
        new(id, $"{id}.pdf", "application/pdf", 10, "hash", Created, null);

    [Fact]
    public void A_draft_posts_its_photo_and_attachment_ids_in_display_order()
    {
        var photoA = Guid.NewGuid();
        var photoB = Guid.NewGuid();
        var fileA = Guid.NewGuid();
        var fileB = Guid.NewGuid();
        var draft = new JournalEntryDraft
        {
            Title = "Hike",
            Content = "Summit.",
            Photos = [new JournalDraftPhoto(photoB, "b.jpg"), new JournalDraftPhoto(photoA, "a.jpg")],
            Attachments = [Meta(fileA), Meta(fileB)],
        };

        var created = JournalWrite.ToNew(draft);
        var updated = JournalWrite.ToUpdate(draft, archived: true);

        Assert.Equal([photoB, photoA], created.PhotoFileIds);
        Assert.Equal([fileA, fileB], created.AttachmentFileIds);
        Assert.Equal(created.PhotoFileIds, updated.PhotoFileIds);
        Assert.Equal(created.AttachmentFileIds, updated.AttachmentFileIds);
        Assert.True(updated.Archived);
    }

    /// <summary>An edit seeds the draft from the loaded entry, photos in position order, so an untouched save keeps them.</summary>
    [Fact]
    public void An_untouched_edit_resends_the_loaded_links()
    {
        var file = Guid.NewGuid();
        var entry = Entry(file);
        var ordered = entry.Photos.OrderBy(p => p.Position).ToList();

        var draft = JournalEntryDraft.From(entry,
            ordered.Select(p => new JournalDraftPhoto(p.FileId, "photo")), [Meta(file)]);
        var update = JournalWrite.ToUpdate(draft, archived: false);

        Assert.Equal(ordered.Select(p => p.FileId), update.PhotoFileIds);
        Assert.Equal([file], update.AttachmentFileIds);
    }

    // ── Tasks: attachments left the task dialog for the row, and the PUT replaces the whole set ─────

    private static ExistingJournalTask LoadedTask(params Guid[] fileIds) => new()
    {
        JournalTaskId = Guid.NewGuid(),
        ExternalUid = "task-renew-passport@odyssey",
        Title = "Renew passport",
        Content = "Book the appointment.",
        Deadline = new DateOnly(2026, 10, 1),
        Status = JournalTaskStatus.Doing,
        Position = 3,
        CreatedAt = Created,
        UpdatedAt = Created,
        TagIds = [Guid.NewGuid()],
        Attachments = [.. fileIds.Select(id => new JournalTaskAttachmentDto { JournalTaskAttachmentId = Guid.NewGuid(), FileId = id, CreatedAt = Created })],
    };

    /// <summary>Editing a task without an attachments field must not clear the attachments it has.</summary>
    [Fact]
    public void A_task_edit_carries_its_attachments_through_unchanged()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var draft = JournalTaskDraft.From(LoadedTask(a, b));
        draft.Title = "Renew passport now";

        var update = JournalTaskWrite.ToUpdate(draft);

        Assert.Equal([a, b], update.AttachmentFileIds);
        Assert.Equal("Renew passport now", update.Title);
    }

    [Fact]
    public void WithAttachments_replaces_only_the_set_and_drops_duplicates()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var task = LoadedTask(a);

        var update = JournalTaskWrite.WithAttachments(task, [a, b, b]);

        Assert.Equal([a, b], update.AttachmentFileIds);
        Assert.Equal(task.Title, update.Title);
        Assert.Equal(task.Content, update.Content);
        Assert.Equal(task.Deadline, update.Deadline);
        Assert.Equal(task.TagIds, update.TagIds);
        Assert.Null(update.Status);
        Assert.Null(update.Position);
    }

    // ── The task row's attachment writes re-read the task first ────────────────────────────────────

    private static (Mock<ITaskApiClient> Tasks, List<UpdateJournalTask> Sent) Server(
        ExistingJournalTask? current, HttpStatusCode readFailure = HttpStatusCode.NotFound)
    {
        var sent = new List<UpdateJournalTask>();
        var tasks = new Mock<ITaskApiClient>();
        tasks.Setup(t => t.GetResultAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(current is null
            ? ApiResult<ExistingJournalTask>.Failure(readFailure, new ApiProblem { Detail = readFailure.ToString() })
            : ApiResult<ExistingJournalTask>.Success(current, HttpStatusCode.OK));
        tasks.Setup(t => t.UpdateAsync(It.IsAny<Guid>(), It.IsAny<UpdateJournalTask>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, UpdateJournalTask u, CancellationToken _) => sent.Add(u))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.NoContent));
        return (tasks, sent);
    }

    /// <summary>
    /// Attaching re-projects the task as the server holds it now, so an edit made since the page cached
    /// its copy (here: a renamed task and a file someone else attached) survives the write.
    /// </summary>
    [Fact]
    public async Task Attaching_writes_over_the_current_task_not_a_cached_copy()
    {
        var theirs = Guid.NewGuid();
        var mine = Guid.NewGuid();
        var current = LoadedTask(theirs);
        current.Title = "Renamed by someone else";
        var (tasks, sent) = Server(current);

        var (result, task) = await JournalTaskAttachments.AddAsync(tasks.Object, current.JournalTaskId, [mine, theirs]);

        Assert.True(result.IsSuccess);
        Assert.Same(current, task);
        var update = Assert.Single(sent);
        Assert.Equal([theirs, mine], update.AttachmentFileIds);
        Assert.Equal("Renamed by someone else", update.Title);
    }

    [Fact]
    public async Task Removing_drops_only_that_file_from_the_current_task()
    {
        var keep = Guid.NewGuid();
        var drop = Guid.NewGuid();
        var current = LoadedTask(keep, drop);
        var (tasks, sent) = Server(current);

        await JournalTaskAttachments.RemoveAsync(tasks.Object, current.JournalTaskId, drop);

        Assert.Equal([keep], Assert.Single(sent).AttachmentFileIds);
    }

    /// <summary>
    /// A task that cannot be read is never written, and the read's own outcome is what the caller sees:
    /// a 404 is a deleted task, but a 403 or a server error is not, and must not be reported as one.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task A_task_that_cannot_be_read_is_not_written_and_keeps_its_status(HttpStatusCode status)
    {
        var (tasks, sent) = Server(null, status);

        var (result, task) = await JournalTaskAttachments.AddAsync(tasks.Object, Guid.NewGuid(), [Guid.NewGuid()]);

        Assert.False(result.IsSuccess);
        Assert.Equal(status, result.Status);
        Assert.Null(task);
        Assert.Empty(sent);
    }
}
