namespace Odyssey.Client.Components;

/// <summary>
/// A calendar event flattened for <see cref="OdsCalendarGrid"/> — already resolved to its
/// calendar's swatch colour (Odyssey Design System · CalendarGrid's <c>CalendarEventVM</c>).
/// <see cref="End"/> is EXCLUSIVE for an all-day event (the midnight after the last day),
/// mirroring the API's storage semantics.
/// </summary>
public sealed record CalendarEventVm(
    Guid Id,
    Guid CalendarId,
    string CalendarName,
    string Title,
    DateTime Start,
    DateTime End,
    bool IsAllDay,
    string Color,
    string Fg,
    bool Recurring);
