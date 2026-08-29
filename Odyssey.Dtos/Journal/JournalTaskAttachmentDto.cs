namespace Odyssey.Dtos.Journal;

/// <summary>A to-do item's owned attachment record: a reference to a Files-store file id.</summary>
public sealed record JournalTaskAttachmentDto
{
    public required Guid JournalTaskAttachmentId { get; set; }

    public required Guid FileId { get; set; }

    public required DateTime CreatedAt { get; set; }
}
