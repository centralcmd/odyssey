namespace Odyssey.Dtos.Journal;

public sealed record ExistingCalendarEvent
{
    public required Guid CalendarEventId { get; set; }

    public required Guid CalendarId { get; set; }

    public required string Title { get; set; }

    public string? Description { get; set; }

    public string? Location { get; set; }

    /// <summary>The linked place's id (issue #380 §5.1), present whenever the record is linked.</summary>
    public Guid? LocationId { get; set; }

    /// <summary>The linked place's embed, or <see langword="null"/> without <c>locations.read</c>.</summary>
    public LocationRef? LocationRef { get; set; }

    /// <summary>The wall-clock start in <see cref="TimeZoneId"/>, or the floating wall-clock (issue #519 §5).</summary>
    public DateTime StartDateTime { get; set; }

    public DateTime EndDateTime { get; set; }

    public bool IsAllDay { get; set; }

    /// <summary>The event's IANA zone; null for a floating or all-day event.</summary>
    public string? TimeZoneId { get; set; }

    /// <summary>The UTC instant of the start; null for a floating or all-day event.</summary>
    public DateTime? StartUtc { get; set; }

    public DateTime? EndUtc { get; set; }

    /// <summary>The event has no zone: its wall-clock is shown unconverted to every viewer.</summary>
    public bool IsFloating { get; set; }

    // Response-only; never accepted as a request body — see the write-binding note on NewCalendarEvent.
    public Guid? RecurrencePatternId { get; set; }

    /// <summary>The occurrence's key in its series (issue #519 §8.2); null on a standalone row or an un-keyed series.</summary>
    public DateTime? OccurrenceKey { get; set; }

    /// <summary>The occurrence was edited on its own. Calendar data under <c>calendar.read</c> (#520 SR3).</summary>
    public bool IsOverride { get; set; }

    /// <summary>The row belongs to a keyed series (issue #519 v3 §D).</summary>
    public bool IsKeyed { get; set; }

    /// <summary>
    /// Write responses only (issue #519 v5 §3): keying the legacy series this write touched turned the written row into a
    /// standalone event, and the write applied to it alone.
    /// </summary>
    public bool Detached { get; set; }

    /// <summary>Write responses only: rows inline keying of the series detached to standalone events.</summary>
    public int DetachedCount { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
