using Odyssey.Client.Components;
using Odyssey.Client.Pages.Journal;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Calendar;

/// <summary>
/// Editable draft backing <see cref="CalendarEventDialog"/>, following this codebase's
/// hand-rolled-validation convention (see <c>JournalTaskDraft</c>) rather than
/// EditForm/DataAnnotations — per-field <c>*Error</c> strings feed each <c>Ods*Field</c>'s
/// <c>Error</c> parameter directly.
/// </summary>
public sealed class CalendarEventDraft
{
    public Guid? CalendarId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    /// <summary>The Details text beside the place (issue #383 §3.2) — meeting links, room numbers.</summary>
    public string Location { get; set; } = string.Empty;

    /// <summary>The linked place: the stored link, the picker's value and a staged create (issue #383).</summary>
    public PlaceDraft Place { get; set; } = new();

    public bool IsAllDay { get; set; }

    /// <summary>
    /// The linked library photos as the dialog shows them (issue #522 §3.1): the loaded links, then the user's adds and
    /// removes. Sent only when <see cref="PhotosChanged"/>; otherwise the save keeps whatever is stored.
    /// </summary>
    public List<JournalDraftPhoto> Photos { get; set; } = [];

    /// <summary>The linked files as the dialog shows them, mapped from the hydrated read (§E). Sent only when <see cref="FilesChanged"/>.</summary>
    public List<FileMetadataResponse> Attachments { get; set; } = [];

    /// <summary>The user added or removed a photo in this session.</summary>
    public bool PhotosChanged { get; set; }

    /// <summary>The user added or removed a file in this session.</summary>
    public bool FilesChanged { get; set; }

    /// <summary>
    /// What the save does with the links (issue #522 goal 5): a kind the user changed is sent whole, any other is kept —
    /// so a user who cannot see a kind, or changed nothing in it, never overwrites it.
    /// </summary>
    public AttachmentLinks Links => AttachmentLinks.Set(
        FilesChanged ? Attachments.Select(a => a.Id) : null,
        PhotosChanged ? Photos.Select(p => p.PhotoId) : null);

    /// <summary>
    /// The IANA zone the times are wall-clock in (issue #520 §3 "Time-zone field"); null is floating. Kept while All day
    /// is ticked — the field comes back with it when it is unticked (FR14) — and sent only for a timed event.
    /// </summary>
    public string? TimeZoneId { get; set; }

    public DateTime? StartDate { get; set; }
    public TimeSpan? StartTime { get; set; } = new(9, 0, 0);
    public DateTime? EndDate { get; set; }
    public TimeSpan? EndTime { get; set; } = new(10, 0, 0);

    public bool Repeats { get; set; }
    public RecurrenceFrequency Frequency { get; set; } = RecurrenceFrequency.Weekly;
    public int? Interval { get; set; } = 1;
    public DaysOfWeekFlags DaysOfWeek { get; set; }
    public int? DayOfMonth { get; set; }
    public int? MonthOfYear { get; set; } = 1;
    public string EndMode { get; set; } = "count"; // "date" | "count"
    public DateTime? RecurrenceEndDate { get; set; }
    public int? OccurrenceCount { get; set; } = 10;

    public string? TitleError { get; set; }
    public string? StartDateError { get; set; }
    public string? EndError { get; set; }
    public string? DaysError { get; set; }
    public string? DayOfMonthError { get; set; }
    public string? EndDateError { get; set; }
    public string? CountError { get; set; }

    /// <summary>The time-zone field's error: free text that matches no zone, or the server's <c>400</c> (issue #520 v3 §E).</summary>
    public string? TimeZoneError { get; set; }

    /// <summary>
    /// A new event on <paramref name="defaultDate"/>, in <paramref name="defaultZone"/>: the browser's zone, else
    /// <c>UTC</c> (issue #520 AC 1).
    /// </summary>
    public static CalendarEventDraft ForCreate(DateOnly? defaultDate, TimeSpan? defaultTime, Guid? defaultCalendarId, string defaultZone = "UTC")
    {
        var date = (defaultDate ?? DateOnly.FromDateTime(DateTime.Now)).ToDateTime(TimeOnly.MinValue);
        var start = defaultTime ?? new TimeSpan(9, 0, 0);
        // Seed BOTH ends together: a slot click at 14:00 must yield a valid 14:00–15:00 range, not
        // leave EndTime at the 10:00 field default (issue #329 round-2 regression fix).
        var end = start.Add(TimeSpan.FromHours(1));
        return new CalendarEventDraft
        {
            CalendarId = defaultCalendarId,
            TimeZoneId = defaultZone,
            StartDate = date,
            EndDate = date,
            StartTime = start,
            EndTime = end,
            DayOfMonth = date.Day,
            MonthOfYear = date.Month,
        };
    }

