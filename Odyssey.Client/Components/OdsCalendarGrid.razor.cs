using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;

namespace Odyssey.Client.Components;

public partial class OdsCalendarGrid
{
    private static readonly string[] MonthNames =
    [
        "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December",
    ];

    /// <summary>Any date inside the month to display.</summary>
    [Parameter, EditorRequired] public DateOnly Month { get; set; }

    [Parameter] public IReadOnlyList<CalendarEventVm> Events { get; set; } = [];

    /// <summary>Calendars whose events are drawn. Null = all events shown.</summary>
    [Parameter] public IReadOnlySet<Guid>? VisibleCalendarIds { get; set; }

    /// <summary>Empty-cell click → start a new event on that day.</summary>
    [Parameter] public EventCallback<DateOnly> OnDayClick { get; set; }

    /// <summary>Chip / popover-row click → open that event.</summary>
    [Parameter] public EventCallback<Guid> OnEventClick { get; set; }

    /// <summary>Drag a chip onto another day → reschedule. Enables drag when set.</summary>
    [Parameter] public EventCallback<(Guid Id, DateOnly ToDate, DateOnly FromDate)> OnEventDrop { get; set; }

    /// <summary>Max item rows before a cell collapses the remainder into "+N more".</summary>
    [Parameter] public int MaxPerDay { get; set; } = 3;

    /// <summary>0 = Sunday-first, 1 = Monday-first (default, matches the rest of the app).</summary>
    [Parameter] public int WeekStartsOn { get; set; } = 1;

    /// <summary>Override "today" — for tests. Defaults to the real today.</summary>
    [Parameter] public DateOnly? Today { get; set; }

    private DateOnly TodayDate => Today ?? DateOnly.FromDateTime(DateTime.Now);

    private List<DateOnly> _days = [];
    private DateOnly _focusDay;
    private DateOnly? _renderedMonth;
    private DateOnly? _popoverDay;
    private ElementReference[] _dayCellRefs = new ElementReference[42];
    private ElementReference _popoverRef;
    private bool _focusPopoverPending;
    private Guid? _draggingEventId;
    private DateOnly _dragFromDay;

    private string GridLabel => $"{MonthNames[Month.Month - 1]} {Month.Year} calendar";

    protected override void OnParametersSet()
    {
        var firstOfMonth = new DateOnly(Month.Year, Month.Month, 1);
        _days = [.. CalendarGridMath.DaysInGrid(Month, WeekStartsOn)];

        // Compare against the month we last rendered — not against `Month` (which is always the
        // incoming month, so the guard would never fire). When the displayed month changes, snap
        // focus back into the new grid so the roving-tabindex invariant holds (exactly one cell has
        // tabindex=0); otherwise `_focusDay` would point at a day no longer in `_days` and no cell
        // would be keyboard-reachable.
        if (_renderedMonth != firstOfMonth)
        {
            _renderedMonth = firstOfMonth;
            _focusDay = TodayDate.Month == Month.Month && TodayDate.Year == Month.Year ? TodayDate : firstOfMonth;
            _popoverDay = null;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusPopoverPending)
        {
            _focusPopoverPending = false;
            await _popoverRef.FocusAsync();
        }
    }

    private bool IsVisible(CalendarEventVm calendarEvent) =>
        VisibleCalendarIds is null || VisibleCalendarIds.Contains(calendarEvent.CalendarId);

    private List<DayItem> ItemsForDay(DateOnly day, int columnIndex)
    {
        var covering = new List<DayItem>();
        var timed = new List<DayItem>();

        foreach (var calendarEvent in Events)
        {
            if (!IsVisible(calendarEvent) || !CalendarGridMath.CoversDay(calendarEvent, day))
            {
                continue;
            }

            if (calendarEvent.IsAllDay)
            {
                covering.Add(new DayItem(
                    calendarEvent, AllDay: true,
                    IsStart: CalendarGridMath.IsStartDay(calendarEvent, day),
                    IsEnd: CalendarGridMath.IsEndDay(calendarEvent, day),
                    ShowLabel: CalendarGridMath.ShowsLabel(calendarEvent, day, columnIndex)));
            }
            else
            {
                timed.Add(new DayItem(calendarEvent, AllDay: false, IsStart: true, IsEnd: true, ShowLabel: true));
            }
        }

        covering.Sort((a, b) => a.Event.Start.CompareTo(b.Event.Start));
        timed.Sort((a, b) => a.Event.Start.CompareTo(b.Event.Start));
        covering.AddRange(timed);
        return covering;
    }

    private static string AccessibleName(CalendarEventVm calendarEvent)
    {
        var parts = new List<string> { calendarEvent.Title, calendarEvent.IsAllDay ? "all day" : calendarEvent.Start.ToString("HH:mm") };
        if (!string.IsNullOrEmpty(calendarEvent.CalendarName))
        {
            parts.Add($"{calendarEvent.CalendarName} calendar");
        }

        if (calendarEvent.Recurring)
        {
            parts.Add("recurring");
        }

        return string.Join(", ", parts);
    }

