using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;

namespace Odyssey.Client.Components;

public partial class OdsCalTimeGrid
{
    private const int HourPx = 46;
    private const int GutterPx = 56;
    private const int SnapMin = 15;
    private const double DragThresholdPx = 5;

    /// <summary>The days to render — 1 for Day view, 7 for Week view (in display order).</summary>
    [Parameter, EditorRequired] public IReadOnlyList<DateOnly> Days { get; set; } = [];

    [Parameter] public IReadOnlyList<CalendarEventVm> Events { get; set; } = [];

    /// <summary>Calendars whose events are drawn. Null = all events shown.</summary>
    [Parameter] public IReadOnlySet<Guid>? VisibleCalendarIds { get; set; }

    /// <summary>Override "today" — for tests. Defaults to the real today.</summary>
    [Parameter] public DateOnly? Today { get; set; }

    /// <summary>Gates empty-slot-click creation (calendar.create).</summary>
    [Parameter] public bool CanCreate { get; set; }

    /// <summary>Gates drag/resize + all-day drop (calendar.update).</summary>
    [Parameter] public bool CanUpdate { get; set; }

    /// <summary>Empty-slot click/Enter → start a new event at that day + time.</summary>
    [Parameter] public EventCallback<(DateOnly Day, TimeSpan Time)> OnSlotClick { get; set; }

    /// <summary>Event block / all-day chip click → open that event.</summary>
    [Parameter] public EventCallback<Guid> OnEventClick { get; set; }

    /// <summary>Timed drag/resize commit → reschedule to a new start/end (UTC wall-clock).</summary>
    [Parameter] public EventCallback<(Guid Id, DateTime NewStart, DateTime NewEnd)> OnEventChange { get; set; }

    /// <summary>All-day chip dropped on another day → whole-day shift (same contract as the Month view).</summary>
    [Parameter] public EventCallback<(Guid Id, DateOnly ToDate, DateOnly FromDate)> OnEventDrop { get; set; }

    private readonly string _instructionsId = $"cal-tg-help-{Guid.NewGuid():N}";
    private List<DateOnly> _days = [];
    private ElementReference[] _slotRefs = [];
    private ElementReference _bodyRef;
    private ElementReference _scrollRef;
    private int _focusedSlot;
    private bool _scrolledToNow;
    private bool _focusSlotPending;

    private Guid? _allDayDragId;
    private DateOnly _allDayFromDay;
    private DragState? _drag;
    private bool _suppressNextClick;

    private IJSObjectReference? _module;

    private DateOnly TodayDate => Today ?? DateOnly.FromDateTime(DateTime.Now);

    private string HeadColumns => $"{GutterPx}px repeat({_days.Count}, 1fr)";

    protected override void OnParametersSet()
    {
        _days = [.. Days];
        if (_slotRefs.Length != _days.Count * 24)
        {
            _slotRefs = new ElementReference[_days.Count * 24];
            var todayCol = _days.IndexOf(TodayDate);
            _focusedSlot = ((todayCol < 0 ? 0 : todayCol)) + (8 * _days.Count); // 08:00 of today (or day 1)
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _module = await JS.InvokeAsync<IJSObjectReference>("import", "./js/cal-timegrid.js");
        }

        if (!_scrolledToNow && _module is not null)
        {
            _scrolledToNow = true;
            var nowMin = DateTime.Now.TimeOfDay.TotalMinutes;
            var top = Math.Max(0, (nowMin / 60 * HourPx) - 12);
            await _module.InvokeVoidAsync("scrollTo", _scrollRef, top);
        }

        if (_focusSlotPending)
        {
            _focusSlotPending = false;
            if (_focusedSlot >= 0 && _focusedSlot < _slotRefs.Length)
            {
                await _slotRefs[_focusedSlot].FocusAsync();
            }
        }
    }

    private bool IsVisible(CalendarEventVm ev) => VisibleCalendarIds is null || VisibleCalendarIds.Contains(ev.CalendarId);

    private IEnumerable<CalendarEventVm> AllDayFor(DateOnly day) =>
        Events.Where(e => IsVisible(e) && e.IsAllDay && CalendarGridMath.CoversDay(e, day))
              .OrderBy(e => e.Start);

    // Timed events touching this day, laid out into greedy overlap columns; each carries its
    // clipped-to-day start/end minutes so a cross-midnight event fills the right slice per day.
    private List<Placed> LayoutDay(DateOnly day)
    {
        var dayStart = day.ToDateTime(TimeOnly.MinValue);
        var list = Events
            .Where(e => IsVisible(e) && !e.IsAllDay && CalendarGridMath.TimeGridOverlapsDay(e, day))
            .OrderBy(e => e.Start)
            .Select(e =>
            {
                var s = Math.Clamp((e.Start - dayStart).TotalMinutes, 0, 1440);
                var en = Math.Clamp((e.End - dayStart).TotalMinutes, 0, 1440);
                return (e, s, en: Math.Max(s + 20, en), clipped: CalendarGridMath.CrossesMidnight(e));
            })
            .ToList();

        var colEnds = new List<double>();
        var placed = new List<Placed>();
        foreach (var (e, s, en, clipped) in list)
        {
            var ci = colEnds.FindIndex(end => end <= s);
            if (ci == -1)
            {
                ci = colEnds.Count;
                colEnds.Add(en);
            }
            else
            {
                colEnds[ci] = en;
            }

            placed.Add(new Placed(e, s, en, ci, clipped));
        }

        var ncol = Math.Max(1, colEnds.Count);
        return [.. placed.Select(pl => pl with { Ncol = ncol })];
    }