    /// <summary>
    /// The draft of a stored event. Its times are the read's wall-clock — in the event's own zone when it has one, so the
    /// day-of-month and month defaults are zone-local (#520 v3 §K) — and an all-day event's zone field comes back, when
    /// All day is unticked, in <paramref name="fallbackZone"/> (FR14).
    /// </summary>
    public static CalendarEventDraft FromEvent(ExistingCalendarEvent source, string fallbackZone = "UTC")
    {
        var start = source.StartDateTime;
        var end = source.EndDateTime;
        return new CalendarEventDraft
        {
            CalendarId = source.CalendarId,
            Title = source.Title,
            Description = source.Description ?? string.Empty,
            Location = source.Location ?? string.Empty,
            Place = PlaceDraft.For(source.LocationId, source.LocationRef),
            IsAllDay = source.IsAllDay,
            Photos = CalendarLinkRows.Photos(source.Photos),
            Attachments = CalendarLinkRows.Files(source.Attachments),
            TimeZoneId = source.IsAllDay ? fallbackZone : source.TimeZoneId,
            StartDate = start.Date,
            StartTime = start.TimeOfDay,
            EndDate = source.IsAllDay ? end.Date.AddDays(-1) : end.Date,
            EndTime = end.TimeOfDay,
            Repeats = source.RecurrencePatternId is not null,
            DayOfMonth = start.Day,
            MonthOfYear = start.Month,
        };
    }

    /// <inheritdoc cref="FromEvent"/>
    public static CalendarEventDraft FromPattern(ExistingRecurrencePattern source, string fallbackZone = "UTC")
    {
        var start = source.StartDateTime;
        var end = source.EndDateTime;
        return new CalendarEventDraft
        {
            CalendarId = source.CalendarId,
            Title = source.Title,
            Description = source.Description ?? string.Empty,
            Location = source.Location ?? string.Empty,
            Place = PlaceDraft.For(source.LocationId, source.LocationRef),
            IsAllDay = source.IsAllDay,
            Photos = CalendarLinkRows.Photos(source.Photos),
            Attachments = CalendarLinkRows.Files(source.Attachments),
            TimeZoneId = source.IsAllDay ? fallbackZone : source.TimeZoneId,
            StartDate = start.Date,
            StartTime = start.TimeOfDay,
            EndDate = source.IsAllDay ? end.Date.AddDays(-1) : end.Date,
            EndTime = end.TimeOfDay,
            Repeats = true,
            Frequency = source.Frequency,
            Interval = source.Interval,
            DaysOfWeek = source.DaysOfWeek ?? DaysOfWeekFlags.None,
            DayOfMonth = source.DayOfMonth ?? start.Day,
            MonthOfYear = source.MonthOfYear ?? start.Month,
            EndMode = source.RecurrenceEndDate is not null ? "date" : "count",
            RecurrenceEndDate = source.RecurrenceEndDate,
            OccurrenceCount = source.OccurrenceCount,
        };
    }

    /// <summary>Validates and populates the <c>*Error</c> fields. Returns whether the draft is valid.</summary>
    /// <param name="checkRule">Whether the repeat rule is part of this save: not for an occurrence, whose write carries no rule —
    /// its draft holds the default rule, which would otherwise refuse every occurrence save with a hidden "Pick at least one
    /// day" (found while building issue #520).</param>
    public bool Validate(bool checkRule = true)
    {
        TitleError = null;
        StartDateError = null;
        EndError = null;
        DaysError = null;
        DayOfMonthError = null;
        EndDateError = null;
        CountError = null;

        // The zone's error is set on blur or by the server and cleared only by picking a zone (issue #520 v3 §E).
        var valid = TimeZoneError is null || IsAllDay;

        if (string.IsNullOrWhiteSpace(Title))
        {
            TitleError = "Give the event a title.";
            valid = false;
        }

        if (CalendarId is null)
        {
            valid = false;
        }

        if (StartDate is null)
        {
            StartDateError = "Choose a start date.";
            valid = false;
        }

        if (EndDate is null)
        {
            EndError = "Choose an end date.";
            valid = false;
        }
        else if (StartDate is not null)
        {
            if (IsAllDay)
            {
                if (EndDate.Value.Date < StartDate.Value.Date)
                {
                    EndError = "End day is before the start day.";
                    valid = false;
                }
            }
            else
            {
                var start = StartDate.Value.Date + (StartTime ?? TimeSpan.Zero);
                var end = EndDate.Value.Date + (EndTime ?? TimeSpan.Zero);
                if (end <= start)
                {
                    EndError = "End must be after the start.";
                    valid = false;
                }
            }
        }

        if (Repeats && checkRule)
        {
            if (Frequency == RecurrenceFrequency.Weekly && DaysOfWeek == DaysOfWeekFlags.None)
            {
                DaysError = "Pick at least one day.";
                valid = false;
            }

            if ((Frequency == RecurrenceFrequency.Monthly || Frequency == RecurrenceFrequency.Yearly)
                && (DayOfMonth is null or < 1 or > 31))
            {
                DayOfMonthError = "Enter a day between 1 and 31.";
                valid = false;
            }

            if (EndMode == "count" && (OccurrenceCount is null or < 1))
            {
                CountError = "Enter how many times it repeats.";
                valid = false;
            }

            if (EndMode == "date" && (RecurrenceEndDate is null || (StartDate is not null && RecurrenceEndDate.Value.Date < StartDate.Value.Date)))
            {
                EndDateError = "End date must be on or after the start.";
                valid = false;
            }
        }

        return valid;
    }

