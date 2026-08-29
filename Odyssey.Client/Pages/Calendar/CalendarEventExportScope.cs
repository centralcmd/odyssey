namespace Odyssey.Client.Pages.Calendar;

/// <summary>
/// The choice offered by <see cref="ExportEventScopeDialog"/> when exporting a single event that is
/// part of a recurring series (issue #340): just the dated occurrence, or the whole series as one
/// RRULE VEVENT.
/// </summary>
public enum CalendarEventExportScope
{
    Occurrence,
    Series,
}
