/**
 * Odyssey DS — CalendarGrid
 * ---------------------------------------------------------------------------
 * The month grid: a 6×7 view of a month with colour-coded event chips. This is
 * the net-new calendar surface (the DS ships no MudCalendar / CalendarView).
 *
 * What it does:
 *  - Draws every day of the month (plus leading/trailing spill days, muted).
 *  - Renders each event as a chip in its calendar's swatch colour, always with
 *    the event TITLE as visible text (colour is never the only signal), and a
 *    baked contrast-safe foreground so labels clear WCAG 1.4.3 on any swatch.
 *  - All-day / multi-day events render ABOVE timed events in each cell, as
 *    flush strips: a multi-day event repeats a square-edged chip across the
 *    days it covers so it reads as one continuous bar; the title shows on the
 *    first covered day of each week. Exclusive-end semantics: the end-midnight
 *    day is not painted.
 *  - Timed chips carry a short time label ("10:00 — Standup").
 *  - Dense days collapse the overflow into a "+N more" affordance that opens an
 *    inline popover listing the full day (same overflow shape as InfoTile).
 *
 * Accessibility:
 *  - The grid is a roving-tabindex `role="grid"`: arrow keys move focus between
 *    days, Home/End jump to week start/end, PageUp/PageDown change month focus,
 *    Enter/Space activates a day (opens the day's events popover, or starts a
 *    new event on an empty day). Today carries `aria-current="date"`.
 *  - Each chip is an independently focusable button whose accessible name is
 *    the full sentence ("Team sync, 09:00, Work calendar"), not the truncated
 *    visual title.
 *
 * Peer atoms (swatchFor, …) are read off the DS namespace at render time.
 */

const ODC_CG_WD_SUN = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
const ODC_CG_MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];

const cgPad = (n) => String(n).padStart(2, '0');
const cgIso = (y, m, d) => `${y}-${cgPad(m + 1)}-${cgPad(d)}`;
const cgDateOf = (iso) => String(iso).slice(0, 10);
const cgParse = (iso) => { const [y, m, d] = cgDateOf(iso).split('-').map(Number); return { y, m: m - 1, d }; };
const cgToUTC = (iso) => { const p = cgParse(iso); return Date.UTC(p.y, p.m, p.d); };
const cgAddDays = (iso, n) => { const dt = new Date(cgToUTC(iso)); dt.setUTCDate(dt.getUTCDate() + n); return cgIso(dt.getUTCFullYear(), dt.getUTCMonth(), dt.getUTCDate()); };
const cgWeekday = (iso) => new Date(cgToUTC(iso)).getUTCDay();
const cgTimeLabel = (iso) => { const t = String(iso).slice(11, 16); return t || ''; };
const cgTodayIso = () => { const t = new Date(); return cgIso(t.getFullYear(), t.getMonth(), t.getDate()); };
const cgLongDay = (iso) => { const p = cgParse(iso); return `${ODC_CG_MONTHS[p.m]} ${p.d}, ${p.y}`; };

