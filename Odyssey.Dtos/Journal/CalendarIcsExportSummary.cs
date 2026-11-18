namespace Odyssey.Dtos.Journal;

/// <summary>
/// What a calendar <c>.ics</c> export would contain, computed by the same planner the export itself runs (issue #516
/// §5). The client asks for it before starting the download, so a refusal (an unknown calendar, event or series, or
/// the event cap) arrives as an ordinary error instead of a failed browser download.
/// </summary>
public sealed record CalendarIcsExportSummary
{
    /// <summary>The planned <c>VEVENT</c> blocks; a series collapsed to one <c>RRULE</c> counts as 1.</summary>
    public int EventCount { get; set; }

    /// <summary>How many of <see cref="EventCount"/> are <c>RRULE</c> blocks.</summary>
    public int SeriesCount { get; set; }

    /// <summary>The calendar occurrences those blocks represent.</summary>
    public int OccurrenceCount { get; set; }
}
