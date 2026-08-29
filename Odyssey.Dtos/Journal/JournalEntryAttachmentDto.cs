namespace Odyssey.Dtos.Journal;

/// <summary>A journal entry's owned attachment record: a reference to a Files-store file id.</summary>
public sealed record JournalEntryAttachmentDto
{
    public required Guid JournalEntryAttachmentId { get; set; }

    public required Guid FileId { get; set; }

    public required DateTime CreatedAt { get; set; }
}