    private string BlockStyle(Placed b)
    {
        var top = b.Start / 60 * HourPx;
        var height = Math.Max(20, ((b.End - b.Start) / 60 * HourPx) - 2);
        var left = (double)b.Col / b.Ncol * 100;
        var width = 100.0 / b.Ncol;
        return $"top:{F(top)}px;height:{F(height)}px;left:calc({F(left)}% + 2px);width:calc({F(width)}% - 4px);--chip:{b.Event.Color}";
    }

    private string GhostStyle(DragState d)
    {
        var top = d.CurStart / 60 * HourPx;
        var height = Math.Max(20, ((d.CurEnd - d.CurStart) / 60 * HourPx) - 2);
        return $"top:{F(top)}px;height:{F(height)}px;" +
            $"left:calc({GutterPx}px + {d.CurDi} * (100% - {GutterPx}px) / {_days.Count} + 2px);" +
            $"width:calc((100% - {GutterPx}px) / {_days.Count} - 4px);--chip:{d.Color}";
    }

    private static string F(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static string Hhmm(double minutes) => $"{(int)minutes / 60:D2}:{(int)minutes % 60:D2}";

    private static double Snap(double minutes) => Math.Round(minutes / SnapMin) * SnapMin;

    private string SlotLabel(DateOnly day, int hour) => $"{day:dddd MMMM d}, {hour:D2}:00";

    private static string AllDayName(CalendarEventVm ev)
    {
        var name = string.IsNullOrEmpty(ev.CalendarName) ? ev.Title : $"{ev.Title}, {ev.CalendarName} calendar";
        return ev.Recurring ? $"{name}, all day, recurring" : $"{name}, all day";
    }

    private static string TimedName(CalendarEventVm ev)
    {
        var parts = new List<string> { ev.Title, $"{ev.Start:HH:mm} to {ev.End:HH:mm}" };
        if (!string.IsNullOrEmpty(ev.CalendarName))
        {
            parts.Add($"{ev.CalendarName} calendar");
        }

        if (ev.Recurring)
        {
            parts.Add("recurring");
        }

        return string.Join(", ", parts);
    }

    // ── Empty-slot creation (mouse: precise time from click Y; keyboard: the slot's hour) ───────────
    private async Task OnColumnClick(MouseEventArgs e, DateOnly day)
    {
        if (_suppressNextClick)
        {
            _suppressNextClick = false;
            return;
        }

        // OffsetY is relative to the column; snap the clicked position to the nearest half hour.
        var mins = Math.Floor(e.OffsetY / HourPx * 60 / 30) * 30;
        mins = Math.Clamp(mins, 0, 23 * 60 + 30);
        await OnSlotActivate(day, TimeSpan.FromMinutes(mins));
    }

    private async Task OnSlotActivate(DateOnly day, TimeSpan time)
    {
        if (CanCreate && OnSlotClick.HasDelegate)
        {
            await OnSlotClick.InvokeAsync((day, time));
        }
    }

    private async Task OnEventActivate(Guid id)
    {
        if (_suppressNextClick)
        {
            _suppressNextClick = false;
            return;
        }

        if (OnEventClick.HasDelegate)
        {
            await OnEventClick.InvokeAsync(id);
        }
    }

    // ── Keyboard grid (structural slot layer) ───────────────────────────────────────────────────────
    private async Task OnSlotKeyDown(KeyboardEventArgs e, int di, int hour)
    {
        switch (e.Key)
        {
            case "ArrowLeft":
                await MoveSlot(di - 1, hour);
                break;
            case "ArrowRight":
                await MoveSlot(di + 1, hour);
                break;
            case "ArrowUp":
                await MoveSlot(di, hour - 1);
                break;
            case "ArrowDown":
                await MoveSlot(di, hour + 1);
                break;
            case "Home":
                await MoveSlot(di, 0);
                break;
            case "End":
                await MoveSlot(di, 23);
                break;
            case "Enter":
            case " ":
                await OnSlotActivate(_days[di], TimeSpan.FromHours(hour));
                break;
        }
    }

    private async Task MoveSlot(int di, int hour)
    {
        di = Math.Clamp(di, 0, _days.Count - 1);
        hour = Math.Clamp(hour, 0, 23);
        _focusedSlot = (hour * _days.Count) + di;
        _focusSlotPending = true;
        StateHasChanged();
        await Task.Yield();
    }

    // ── All-day drag (HTML5, whole-day shift — same contract as the Month view) ──────────────────────
    private void OnAllDayDragStart(Guid id, DateOnly fromDay)
    {
        _allDayDragId = id;
        _allDayFromDay = fromDay;
    }

    private async Task OnAllDayDrop(DateOnly toDay)
    {
        var id = _allDayDragId;
        var fromDay = _allDayFromDay;
        _allDayDragId = null;
        if (id is { } eventId && fromDay != toDay && OnEventDrop.HasDelegate && CanUpdate)
        {
            await OnEventDrop.InvokeAsync((eventId, toDay, fromDay));
        }
    }

    // ── Timed drag/resize (pointer events) ───────────────────────────────────────────────────────────
    private async Task BeginDrag(PointerEventArgs e, CalendarEventVm ev, DragMode mode)
    {
        if (!CanUpdate || e.Button != 0 || CalendarGridMath.CrossesMidnight(ev) || _module is null)
        {
            return;
        }

        var rect = await _module.InvokeAsync<Rect>("bodyRect", _bodyRef);
        if (rect is null)
        {
            return;
        }

        await _module.InvokeVoidAsync("capture", _bodyRef, e.PointerId);

        var startDay = DateOnly.FromDateTime(ev.Start);
        var dayStart = startDay.ToDateTime(TimeOnly.MinValue);
        var s = (ev.Start - dayStart).TotalMinutes;
        var en = Math.Max(s + SnapMin, (ev.End - dayStart).TotalMinutes);
        var di = Math.Max(0, _days.IndexOf(startDay));

        _drag = new DragState
        {
            Id = ev.Id,
            Mode = mode,
            BaseStart = s,
            BaseEnd = en,
            Dur = en - s,
            GrabY = e.ClientY,
            GrabDi = di,
            CurStart = s,
            CurEnd = en,
            CurDi = di,
            Color = ev.Color,
            RectLeft = rect.Left,
            RectWidth = rect.Width,
        };
    }

    private void OnBodyPointerMove(PointerEventArgs e)
    {
        if (_drag is not { } d)
        {
            return;
        }

        var delta = Snap((e.ClientY - d.GrabY) / HourPx * 60);
        if (Math.Abs(e.ClientY - d.GrabY) > DragThresholdPx)
        {
            d.Moved = true;
        }

        if (d.Mode == DragMode.Move)
        {
            var colW = (d.RectWidth - GutterPx) / _days.Count;
            var di = Math.Clamp((int)Math.Floor((e.ClientX - d.RectLeft - GutterPx) / colW), 0, _days.Count - 1);
            if (di != d.GrabDi)
            {
                d.Moved = true;
            }

            var cs = Math.Clamp(d.BaseStart + delta, 0, 1440 - d.Dur);
            d.CurStart = cs;
            d.CurEnd = cs + d.Dur;
            d.CurDi = di;
        }
        else
        {
            d.CurStart = d.BaseStart;
            d.CurEnd = Math.Clamp(d.BaseEnd + delta, d.BaseStart + SnapMin, 1440);
            d.CurDi = d.GrabDi;
        }

        StateHasChanged();
    }

    private async Task OnBodyPointerUp(PointerEventArgs e)
    {
        if (_drag is not { } d)
        {
            return;
        }

        if (_module is not null)
        {
            await _module.InvokeVoidAsync("release", _bodyRef, e.PointerId);
        }

        var moved = d.Moved;
        _drag = null;

        if (moved && CanUpdate && OnEventChange.HasDelegate)
        {
            var day = _days[d.CurDi].ToDateTime(TimeOnly.MinValue);
            var newStart = DateTime.SpecifyKind(day.AddMinutes(d.CurStart), DateTimeKind.Utc);
            var newEnd = DateTime.SpecifyKind(day.AddMinutes(d.CurEnd), DateTimeKind.Utc);
            _suppressNextClick = true; // Pointer Events still synthesize a click after a real drag.
            await OnEventChange.InvokeAsync((d.Id, newStart, newEnd));
        }

        StateHasChanged();
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // circuit already gone
            }
        }
    }

    private sealed record Placed(CalendarEventVm Event, double Start, double End, int Col, bool Clipped)
    {
        public int Ncol { get; init; } = 1;
    }

    private enum DragMode { Move, Resize }

    private sealed class DragState
    {
        public Guid Id { get; init; }
        public DragMode Mode { get; init; }
        public double BaseStart { get; init; }
        public double BaseEnd { get; init; }
        public double Dur { get; init; }
        public double GrabY { get; init; }
        public int GrabDi { get; init; }
        public double CurStart { get; set; }
        public double CurEnd { get; set; }
        public int CurDi { get; set; }
        public string Color { get; init; } = "";
        public double RectLeft { get; init; }
        public double RectWidth { get; init; }
        public bool Moved { get; set; }
    }

    private sealed class Rect
    {
        public double Top { get; set; }
        public double Left { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }
}