    /// <summary>The zone a write carries: none for an all-day event (issue #520 Non-Goal 4).</summary>
    public string? WriteZone => IsAllDay ? null : TimeZoneId;

    /// <summary>The timed start as entered, wall-clock in <see cref="TimeZoneId"/>; null for all-day or before a date.</summary>
    public DateTime? StartWall => IsAllDay || StartDate is null ? null : StartDate.Value.Date + (StartTime ?? TimeSpan.Zero);

    /// <summary>The timed end as entered; null for all-day or before a date.</summary>
    public DateTime? EndWall => IsAllDay || EndDate is null ? null : EndDate.Value.Date + (EndTime ?? TimeSpan.Zero);

    /// <summary>Builds the (StartDateTime, EndDateTime) pair the API expects — all-day converts the
    /// inclusive UI end day to the exclusive midnight boundary the backend stores. Timed values are the entered
    /// wall-clock; <see cref="CalendarWrites"/> labels them for the zone.</summary>
    public (DateTime Start, DateTime End) BuildTimes()
    {
        if (IsAllDay)
        {
            var start = DateTime.SpecifyKind(StartDate!.Value.Date, DateTimeKind.Utc);
            var end = DateTime.SpecifyKind(EndDate!.Value.Date.AddDays(1), DateTimeKind.Utc);
            return (start, end);
        }

        var timedStart = DateTime.SpecifyKind(StartDate!.Value.Date + (StartTime ?? TimeSpan.Zero), DateTimeKind.Utc);
        var timedEnd = DateTime.SpecifyKind(EndDate!.Value.Date + (EndTime ?? TimeSpan.Zero), DateTimeKind.Utc);
        return (timedStart, timedEnd);
    }

    /// <summary>
    /// The event write. <paramref name="link"/> is required: a full replacement without it would unlink. The links travel
    /// as <see cref="Links"/>, which keeps every kind the user did not change (issue #522 goal 5).
    /// </summary>
    public NewCalendarEvent ToNewCalendarEvent(LocationLink link)
    {
        var (start, end) = BuildTimes();
        return CalendarWrites.Event(
            CalendarId!.Value,
            Title.Trim(),
            string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
            string.IsNullOrWhiteSpace(Location) ? null : Location.Trim(),
            start, end, IsAllDay, WriteZone, Links, link);
    }

    /// <summary>The series write. <paramref name="link"/> is required: a full replacement without it would unlink.</summary>
    public NewRecurrencePattern ToNewRecurrencePattern(LocationLink link)
    {
        var (start, end) = BuildTimes();
        var isWeekly = Frequency == RecurrenceFrequency.Weekly;
        var isMonthlyOrYearly = Frequency is RecurrenceFrequency.Monthly or RecurrenceFrequency.Yearly;
        var isYearly = Frequency == RecurrenceFrequency.Yearly;

        return CalendarWrites.Pattern(
            CalendarId!.Value,
            Title.Trim(),
            string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
            string.IsNullOrWhiteSpace(Location) ? null : Location.Trim(),
            start, end, IsAllDay, WriteZone,
            Frequency,
            Interval ?? 1,
            isWeekly ? DaysOfWeek : null,
            isMonthlyOrYearly ? DayOfMonth : null,
            isYearly ? MonthOfYear : null,
            EndMode == "date" ? RecurrenceEndDate : null,
            EndMode == "count" ? OccurrenceCount : null,
            Links, link);
    }
}
