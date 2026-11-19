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
    bool Recurring,
    // The linked place's name in words, "(archived)" included — null without a readable place (issue #383 §3.4).
    string? LocationName = null,
    // Files or photos are linked — from the list counts, visible to every calendar.read holder (issue #522 §3.3). Display
    // data only, never an authorization signal.
    bool HasAttachments = false);

/// <summary>
/// The suffixes every event surface appends to an event's accessible name, in this order (issue #522 §3.3): the grid
/// chips through their name builders, the popover and agenda rows as visually hidden text.
/// </summary>
public static class OdsCalendarNames
{
    public const string RecurringSuffix = ", recurring";

    public const string AttachmentsSuffix = ", has attachments";
}
