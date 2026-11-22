using Odyssey.Client.Components;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Calendar;

/// <summary>
/// The one builder per calendar write DTO (issue #383 §5.2). The server's <c>PUT</c> is a full replacement
/// and an omitted <c>locationId</c> clears an event's place, so every client construction of
/// <see cref="NewCalendarEvent"/> and <see cref="NewRecurrencePattern"/> goes through here, with the link as
/// a REQUIRED <see cref="LocationLink"/> argument. A source-lint (<c>LocationWriteBuilderLintTests</c>) fails
/// on either DTO constructed anywhere else in the client.
/// </summary>
/// <remarks>
/// Times are WALL-CLOCK plus zone (issue #520 §2 goal 3, #519 §8.1): the server derives the instant. A zoned time is
/// sent unlabelled (<c>Kind = Unspecified</c>, no <c>Z</c> and never an offset, which the server refuses); a floating
/// or all-day time keeps its UTC label and no zone, exactly as before #519, so a floating event saved unchanged stays
/// floating (AC 2).
/// </remarks>
public static class CalendarWrites
{
    /// <summary>The single construction of <see cref="NewCalendarEvent"/> in the client.</summary>
    public static NewCalendarEvent Event(
        Guid calendarId, string title, string? description, string? details,
        DateTime start, DateTime end, bool isAllDay, string? timeZoneId, AttachmentLinks attachments, LocationLink link) => new()
        {
            CalendarId = calendarId,
            Title = title,
            Description = description,
            Location = details,
            LocationId = link.Id,
            StartDateTime = Wire(start, Zone(isAllDay, timeZoneId)),
            EndDateTime = Wire(end, Zone(isAllDay, timeZoneId)),
            IsAllDay = isAllDay,
            TimeZoneId = Zone(isAllDay, timeZoneId),
            AttachmentFileIds = attachments.FileIds,
            PhotoIds = attachments.PhotoIds,
        };

    /// <summary>
    /// A loaded event re-timed and nothing else — the calendar's drag (a day move) and resize (a new start
    /// and end). The stored place is KEPT: the VM carries no place, and an omitted id would unlink it.
    /// <paramref name="start"/> and <paramref name="end"/> are wall-clock in the event's OWN zone (issue #520 §3 "Drag
    /// and resize"), so its zone travels with them; a floating event keeps none. The links are an argument too (issue
    /// #522 goal 5): a list read carries no link ids, so a drag or resize passes <see cref="AttachmentLinks.Keep"/>.
    /// </summary>
    public static NewCalendarEvent Retimed(ExistingCalendarEvent existing, DateTime start, DateTime end, AttachmentLinks attachments) =>
        Event(existing.CalendarId, existing.Title, existing.Description, existing.Location,
            start, end, existing.IsAllDay, existing.TimeZoneId, attachments, LocationLink.Keep(existing.LocationId));

    /// <summary>The single construction of <see cref="NewRecurrencePattern"/> in the client.</summary>
    public static NewRecurrencePattern Pattern(
        Guid calendarId, string title, string? description, string? details,
        DateTime start, DateTime end, bool isAllDay, string? timeZoneId,
        RecurrenceFrequency frequency, int interval, DaysOfWeekFlags? daysOfWeek, int? dayOfMonth, int? monthOfYear,
        DateTime? recurrenceEndDate, int? occurrenceCount, AttachmentLinks attachments, LocationLink link) => new()
        {
            CalendarId = calendarId,
            Title = title,
            Description = description,
            Location = details,
            LocationId = link.Id,
            StartDateTime = Wire(start, Zone(isAllDay, timeZoneId)),
            EndDateTime = Wire(end, Zone(isAllDay, timeZoneId)),
            IsAllDay = isAllDay,
            TimeZoneId = Zone(isAllDay, timeZoneId),
            Frequency = frequency,
            Interval = interval,
            DaysOfWeek = daysOfWeek,
            DayOfMonth = dayOfMonth,
            MonthOfYear = monthOfYear,
            RecurrenceEndDate = recurrenceEndDate is { } until ? Wire(until, Zone(isAllDay, timeZoneId)) : null,
            OccurrenceCount = occurrenceCount,
            AttachmentFileIds = attachments.FileIds,
            PhotoIds = attachments.PhotoIds,
        };

    /// <summary>
    /// A series write bound to what the user saw (issue #520 v3 §A, v5 B1): the <see cref="ExistingRecurrencePattern.Version"/>
    /// loaded when the dialog opened, and the drop counts the user confirmed (<c>{0, 0}</c> when no confirm step was shown).
    /// </summary>
    public static NewRecurrencePattern Bound(NewRecurrencePattern pattern, Guid expectedVersion, int confirmedOverrides, int confirmedExclusions) =>
        pattern with
        {
            ExpectedVersion = expectedVersion,
            ConfirmedDrops = new RecurrenceConfirmedDrops { Overrides = confirmedOverrides, Exclusions = confirmedExclusions },
        };

    // An all-day event carries no zone (the server stores it floating).
    private static string? Zone(bool isAllDay, string? timeZoneId) => isAllDay ? null : timeZoneId;

    // A zoned time is its wall-clock, unlabelled; a floating or all-day one is labelled UTC as before #519.
    private static DateTime Wire(DateTime value, string? zone) =>
        DateTime.SpecifyKind(value, zone is null ? DateTimeKind.Utc : DateTimeKind.Unspecified);
}

/// <summary>
/// What a calendar write does with its attached files and photos (issue #522 goal 5, #521 §5.1). Every builder in
/// <see cref="CalendarWrites"/> takes one as a REQUIRED argument, so no write can drop links by leaving them out:
/// <see cref="Keep"/> sends <see langword="null"/> for both kinds, which the server reads as "leave the links alone" — the
/// only path for a drag, a resize, a template-only edit, an occurrence, and a user who cannot see a kind (its links are
/// kept by contract, never by echoing ids). <see cref="Set"/> sends a kind's whole list, <c>[]</c> clearing it.
/// </summary>
public sealed class AttachmentLinks
{
    private AttachmentLinks(Guid[]? fileIds, Guid[]? photoIds)
    {
        FileIds = fileIds;
        PhotoIds = photoIds;
    }

    /// <summary>The <c>attachmentFileIds</c> the write sends; null keeps the stored files.</summary>
    public Guid[]? FileIds { get; }

    /// <summary>The <c>photoIds</c> the write sends, in gallery order; null keeps the stored photos.</summary>
    public Guid[]? PhotoIds { get; }

    /// <summary>Both kinds kept: <c>null</c> arrays.</summary>
    public static AttachmentLinks Keep { get; } = new(null, null);

    /// <summary>
    /// The lists a dialog changed. A kind passed as <see langword="null"/> is kept — a save names only the kinds the user
    /// actually changed (§3.1 "Keeping links").
    /// </summary>
    public static AttachmentLinks Set(IEnumerable<Guid>? fileIds, IEnumerable<Guid>? photoIds) =>
        fileIds is null && photoIds is null ? Keep : new(fileIds?.ToArray(), photoIds?.ToArray());

    public override string ToString() => $"Files({Count(FileIds)}) Photos({Count(PhotoIds)})";

    private static string Count(Guid[]? ids) =>
        ids is null ? "keep" : ids.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