    private static string LongDay(DateOnly day) => $"{MonthNames[day.Month - 1]} {day.Day}, {day.Year}";

    private string DayAriaLabel(DateOnly day, int eventCount) =>
        eventCount == 0
            ? $"{LongDay(day)}, no events"
            : $"{LongDay(day)}, {eventCount} {(eventCount == 1 ? "event" : "events")}";

    private static string DayCellClass(bool inMonth, bool isToday, DateOnly day)
    {
        var classes = new List<string> { "odc-cal-day" };
        if (!inMonth)
        {
            classes.Add("muted");
        }

        if (isToday)
        {
            classes.Add("today");
        }

        return string.Join(' ', classes);
    }

    private string ChipClass(DayItem item, int columnIndex)
    {
        var classes = new List<string> { "odc-cal-chip" };
        if (item.AllDay)
        {
            classes.Add("allday");
            if (!(item.IsStart || columnIndex == 0))
            {
                classes.Add("cont-l");
            }

            if (!(item.IsEnd || columnIndex == 6))
            {
                classes.Add("cont-r");
            }
        }
        else
        {
            classes.Add("timed");
        }

        if (_draggingEventId == item.Event.Id)
        {
            classes.Add("dragging");
        }

        return string.Join(' ', classes);
    }

    private static string ChipStyle(DayItem item) =>
        item.AllDay ? $"--chip:{item.Event.Color};--chip-fg:{item.Event.Fg}" : $"--chip:{item.Event.Color}";

    private async Task Move(DateOnly day)
    {
        _focusDay = day;
        StateHasChanged();
        var index = _days.IndexOf(day);
        if (index >= 0)
        {
            await Task.Yield(); // let the render commit the new tabindex before focusing
            await _dayCellRefs[index].FocusAsync();
        }
    }

    private async Task OnCellKeyDown(KeyboardEventArgs e, DateOnly day)
    {
        switch (e.Key)
        {
            case "ArrowLeft":
                await Move(day.AddDays(-1));
                break;
            case "ArrowRight":
                await Move(day.AddDays(1));
                break;
            case "ArrowUp":
                await Move(day.AddDays(-7));
                break;
            case "ArrowDown":
                await Move(day.AddDays(7));
                break;
            case "Home":
                await Move(CalendarGridMath.StartOfWeek(day, WeekStartsOn));
                break;
            case "End":
                await Move(CalendarGridMath.StartOfWeek(day, WeekStartsOn).AddDays(6));
                break;
            case "Enter":
            case " ":
                await ActivateDay(day);
                break;
        }
    }

    private async Task OnDayCellClick(DateOnly day) => await ActivateDay(day);

    private async Task ActivateDay(DateOnly day)
    {
        var items = ItemsForDay(day, _days.IndexOf(day) % 7);
        if (items.Count > 0)
        {
            OpenPopover(day);
        }
        else if (OnDayClick.HasDelegate)
        {
            await OnDayClick.InvokeAsync(day);
        }
    }

    private void OpenPopover(DateOnly day)
    {
        _popoverDay = day;
        _focusPopoverPending = true; // move focus into the dialog once it renders
    }

    private void ClosePopover() => _popoverDay = null;

    private async Task OnPopoverKeyDown(KeyboardEventArgs e, DateOnly day)
    {
        if (e.Key is "Escape" or "Esc")
        {
            _popoverDay = null;
            await Move(day); // dismiss and return focus to the day cell that opened it
        }
    }

    private async Task OnPopoverRowClick(Guid eventId)
    {
        _popoverDay = null;
        if (OnEventClick.HasDelegate)
        {
            await OnEventClick.InvokeAsync(eventId);
        }
    }

    private async Task OnPopoverNewEvent(DateOnly day)
    {
        _popoverDay = null;
        if (OnDayClick.HasDelegate)
        {
            await OnDayClick.InvokeAsync(day);
        }
    }

    private async Task OnChipClick(Guid eventId)
    {
        if (OnEventClick.HasDelegate)
        {
            await OnEventClick.InvokeAsync(eventId);
        }
    }

    private void OnChipDragStart(Guid eventId, DateOnly fromDay)
    {
        _draggingEventId = eventId;
        _dragFromDay = fromDay;
    }

    private void OnChipDragEnd()
    {
        _draggingEventId = null;
    }

    private void OnDayDragOver(DragEventArgs e, DateOnly day)
    {
        // preventDefault (via the directive above) is what allows a drop here at all.
    }

    private async Task OnDayDrop(DateOnly toDay)
    {
        var eventId = _draggingEventId;
        var fromDay = _dragFromDay;
        _draggingEventId = null;

        if (eventId is { } id && fromDay != toDay && OnEventDrop.HasDelegate)
        {
            await OnEventDrop.InvokeAsync((id, toDay, fromDay));
        }
    }

    private sealed record DayItem(CalendarEventVm Event, bool AllDay, bool IsStart, bool IsEnd, bool ShowLabel);
}
