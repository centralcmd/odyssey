using System.ComponentModel.DataAnnotations;
using Odyssey.Dtos;

namespace Odyssey.Dtos.Journal;

public sealed record NewRecurrencePattern
{
    [Required]
    public required Guid CalendarId { get; set; }

    [StringLength(200)]
    [Required]
    public required string Title { get; set; }

    [StringLength(2000)]
    public string? Description { get; set; }

    /// <summary>Optional detail text beside the linked place (issue #380 §4), e.g. "room 3".</summary>
    [StringLength(300)]
    public string? Location { get; set; }

    /// <summary>
    /// The linked place, by id only (issue #380 §5.2). A changed link needs <c>locations.read</c>; re-sending
    /// the stored id needs nothing. This is a full replacement, so an omitted id clears the link.
    /// </summary>
    public Guid? LocationId { get; set; }

    /// <summary>The first occurrence's wall-clock start in <see cref="TimeZoneId"/>, or floating without one (issue #519 §5).</summary>
    [Required]
    public required DateTime StartDateTime { get; set; }

    [Required]
    public required DateTime EndDateTime { get; set; }

    public bool IsAllDay { get; set; }

    [EnumDataType(typeof(RecurrenceFrequency))]
    public required RecurrenceFrequency Frequency { get; set; }

    [Range(1, 365)]
    public int Interval { get; set; } = 1;

    // [Flags]-aware: rejects any bit outside Monday..Sunday (issue #243), e.g. 128 or 129.
    [EnumDataType(typeof(DaysOfWeekFlags))]
    public DaysOfWeekFlags? DaysOfWeek { get; set; }

    [Range(1, 31)]
    public int? DayOfMonth { get; set; }

    [Range(1, 12)]
    public int? MonthOfYear { get; set; }

    /// <summary>The inclusive <c>UNTIL</c>, as a wall-clock in <see cref="TimeZoneId"/>, or floating without one.</summary>
    public DateTime? RecurrenceEndDate { get; set; }

    [Range(1, 730)]
    public int? OccurrenceCount { get; set; }

    /// <summary>
    /// The zone the rule is generated in (issue #519 §8.1): null on <c>POST</c> is floating, null on <c>PUT</c> keeps the
    /// stored zone. Ignored on an all-day series. Changing it keeps every occurrence key and moves the instants.
    /// </summary>
    [StringLength(IanaTimeZones.MaxLength)]
    [IanaTimeZone]
    public string? TimeZoneId { get; set; }

    /// <summary>
    /// <c>PUT</c> only (issue #519 v3 §B): the series <c>Version</c> the caller edited. When it is sent and stale the write
    /// is a <c>409 calendar-series-version-conflict</c>; omitted, no check is made. Ignored on <c>POST</c>.
    /// </summary>
    public Guid? ExpectedVersion { get; set; }

    /// <summary>
    /// <c>PUT</c> only: the drops the caller's user confirmed from the preview. When sent and exceeded by the drops the
    /// write would actually make, it is a <c>409 calendar-series-drops-changed</c> and nothing is written.
    /// </summary>
    public RecurrenceConfirmedDrops? ConfirmedDrops { get; set; }
}

/// <summary>The overrides and exclusions a user agreed a series edit may discard (issue #519 v3 §B.3).</summary>
public sealed record RecurrenceConfirmedDrops
{
    [Range(0, int.MaxValue)]
    public int Overrides { get; set; }

    [Range(0, int.MaxValue)]
    public int Exclusions { get; set; }
}

/// <summary>
/// The result of a series <c>PUT</c> (issue #519 §5, S5; v4 §4, v5 §4): the series, and what the edit discarded,
/// detached or moved. <c>GET</c> and <c>POST</c> carry no counts.
/// </summary>
public sealed record RecurrencePatternUpdateResult
{
    public required ExistingRecurrencePattern Pattern { get; set; }

    /// <summary>Separately edited future occurrences whose key the new schedule no longer contains.</summary>
    public int DroppedOverrideCount { get; set; }

    /// <summary>Deleted future occurrences whose key the new schedule no longer contains.</summary>
    public int DroppedExclusionCount { get; set; }

    /// <summary>Rows inline keying of a legacy series turned into standalone events.</summary>
    public int DetachedCount { get; set; }

    /// <summary>Rows whose wall-clock fell in a daylight-saving gap of the series' new zone and moved forward.</summary>
    public int AdjustedCount { get; set; }
}

/// <summary>
/// What a series <c>PUT</c> with the same body would discard (issue #519 §5 endpoint 4). Advisory: the preview takes
/// no lock, and the <c>PUT</c> recomputes and binds the counts through <see cref="NewRecurrencePattern.ConfirmedDrops"/>.
/// </summary>
public sealed record RecurrencePatternPreview
{
    public int DroppedOverrideCount { get; set; }

    public int DroppedExclusionCount { get; set; }

    public int DetachedCount { get; set; }

    public int AdjustedCount { get; set; }

    /// <summary>The series' version the counts were computed against.</summary>
    public Guid Version { get; set; }
}
