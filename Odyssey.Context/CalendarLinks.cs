using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Odyssey.Context;

// Calendar event attachments (issue #521 §4): four sibling link tables, one per owner and kind, the shape of
// JournalEntryAttachment / JournalEntryPhoto exactly — no attribution column. CalendarLinkService (Odyssey.Core) is the
// only writer of all four; a source-lint keeps it that way. Rows in the two CalendarEvent* tables exist only for a
// standalone event (RecurrencePatternId null): a series' links live on the series and show on every occurrence.

/// <summary>A file from the Files store attached to a standalone calendar event.</summary>
public class CalendarEventAttachment
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid CalendarEventAttachmentId { get; set; }

    public Guid CalendarEventId { get; set; }

    public Guid FileId { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>A library photo linked to a standalone calendar event, at a gallery position.</summary>
public class CalendarEventPhoto
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid CalendarEventPhotoId { get; set; }

    public Guid CalendarEventId { get; set; }

    public Guid PhotoId { get; set; }

    public int Position { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>A file from the Files store attached to a recurring series; it shows on every occurrence.</summary>
public class RecurrencePatternAttachment
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid RecurrencePatternAttachmentId { get; set; }

    public Guid RecurrencePatternId { get; set; }

    public Guid FileId { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>A library photo linked to a recurring series, at a gallery position; it shows on every occurrence.</summary>
public class RecurrencePatternPhoto
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid RecurrencePatternPhotoId { get; set; }

    public Guid RecurrencePatternId { get; set; }

    public Guid PhotoId { get; set; }

    public int Position { get; set; }

    public DateTime CreatedAt { get; set; }
}