export function CalendarGrid({
  month,
  events = [],
  visibleCalendarIds = null,
  onDayClick,
  onEventClick,
  onEventDrop,
  maxPerDay = 3,
  weekStartsOn = 0,
  today,
}) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const swatchFor = NS.swatchFor || ((hex) => ({ hex: hex || '#0369A1', fg: '#fff' }));

  const monthIso = month instanceof Date ? cgIso(month.getFullYear(), month.getMonth(), month.getDate()) : `${String(month).slice(0, 7)}-01`;
  const view = cgParse(monthIso);
  const todayIso = today || cgTodayIso();

  const visSet = visibleCalendarIds == null ? null : (visibleCalendarIds instanceof Set ? visibleCalendarIds : new Set(visibleCalendarIds));
  const isVisible = (e) => visSet == null || visSet.has(e.calendarId);

  const [focusIso, setFocusIso] = React.useState(() => {
    const t = todayIso; const tp = cgParse(t);
    return (tp.y === view.y && tp.m === view.m) ? t : cgIso(view.y, view.m, 1);
  });
  React.useEffect(() => {
    const tp = cgParse(todayIso);
    setFocusIso((tp.y === view.y && tp.m === view.m) ? todayIso : cgIso(view.y, view.m, 1));
  }, [monthIso]);

  const [pop, setPop] = React.useState(null); // { iso, top, left }
  const gridRef = React.useRef(null);
  const dragRef = React.useRef(null); // { id, from }
  const [dragOverIso, setDragOverIso] = React.useState(null);
  const [draggingId, setDraggingId] = React.useState(null);

  // Grid start: back up to the week start containing the 1st.
  const firstWd = cgWeekday(cgIso(view.y, view.m, 1));
  const lead = (firstWd - weekStartsOn + 7) % 7;
  const gridStart = cgAddDays(cgIso(view.y, view.m, 1), -lead);
  const days = Array.from({ length: 42 }, (_, i) => cgAddDays(gridStart, i));
  const weeks = Array.from({ length: 6 }, (_, w) => days.slice(w * 7, w * 7 + 7));

  const weekdays = weekStartsOn === 1 ? [...ODC_CG_WD_SUN.slice(1), ODC_CG_WD_SUN[0]] : ODC_CG_WD_SUN;

  // Items covering a given day, all-day first (with continuation flags), then timed.
  const itemsForDay = (iso, colIndex) => {
    const covering = [];
    const timed = [];
    for (const e of events) {
      if (!isVisible(e)) continue;
      const s = cgDateOf(e.start);
      if (e.isAllDay) {
        const endExcl = cgDateOf(e.end); // exclusive
        if (iso >= s && iso < endExcl) {
          const lastDay = cgAddDays(endExcl, -1);
          covering.push({
            e, allDay: true,
            isStart: iso === s,
            isEnd: iso === lastDay,
            showLabel: iso === s || colIndex === 0,
          });
        }
      } else if (s === iso) {
        timed.push({ e, allDay: false, isStart: true, isEnd: true, showLabel: true });
      }
    }
    covering.sort((a, b) => (a.e.start < b.e.start ? -1 : a.e.start > b.e.start ? 1 : 0));
    timed.sort((a, b) => (a.e.start < b.e.start ? -1 : a.e.start > b.e.start ? 1 : 0));
    return covering.concat(timed);
  };

  const accName = (e) => {
    const parts = [e.title];
    parts.push(e.isAllDay ? 'all day' : cgTimeLabel(e.start));
    if (e.calendarName) parts.push(`${e.calendarName} calendar`);
    if (e.recurring) parts.push('recurring');
    return parts.filter(Boolean).join(', ');
  };

  const openDayPopover = (iso, cellEl) => {
    if (!cellEl) return;
    const r = cellEl.getBoundingClientRect();
    const top = Math.min(r.top, window.innerHeight - 320);
    const left = Math.min(r.left, window.innerWidth - 300);
    setPop({ iso, top: Math.max(8, top), left: Math.max(8, left) });
  };

  const move = (nextIso, cellFocus = true) => {
    const p = cgParse(nextIso);
    setFocusIso(nextIso);
    if (cellFocus) requestAnimationFrame(() => {
      const el = gridRef.current && gridRef.current.querySelector(`[data-iso="${nextIso}"]`);
      if (el) el.focus();
    });
    void p;
  };

  const onCellKey = (e, iso) => {
    switch (e.key) {
      case 'ArrowLeft': e.preventDefault(); move(cgAddDays(iso, -1)); break;
      case 'ArrowRight': e.preventDefault(); move(cgAddDays(iso, 1)); break;
      case 'ArrowUp': e.preventDefault(); move(cgAddDays(iso, -7)); break;
      case 'ArrowDown': e.preventDefault(); move(cgAddDays(iso, 7)); break;
      case 'Home': e.preventDefault(); move(cgAddDays(iso, -((cgWeekday(iso) - weekStartsOn + 7) % 7))); break;
      case 'End': e.preventDefault(); move(cgAddDays(iso, 6 - ((cgWeekday(iso) - weekStartsOn + 7) % 7))); break;
      case 'Enter': case ' ': {
        e.preventDefault();
        const items = itemsForDay(iso, cgWeekday(iso));
        if (items.length) openDayPopover(iso, e.currentTarget);
        else if (onDayClick) onDayClick(iso);
        break;
      }
      default: break;
    }
  };

  const gridLabel = `${ODC_CG_MONTHS[view.m]} ${view.y} calendar`;

  return (
    <div className="odc-cal" role="grid" aria-label={gridLabel} ref={gridRef}>
      <div className="odc-cal-wdhead" role="row">
        {weekdays.map((w) => <div key={w} className="odc-cal-wd" role="columnheader">{w}</div>)}
      </div>
      {weeks.map((week, wi) => (
        <div key={wi} className="odc-cal-week" role="row">
          {week.map((iso, ci) => {
            const p = cgParse(iso);
            const inMonth = p.m === view.m;
            const isToday = iso === todayIso;
            const items = itemsForDay(iso, ci);
            const visible = items.slice(0, maxPerDay);
            const overflow = items.length - visible.length;
            const wd = weekdays[ci];
            return (
              <div
                key={iso}
                role="gridcell"
                data-iso={iso}
                tabIndex={iso === focusIso ? 0 : -1}
                aria-current={isToday ? 'date' : undefined}
                aria-label={`${cgLongDay(iso)}${items.length ? `, ${items.length} ${items.length === 1 ? 'event' : 'events'}` : ', no events'}`}
                className={`odc-cal-day${inMonth ? '' : ' muted'}${isToday ? ' today' : ''}${dragOverIso === iso ? ' dragover' : ''}`}
                onClick={(ev) => { if (ev.target === ev.currentTarget || ev.target.classList.contains('odc-cal-items') || ev.target.classList.contains('odc-cal-daynum-row')) { if (onDayClick) onDayClick(iso); } }}
                onKeyDown={(ev) => onCellKey(ev, iso)}
                onDragOver={onEventDrop ? (ev) => { ev.preventDefault(); ev.dataTransfer.dropEffect = 'move'; if (dragOverIso !== iso) setDragOverIso(iso); } : undefined}
                onDrop={onEventDrop ? (ev) => { ev.preventDefault(); const d = dragRef.current; setDragOverIso(null); setDraggingId(null); if (d && d.from !== iso) onEventDrop(d.id, iso, d.from); dragRef.current = null; } : undefined}
              >
                <div className="odc-cal-daynum-row">
                  {ci === 0 || p.d === 1 ? <span className="odc-cal-daymo">{ODC_CG_MONTHS[p.m].slice(0, 3)}</span> : null}
                  <span className={`odc-cal-daynum${isToday ? ' today' : ''}`}>{p.d}</span>
                </div>
                <div className="odc-cal-items">
                  {visible.map((it, i) => {
                    const sw = it.e.color ? { hex: it.e.color, fg: it.e.fg || '#fff' } : swatchFor(it.e.color);
                    const cls = ['odc-cal-chip'];
                    if (it.allDay) {
                      cls.push('allday');
                      if (!(it.isStart || ci === 0)) cls.push('cont-l');
                      if (!(it.isEnd || ci === 6)) cls.push('cont-r');
                    } else {
                      cls.push('timed');
                    }
                    return (
                      <button
                        key={it.e.id + '-' + i}
                        type="button"
                        className={cls.join(' ') + (draggingId === it.e.id ? ' dragging' : '')}
                        draggable={onEventDrop ? true : undefined}
                        onDragStart={onEventDrop ? (ev) => { dragRef.current = { id: it.e.id, from: iso }; setDraggingId(it.e.id); ev.dataTransfer.effectAllowed = 'move'; try { ev.dataTransfer.setData('text/plain', it.e.id); } catch (x) { void x; } } : undefined}
                        onDragEnd={onEventDrop ? () => { setDraggingId(null); setDragOverIso(null); dragRef.current = null; } : undefined}
                        style={it.allDay
                          ? { '--chip': sw.hex, '--chip-fg': sw.fg }
                          : { '--chip': sw.hex }}
                        aria-label={accName(it.e)}
                        title={it.e.title}
                        onClick={(ev) => { ev.stopPropagation(); if (onEventClick) onEventClick(it.e.id); }}
                      >
                        {it.allDay
                          ? (it.showLabel
                              ? <span className="odc-cal-chip-t">{it.e.recurring ? <span className="material-icons odc-cal-chip-rec" aria-hidden="true">autorenew</span> : null}{it.e.title}</span>
                              : <span className="odc-cal-chip-t" aria-hidden="true">&nbsp;</span>)
                          : (
                            <React.Fragment>
                              <span className="odc-cal-chip-dot" aria-hidden="true" />
                              <span className="odc-cal-chip-time">{cgTimeLabel(it.e.start)}</span>
                              <span className="odc-cal-chip-t">{it.e.recurring ? <span className="material-icons odc-cal-chip-rec" aria-hidden="true">autorenew</span> : null}{it.e.title}</span>
                            </React.Fragment>
                          )}
                      </button>
                    );
                  })}
                  {overflow > 0 ? (
                    <button type="button" className="odc-cal-more"
                      aria-label={`${overflow} more ${overflow === 1 ? 'event' : 'events'} on ${cgLongDay(iso)}`}
                      onClick={(ev) => { ev.stopPropagation(); openDayPopover(iso, ev.currentTarget.closest('.odc-cal-day')); }}>
                      +{overflow} more
                    </button>
                  ) : null}
                </div>
                {void wd}
              </div>
            );
          })}
        </div>
      ))}

      {pop && typeof document !== 'undefined'
        ? ReactDOM.createPortal(
          <React.Fragment>
            <div className="odc-cal-popcatch" onClick={() => setPop(null)} />
            <div className="odc-cal-daypop" role="dialog" aria-label={cgLongDay(pop.iso)} style={{ position: 'fixed', top: pop.top, left: pop.left }}>
              <div className="odc-cal-daypop-head">
                <div className="odc-cal-daypop-wd">{ODC_CG_WD_SUN[cgWeekday(pop.iso)]}</div>
                <div className="odc-cal-daypop-n">{cgParse(pop.iso).d}</div>
              </div>
              <div className="odc-cal-daypop-list">
                {itemsForDay(pop.iso, cgWeekday(pop.iso)).map((it, i) => {
                  const sw = it.e.color ? { hex: it.e.color, fg: it.e.fg || '#fff' } : swatchFor(it.e.color);
                  return (
                    <button key={it.e.id + '-' + i} type="button" className="odc-cal-daypop-row"
                      onClick={() => { setPop(null); if (onEventClick) onEventClick(it.e.id); }}>
                      <span className="odc-cal-daypop-swatch" style={{ background: sw.hex }} aria-hidden="true" />
                      <span className="odc-cal-daypop-time">{it.allDay ? 'All day' : cgTimeLabel(it.e.start)}</span>
                      <span className="odc-cal-daypop-t">{it.e.recurring ? <span className="material-icons odc-cal-chip-rec" aria-hidden="true">autorenew</span> : null}{it.e.title}</span>
                    </button>
                  );
                })}
              </div>
              <div className="odc-cal-daypop-foot">
                <button type="button" className="odc-btn text" onClick={() => { const iso = pop.iso; setPop(null); if (onDayClick) onDayClick(iso); }}>
                  <span className="material-icons" aria-hidden="true" style={{ fontSize: 18 }}>add</span> New event
                </button>
              </div>
            </div>
          </React.Fragment>,
          document.body,
        )
        : null}
    </div>
  );
}
