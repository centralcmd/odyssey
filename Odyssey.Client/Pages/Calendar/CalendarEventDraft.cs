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
    public string Location { get; set; } = string.Empty;
    public bool IsAllDay { get; set; }
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

    public static CalendarEventDraft ForCreate(DateOnly? defaultDate, TimeSpan? defaultTime, Guid? defaultCalendarId)
    {
        var date = (defaultDate ?? DateOnly.FromDateTime(DateTime.Now)).ToDateTime(TimeOnly.MinValue);
        var start = defaultTime ?? new TimeSpan(9, 0, 0);
        // Seed BOTH ends together: a slot click at 14:00 must yield a valid 14:00–15:00 range, not
        // leave EndTime at the 10:00 field default (issue #329 round-2 regression fix).
        var end = start.Add(TimeSpan.FromHours(1));
        return new CalendarEventDraft
        {
            CalendarId = defaultCalendarId,
            StartDate = date,
            EndDate = date,
            StartTime = start,
            EndTime = end,
            DayOfMonth = date.Day,
            MonthOfYear = date.Month,
        };
    }

    public static CalendarEventDraft FromEvent(ExistingCalendarEvent source)
    {
        var start = source.StartDateTime;
        var end = source.EndDateTime;
        return new CalendarEventDraft
        {
            CalendarId = source.CalendarId,
            Title = source.Title,
            Description = source.Description ?? string.Empty,
            Location = source.Location ?? string.Empty,
            IsAllDay = source.IsAllDay,
            StartDate = start.Date,
            StartTime = start.TimeOfDay,
            EndDate = source.IsAllDay ? end.Date.AddDays(-1) : end.Date,
            EndTime = end.TimeOfDay,
            Repeats = source.RecurrencePatternId is not null,
            DayOfMonth = start.Day,
            MonthOfYear = start.Month,
        };
    }

    public static CalendarEventDraft FromPattern(ExistingRecurrencePattern source)
    {
        var start = source.StartDateTime;
        var end = source.EndDateTime;
        return new CalendarEventDraft
        {
            CalendarId = source.CalendarId,
            Title = source.Title,
            Description = source.Description ?? string.Empty,
            Location = source.Location ?? string.Empty,
            IsAllDay = source.IsAllDay,
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
    public bool Validate()
    {
        TitleError = null;
        StartDateError = null;
        EndError = null;
        DaysError = null;
        DayOfMonthError = null;
        EndDateError = null;
        CountError = null;

        var valid = true;

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

        if (Repeats)
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

    /// <summary>Builds the (StartDateTime, EndDateTime) pair the API expects — all-day converts the
    /// inclusive UI end day to the exclusive UTC-midnight boundary the backend stores.</summary>
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

    public NewCalendarEvent ToNewCalendarEvent()
    {
        var (start, end) = BuildTimes();
        return new NewCalendarEvent
        {
            CalendarId = CalendarId!.Value,
            Title = Title.Trim(),
            Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
            Location = string.IsNullOrWhiteSpace(Location) ? null : Location.Trim(),
            StartDateTime = start,
            EndDateTime = end,
            IsAllDay = IsAllDay,
        };
    }

    public NewRecurrencePattern ToNewRecurrencePattern()
    {
        var (start, end) = BuildTimes();
        var isWeekly = Frequency == RecurrenceFrequency.Weekly;
        var isMonthlyOrYearly = Frequency is RecurrenceFrequency.Monthly or RecurrenceFrequency.Yearly;
        var isYearly = Frequency == RecurrenceFrequency.Yearly;

        return new NewRecurrencePattern
        {
            CalendarId = CalendarId!.Value,
            Title = Title.Trim(),
            Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
            Location = string.IsNullOrWhiteSpace(Location) ? null : Location.Trim(),
            StartDateTime = start,
            EndDateTime = end,
            IsAllDay = IsAllDay,
            Frequency = Frequency,
            Interval = Interval ?? 1,
            DaysOfWeek = isWeekly ? DaysOfWeek : null,
            DayOfMonth = isMonthlyOrYearly ? DayOfMonth : null,
            MonthOfYear = isYearly ? MonthOfYear : null,
            RecurrenceEndDate = EndMode == "date" ? RecurrenceEndDate : null,
            OccurrenceCount = EndMode == "count" ? OccurrenceCount : null,
        };
    }
}
