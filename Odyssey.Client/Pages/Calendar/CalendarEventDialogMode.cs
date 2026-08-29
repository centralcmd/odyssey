namespace Odyssey.Client.Pages.Calendar;

/// <summary>
/// The three modes <see cref="CalendarEventDialog"/> shows, mirroring the Odyssey Design System's
/// <c>AddCalendarEventModal</c>: a brand-new event, a single existing event/occurrence (the Repeats
/// toggle is read-only — fixed at creation), or a recurring series' template/rule.
/// </summary>
public enum CalendarEventDialogMode
{
    Create,
    Edit,
    Series,
}
