/* Calendar — /calendar
   ----------------------------------------------------------------------------
   The shared household calendar, grouped under the Journal module. Events live
   on named, colour-coded calendars; a month grid is the primary view, with
   week / day time-grids and a chronological agenda. A legend sidebar toggles
   each calendar's visibility. Recurring events are eagerly materialized into
   individually-editable occurrences (mirrors the API). Seed from calendar-data.js. */

const CAL_VIEWS = [
  { value: 'month', label: 'Month', icon: 'calendar_view_month' },
  { value: 'week', label: 'Week', icon: 'calendar_view_week' },
  { value: 'day', label: 'Day', icon: 'calendar_view_day' },
  { value: 'agenda', label: 'Agenda', icon: 'view_agenda' },
];
const CAL_WD = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
const CAL_WD_S = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
const CAL_MO = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];

const calPad = (n) => String(n).padStart(2, '0');
const calDate = (iso) => (iso || '').slice(0, 10);
const calTime = (iso) => (iso || '').slice(11, 16);
const calParse = (isoD) => { const [y, m, d] = calDate(isoD).split('-').map(Number); return { y, m: m - 1, d }; };
const calMk = (y, m, d) => `${y}-${calPad(m + 1)}-${calPad(d)}`;
const calAdd = (isoD, n) => { const p = calParse(isoD); const dt = new Date(Date.UTC(p.y, p.m, p.d)); dt.setUTCDate(dt.getUTCDate() + n); return calMk(dt.getUTCFullYear(), dt.getUTCMonth(), dt.getUTCDate()); };
const calAddMonths = (isoD, n) => { const p = calParse(isoD); const dt = new Date(Date.UTC(p.y, p.m, 1)); dt.setUTCMonth(dt.getUTCMonth() + n); const dim = new Date(Date.UTC(dt.getUTCFullYear(), dt.getUTCMonth() + 1, 0)).getUTCDate(); return calMk(dt.getUTCFullYear(), dt.getUTCMonth(), Math.min(p.d, dim)); };
const calWeekday = (isoD) => { const p = calParse(isoD); return new Date(Date.UTC(p.y, p.m, p.d)).getUTCDay(); };
const calWeekStart = (isoD) => calAdd(isoD, -calWeekday(isoD));
const calMinutes = (iso) => { const t = calTime(iso); const [h, m] = t.split(':').map(Number); return (h || 0) * 60 + (m || 0); };
const calLongDate = (isoD) => { const p = calParse(isoD); return `${CAL_WD[calWeekday(isoD)]}, ${CAL_MO[p.m]} ${p.d}`; };

// ---- Recurrence materializer (bounded, mirrors the API's eager generation) ----
function calGenerateOccurrences(core, pattern) {
  const out = [];
  const startDate = calDate(core.start);
  const startT = core.isAllDay ? '00:00' : calTime(core.start);
  const endT = core.isAllDay ? '00:00' : calTime(core.end);
  // duration in days for all-day (exclusive-end): end-start
  const spanDays = core.isAllDay ? Math.max(1, Math.round((new Date(core.end) - new Date(core.start)) / 86400000)) : 0;
  const maxN = pattern.occurrenceCount ? Math.min(pattern.occurrenceCount, 400) : 400;
  const endLimit = pattern.recurrenceEndDate || calAdd(startDate, 365 * 3);
  const interval = Math.max(1, pattern.interval || 1);
  const push = (dISO) => {
    const start = `${dISO}T${startT}`;
    const end = core.isAllDay ? `${calAdd(dISO, spanDays)}T00:00` : `${dISO}T${endT}`;
    out.push({ start, end });
  };
  if (pattern.frequency === 'Daily') {
    let d = startDate; let i = 0;
    while (out.length < maxN && d <= endLimit && i < 2000) { push(d); d = calAdd(d, interval); i++; }
  } else if (pattern.frequency === 'Weekly') {
    const days = (pattern.daysOfWeek && pattern.daysOfWeek.length) ? pattern.daysOfWeek : [CAL_WD_S[calWeekday(startDate)]];
    const wkStart = calWeekStart(startDate);
    let wk = 0;
    while (out.length < maxN && wk < 260) {
      const base = calAdd(wkStart, wk * 7 * interval);
      for (let dow = 0; dow < 7; dow++) {
        const dISO = calAdd(base, dow);
        if (dISO < startDate) continue;
        if (dISO > endLimit) { wk = 9999; break; }
        if (days.includes(CAL_WD_S[calWeekday(dISO)]) && out.length < maxN) push(dISO);
      }
      wk++;
    }
  } else if (pattern.frequency === 'Monthly') {
    let anchor = startDate; let i = 0;
    while (out.length < maxN && anchor <= endLimit && i < 400) {
      const p = calParse(anchor); const dim = new Date(Date.UTC(p.y, p.m + 1, 0)).getUTCDate();
      push(calMk(p.y, p.m, Math.min(pattern.dayOfMonth || p.d + 1, dim)));
      anchor = calAddMonths(anchor, interval); i++;
    }
  } else { // Yearly
    let anchor = startDate; let i = 0;
    while (out.length < maxN && anchor <= endLimit && i < 200) {
      const p = calParse(anchor); const mo = (pattern.monthOfYear || p.m + 1) - 1;
      const dim = new Date(Date.UTC(p.y, mo + 1, 0)).getUTCDate();
      push(calMk(p.y, mo, Math.min(pattern.dayOfMonth || p.d + 1, dim)));
      anchor = calMk(p.y + interval, p.m, p.d); i++;
    }
  }
  return out;
}

/* ---- Week / Day time-grid ---- */
const CalTimeGrid = ({ days, events, onEventClick, onSlotClick, onEventChange, todayIso }) => {
  const { useRef, useEffect, useState } = React;
  const HOUR = 46;
  const GUTTER = 56;
  const scrollRef = useRef(null);
  const bodyRef = useRef(null);
  const movedRef = useRef(false);
  const justDraggedRef = useRef(false);
  const [drag, setDrag] = useState(null);
  useEffect(() => { if (scrollRef.current) scrollRef.current.scrollTop = 7 * HOUR - 12; }, []);

  const snap = (m) => Math.round(m / 15) * 15;
  const isoAt = (dayIso, min) => `${dayIso}T${calPad(Math.floor(min / 60))}:${calPad(min % 60)}`;
  const hhmm = (min) => `${calPad(Math.floor(min / 60))}:${calPad(min % 60)}`;

  const begin = (e, ev, mode) => {
    if (!onEventChange || ev.button !== 0) return;
    ev.preventDefault(); ev.stopPropagation();
    movedRef.current = false;
    const s = calMinutes(e.start); const en = Math.max(s + 15, calMinutes(e.end));
    const di = Math.max(0, days.indexOf(calDate(e.start)));
    setDrag({ id: e.id, mode, baseStart: s, baseEnd: en, dur: en - s, grabY: ev.clientY, grabDi: di, curStart: s, curEnd: en, curDi: di, color: e.color });
  };

  useEffect(() => {
    if (!drag) return undefined;
    const onMove = (ev) => {
      const body = bodyRef.current; if (!body) return;
      const rect = body.getBoundingClientRect();
      const colW = (rect.width - GUTTER) / days.length;
      const delta = snap(((ev.clientY - drag.grabY) / HOUR) * 60);
      if (Math.abs(ev.clientY - drag.grabY) > 3) movedRef.current = true;
      setDrag((d) => {
        if (!d) return d;
        if (d.mode === 'move') {
          let di = Math.floor((ev.clientX - rect.left - GUTTER) / colW);
          di = Math.max(0, Math.min(days.length - 1, di));
          if (di !== d.grabDi) movedRef.current = true;
          const cs = Math.max(0, Math.min(24 * 60 - d.dur, d.baseStart + delta));
          return { ...d, curStart: cs, curEnd: cs + d.dur, curDi: di };
        }
        const ce = Math.max(d.baseStart + 15, Math.min(24 * 60, d.baseEnd + delta));
        return { ...d, curEnd: ce, curDi: d.grabDi, curStart: d.baseStart };
      });
    };
    const onUp = () => {
      setDrag((d) => {
        if (d && movedRef.current && onEventChange) {
          const day = days[d.curDi];
          onEventChange(d.id, { start: isoAt(day, d.curStart), end: isoAt(day, d.curEnd) });
        }
        return null;
      });
      if (movedRef.current) { justDraggedRef.current = true; setTimeout(() => { justDraggedRef.current = false; }, 0); }
    };
    document.body.style.userSelect = 'none';
    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
    return () => { document.body.style.userSelect = ''; window.removeEventListener('mousemove', onMove); window.removeEventListener('mouseup', onUp); };
  }, [drag && drag.id, drag && drag.mode]);

  const allDayByDay = days.map((iso) => events.filter((e) => e.isAllDay && iso >= calDate(e.start) && iso < calDate(e.end)));
  const timedByDay = days.map((iso) => events.filter((e) => !e.isAllDay && calDate(e.start) === iso).sort((a, b) => calMinutes(a.start) - calMinutes(b.start)));

  // Greedy overlap columns within a day.
  const layout = (list) => {
    const cols = [];
    const placed = list.map((e) => {
      const s = calMinutes(e.start); const en = Math.max(s + 20, calMinutes(e.end));
      let ci = cols.findIndex((end) => end <= s);
      if (ci === -1) { ci = cols.length; cols.push(en); } else cols[ci] = en;
      return { e, s, en, ci };
    });
    return { placed, ncol: Math.max(1, cols.length) };
  };

  return (
    <div className="cal-tg">
      <div className="cal-tg-head" style={{ gridTemplateColumns: `56px repeat(${days.length}, 1fr)` }}>
        <div className="cal-tg-corner" />
        {days.map((iso) => {
          const p = calParse(iso); const isToday = iso === todayIso;
          return (
            <div key={iso} className={`cal-tg-dayhead${isToday ? ' today' : ''}`}>
              <span className="cal-tg-wd">{CAL_WD_S[calWeekday(iso)]}</span>
              <span className={`cal-tg-dnum${isToday ? ' today' : ''}`}>{p.d}</span>
            </div>
          );
        })}
      </div>
      <div className="cal-tg-allday" style={{ gridTemplateColumns: `56px repeat(${days.length}, 1fr)` }}>
        <div className="cal-tg-alllabel">all-day</div>
        {days.map((iso, di) => (
          <div key={iso} className="cal-tg-allcell" onClick={() => onSlotClick && onSlotClick(iso)}>
            {allDayByDay[di].map((e) => (
              <button key={e.id} type="button" className="cal-tg-allchip" style={{ background: e.color, color: e.fg }}
                onClick={(ev) => { ev.stopPropagation(); onEventClick(e.id); }}>
                {e.recurring ? <MIcon name="autorenew" size={12} /> : null}{e.title}
              </button>
            ))}
          </div>
        ))}
      </div>
      <div className="cal-tg-body-scroll" ref={scrollRef}>
        <div className="cal-tg-body" ref={bodyRef} style={{ gridTemplateColumns: `56px repeat(${days.length}, 1fr)`, height: 24 * HOUR }}>
          <div className="cal-tg-gutter">
            {Array.from({ length: 24 }, (_, h) => <div key={h} className="cal-tg-hour" style={{ height: HOUR }}><span>{h === 0 ? '' : `${calPad(h)}:00`}</span></div>)}
          </div>
          {days.map((iso, di) => {
            const { placed, ncol } = layout(timedByDay[di]);
            const isToday = iso === todayIso;
            return (
              <div key={iso} className={`cal-tg-col${isToday ? ' today' : ''}`} onClick={(ev) => { if (justDraggedRef.current) return; const r = ev.currentTarget.getBoundingClientRect(); const mins = Math.floor(((ev.clientY - r.top) / HOUR) * 60 / 30) * 30; onSlotClick && onSlotClick(iso, `${calPad(Math.floor(mins / 60))}:${calPad(mins % 60)}`); }}>
                {Array.from({ length: 24 }, (_, h) => <div key={h} className="cal-tg-line" style={{ top: h * HOUR }} />)}
                {placed.map(({ e, s, en, ci }) => (
                  <button key={e.id} type="button" className={`cal-tg-ev${onEventChange ? ' draggable' : ''}${drag && drag.id === e.id ? ' dragging' : ''}`}
                    style={{ top: (s / 60) * HOUR, height: Math.max(20, ((en - s) / 60) * HOUR - 2), left: `calc(${(ci / ncol) * 100}% + 2px)`, width: `calc(${100 / ncol}% - 4px)`, '--chip': e.color }}
                    onMouseDown={onEventChange ? (ev) => { if (!ev.target.closest('.cal-tg-ev-resize')) begin(e, ev, 'move'); } : undefined}
                    onClick={(ev) => { ev.stopPropagation(); if (justDraggedRef.current || movedRef.current) { movedRef.current = false; return; } onEventClick(e.id); }}>
                    <span className="cal-tg-ev-t">{e.recurring ? <MIcon name="autorenew" size={11} /> : null}{e.title}</span>
                    <span className="cal-tg-ev-time">{calTime(e.start)}–{calTime(e.end)}</span>
                    {e.location ? <span className="cal-tg-ev-loc">{e.location}</span> : null}
                    {onEventChange ? <span className="cal-tg-ev-resize" onMouseDown={(ev) => begin(e, ev, 'resize')} aria-hidden="true" /> : null}
                  </button>
                ))}
              </div>
            );
          })}
          {drag ? (
            <div className="cal-tg-ghost" style={{ top: (drag.curStart / 60) * HOUR, height: Math.max(20, ((drag.curEnd - drag.curStart) / 60) * HOUR - 2), left: `calc(56px + ${drag.curDi} * (100% - 56px) / ${days.length} + 2px)`, width: `calc((100% - 56px) / ${days.length} - 4px)`, '--chip': drag.color }}>
              <span className="cal-tg-ev-time">{hhmm(drag.curStart)}–{hhmm(drag.curEnd)}</span>
            </div>
          ) : null}
        </div>
      </div>
    </div>
  );
};

/* ---- Agenda ---- */
const CalAgenda = ({ events, fromIso, onEventClick, todayIso }) => {
  const upcoming = events.filter((e) => calDate(e.end) >= fromIso).sort((a, b) => (a.start < b.start ? -1 : 1));
  const byDay = {};
  upcoming.forEach((e) => { const k = calDate(e.start) < fromIso ? fromIso : calDate(e.start); (byDay[k] = byDay[k] || []).push(e); });
  const days = Object.keys(byDay).sort().slice(0, 60);
  if (!days.length) return <div className="cal-agenda-empty">Nothing scheduled from here on.</div>;
  return (
    <div className="cal-agenda">
      {days.map((iso) => (
        <div key={iso} className="cal-agenda-day">
          <div className={`cal-agenda-date${iso === todayIso ? ' today' : ''}`}>
            <span className="cal-agenda-dnum">{calParse(iso).d}</span>
            <span className="cal-agenda-dmeta"><span className="cal-agenda-wd">{CAL_WD[calWeekday(iso)]}</span><span className="cal-agenda-mo">{CAL_MO[calParse(iso).m]}{iso === todayIso ? ' · Today' : ''}</span></span>
          </div>
          <div className="cal-agenda-rows">
            {byDay[iso].sort((a, b) => (a.isAllDay === b.isAllDay ? calMinutes(a.start) - calMinutes(b.start) : a.isAllDay ? -1 : 1)).map((e) => (
              <button key={e.id} type="button" className="cal-agenda-row" onClick={() => onEventClick(e.id)}>
                <span className="cal-agenda-time">{e.isAllDay ? 'All day' : `${calTime(e.start)}–${calTime(e.end)}`}</span>
                <span className="cal-agenda-bar" style={{ background: e.color }} aria-hidden="true" />
                <span className="cal-agenda-t">{e.recurring ? <MIcon name="autorenew" size={13} /> : null}{e.title}</span>
                {e.location ? <span className="cal-agenda-loc"><MIcon name="place" size={13} />{e.location}</span> : null}
                <span className="cal-agenda-cal">{e.calendarName}</span>
              </button>
            ))}
          </div>
        </div>
      ))}
    </div>
  );
};

/* ---- Page ---- */
const Calendar = () => {
  const { useState, useMemo } = React;
  const CD = window.OdysseyCalendarData;
  const { Toast: CalToast, ToastStack: CalToastStack } = window.OdysseyDesignSystem_d5aa51 || {};

  const [calendars, setCalendars] = useState(CD.calendars);
  const [patterns, setPatterns] = useState(CD.patterns);
  const [events, setEvents] = useState(CD.events);
  const [view, setView] = useState('month');
  const [cursor, setCursor] = useState(CD.todayIso);
  const [calFilter, setCalFilter] = useState([]); // selected calendar ids; empty = all shown
  const [q, setQ] = useState('');
  const [dialog, setDialog] = useState(null); // {type:'create'|'edit'|'series', ...}
  const [managing, setManaging] = useState(false);
  const [importing, setImporting] = useState(false);
  const [exporting, setExporting] = useState(false);       // bulk export dialog
  const [exportScope, setExportScope] = useState(null);    // single recurring-event scope prompt: the event
  const [toast, setToast] = useState(null);
  const pushToast = (severity, message) => setToast({ severity, message, k: Date.now() });

  const calById = useMemo(() => calendars.reduce((m, c) => { m[c.id] = c; return m; }, {}), [calendars]);
  const uid = (p) => `${p}-${Date.now()}-${Math.floor(Math.random() * 1000)}`;

  // View-models for the grid/views — filtered by the header's calendar filter
  // (empty = all) and event-title/location search.
  const vms = useMemo(() => events.filter((e) => {
    if (calFilter.length && !calFilter.includes(e.calendarId)) return false;
    if (q.trim()) { const hay = `${e.title} ${e.location || ''}`.toLowerCase(); if (!hay.includes(q.trim().toLowerCase())) return false; }
    return true;
  }).map((e) => {
    const c = calById[e.calendarId] || {};
    return { ...e, calendarName: c.name, color: e.color || c.color, fg: e.fg || c.fg, recurring: !!e.patternId };
  }), [events, calFilter, q, calById]);

  const eventCounts = useMemo(() => { const m = {}; events.forEach((e) => { m[e.calendarId] = (m[e.calendarId] || 0) + 1; }); return m; }, [events]);

  // ---- CRUD ----
  const createEvent = (dto) => {
    const c = calById[dto.calendarId] || {};
    if (dto.pattern) {
      const pid = uid('rp');
      setPatterns((p) => [...p, { id: pid, calendarId: dto.calendarId, title: dto.title, ...dto.pattern, isAllDay: dto.isAllDay }]);
      const occ = calGenerateOccurrences(dto, dto.pattern);
      const rows = occ.map((o) => ({ id: uid('ev'), calendarId: dto.calendarId, title: dto.title, description: dto.description, location: dto.location, start: o.start, end: o.end, isAllDay: dto.isAllDay, patternId: pid, color: c.color, fg: c.fg }));
      setEvents((prev) => [...prev, ...rows]);
    } else {
      setEvents((prev) => [...prev, { id: uid('ev'), patternId: null, color: c.color, fg: c.fg, ...dto }]);
    }
    setDialog(null);
  };
  const updateEvent = (id, patch) => { const c = calById[patch.calendarId]; setEvents((prev) => prev.map((e) => e.id === id ? { ...e, ...patch, color: (c && c.color) || e.color, fg: (c && c.fg) || e.fg } : e)); setDialog(null); };
  const deleteEvent = (id) => { setEvents((prev) => prev.filter((e) => e.id !== id)); setDialog(null); };
  // Drag-and-drop reschedule: month grid shifts by whole days; week/day grid
  // sets exact start/end (move or resize).
  const moveEventDays = (id, toIso, fromIso) => {
    const delta = Math.round((new Date(toIso) - new Date(fromIso)) / 86400000);
    if (!delta) return;
    const shift = (iso) => `${calAdd(calDate(iso), delta)}${String(iso).slice(10)}`;
    setEvents((prev) => prev.map((e) => e.id === id ? { ...e, start: shift(e.start), end: shift(e.end) } : e));
  };
  const updateEventTimes = (id, patch) => setEvents((prev) => prev.map((e) => e.id === id ? { ...e, ...patch } : e));

  const openSeries = (occ) => {
    const pat = patterns.find((p) => p.id === occ.patternId);
    setDialog({ type: 'series', event: { ...occ, pattern: pat, patternId: occ.patternId } });
  };
  const updateSeries = (patternId, core, pattern) => {
    const c = calById[core.calendarId] || {};
    const today = CD.todayIso;
    setPatterns((prev) => prev.map((p) => p.id === patternId ? { ...p, ...pattern, title: core.title, isAllDay: core.isAllDay } : p));
    // Regenerate FUTURE occurrences only; keep past rows.
    const occ = calGenerateOccurrences(core, pattern).filter((o) => calDate(o.start) >= today);
    setEvents((prev) => {
      const kept = prev.filter((e) => !(e.patternId === patternId && calDate(e.start) >= today));
      const rows = occ.map((o) => ({ id: uid('ev'), calendarId: core.calendarId, title: core.title, description: core.description, location: core.location, start: o.start, end: o.end, isAllDay: core.isAllDay, patternId, color: c.color, fg: c.fg }));
      return [...kept, ...rows];
    });
    setDialog(null);
  };
  const deleteSeries = (patternId) => { const today = CD.todayIso; setEvents((prev) => prev.filter((e) => !(e.patternId === patternId && calDate(e.start) >= today))); setDialog(null); };

  // ---- Calendars CRUD ----
  const createCalendar = (dto) => { const sw = swatchFor(dto.color); setCalendars((prev) => [...prev, { id: uid('cal'), ...dto, fg: sw.fg }]); };
  const updateCalendar = (id, patch) => { const sw = swatchFor(patch.color); setCalendars((prev) => prev.map((c) => c.id === id ? { ...c, ...patch, fg: sw.fg } : c)); setEvents((prev) => prev.map((e) => e.calendarId === id ? { ...e, color: patch.color, fg: sw.fg } : e)); };
  const deleteCalendar = (id) => { if ((eventCounts[id] || 0) > 0) return; setCalendars((prev) => prev.filter((c) => c.id !== id)); setCalFilter((f) => f.filter((x) => x !== id)); };

  // ---- ICS export / import ----
  // Export builds the text/calendar body (calendar-data.js buildIcs — one RRULE
  // VEVENT per unmodified series, standalone VEVENTs otherwise, every VEVENT
  // UID-stamped) and saves it via a Blob download, mirroring the app's
  // downloadFileFromBytes interop.
  const exportCalendar = (id) => {
    const c = calById[id]; if (!c) return;
    const ics = CD.buildIcs(c, events, patterns);
    const safe = c.name.replace(/[\\/:*?"<>|]+/g, '').trim() || 'calendar';
    const blob = new Blob([ics], { type: 'text/calendar;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url; a.download = `${safe}.ics`;
    document.body.appendChild(a); a.click(); a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  };
  // Import runs the simulated parse, appends the imported rows to the calendar,
  // and returns the IcsImportResult for the dialog to summarize.
  const importIcs = (id, file) => {
    const { rows, result } = CD.mockImport(id);
    setEvents((prev) => [...prev, ...rows]);
    return result;
  };

  // ---- VEVENT export: single event, all, filtered (spec §5/§7) ----
  const icsDownload = (text, filename) => {
    const safe = (filename || 'calendar').replace(/[\\/:*?"<>|]+/g, '').trim() || 'calendar';
    const blob = new Blob([text], { type: 'text/calendar;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url; a.download = /\.ics$/i.test(safe) ? safe : `${safe}.ics`;
    document.body.appendChild(a); a.click(); a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  };
  const expStamp = () => { const d = new Date(); const p = (n) => String(n).padStart(2, '0'); return `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}`; };

  // Matched set for a bulk-export query (spec §9 filter semantics): scope 'all'
  // = every event; 'filtered' = date-overlap + calendar-id + title/location search.
  const eventsForQuery = (query) => {
    if (!query || query.scope === 'all') return events;
    return events.filter((e) => {
      if (query.calendarIds && query.calendarIds.length && !query.calendarIds.includes(e.calendarId)) return false;
      if (query.from && query.to) { if (calDate(e.end) < query.from || calDate(e.start) > query.to) return false; }
      if (query.search) { const hay = `${e.title} ${e.location || ''}`.toLowerCase(); if (!hay.includes(query.search.toLowerCase())) return false; }
      return true;
    });
  };
  const MAX_VEVENTS = 2000;

  // Single-event export (spec §7 #1). A standalone event downloads immediately;
  // a recurring occurrence first asks occurrence-vs-series (ExportEventScopeModal).
  const exportSingle = (event, scope) => {
    const pattern = event.patternId ? patterns.find((p) => p.id === event.patternId) : null;
    const seriesRows = event.patternId ? events.filter((e) => e.patternId === event.patternId) : null;
    icsDownload(CD.buildSingleIcs(event, scope, pattern, seriesRows), `odyssey-event-${expStamp()}`);
    pushToast('success', scope === 'series' ? 'Exported the full series.' : 'Exported 1 event.');
  };
  const requestExportSingle = (event) => {
    if (event && event.patternId) setExportScope(event);
    else if (event) exportSingle(event, 'occurrence');
  };
  // Bulk export (spec §7 #2). Returns { error } to keep the dialog open on the
  // cap failure; otherwise fires the download and closes the dialog.
  const exportBulk = (query) => {
    const set = eventsForQuery(query);
    if (set.length > MAX_VEVENTS) return { error: `That would export ${set.length} events, over the ${MAX_VEVENTS.toLocaleString()} limit. Narrow the date range or calendar selection and try again.` };
    const pats = query.scope === 'all' ? patterns : patterns.filter((p) => set.some((e) => e.patternId === p.id));
    window.__calAllEvents = events; // lets the builder tell a whole series from a filtered subset
    const name = query.scope === 'filtered' ? `odyssey-events-filtered-${expStamp()}` : `odyssey-events-${expStamp()}`;
    icsDownload(CD.buildIcsFrom(set, pats, {}), name);
    setExporting(false);
    pushToast('success', `Exported ${set.length} ${set.length === 1 ? 'event' : 'events'}.`);
    return undefined;
  };

  // ---- Navigation ----
  const step = (dir) => {
    if (view === 'month') setCursor((c) => calAddMonths(c, dir));
    else if (view === 'week') setCursor((c) => calAdd(c, dir * 7));
    else if (view === 'day') setCursor((c) => calAdd(c, dir));
    else setCursor((c) => calAddMonths(c, dir));
  };
  const periodLabel = () => {
    const p = calParse(cursor);
    if (view === 'month' || view === 'agenda') return `${CAL_MO[p.m]} ${p.y}`;
    if (view === 'day') return calLongDate(cursor) + `, ${p.y}`;
    const ws = calWeekStart(cursor); const we = calAdd(ws, 6); const wsP = calParse(ws); const weP = calParse(we);
    return wsP.m === weP.m ? `${CAL_MO[wsP.m]} ${wsP.d} – ${weP.d}, ${wsP.y}` : `${CAL_MO[wsP.m].slice(0, 3)} ${wsP.d} – ${CAL_MO[weP.m].slice(0, 3)} ${weP.d}, ${weP.y}`;
  };

  const weekDays = useMemo(() => { const ws = calWeekStart(cursor); return Array.from({ length: 7 }, (_, i) => calAdd(ws, i)); }, [cursor]);

  // Prefill the bulk-export dialog's From/To from the period on screen (spec §3).
  const periodBounds = () => {
    if (view === 'week') { const ws = calWeekStart(cursor); return { from: ws, to: calAdd(ws, 6) }; }
    if (view === 'day') return { from: cursor, to: cursor };
    const p = calParse(cursor); // month / agenda
    const first = calMk(p.y, p.m, 1);
    const last = calMk(p.y, p.m, new Date(Date.UTC(p.y, p.m + 1, 0)).getUTCDate());
    return { from: first, to: last };
  };
  const exportInitial = () => { const b = periodBounds(); return { scope: 'filtered', from: b.from, to: b.to, calendarIds: calFilter, search: q }; };

  const hasEvents = events.length > 0;
  const calOptions = calendars.map((c) => ({ value: c.id, label: c.name, icon: 'circle', iconColor: c.color }));

  return (
    <div className="col gap-6">
      <PageHeader
        title="Calendar"
        icon="calendar_month"
        card
        sub={`${events.length} ${events.length === 1 ? 'event' : 'events'} · ${calendars.length} calendars`}
        menu={[
          { icon: 'settings', label: 'Manage calendars', onClick: () => setManaging(true) },
          { divider: true },
          { icon: 'event', label: 'Export all as iCalendar', onClick: () => exportBulk({ scope: 'all' }) },
          { icon: 'filter_list', label: 'Export filtered as iCalendar…', onClick: () => setExporting(true) },
          { divider: true },
          { icon: 'upload_file', label: 'Import from file…', onClick: () => setImporting(true) },
        ]}
        searchDefaultOpen
        search={(
          <div className="row gap-3 acct-filter-bar" style={{ flexWrap: 'wrap' }}>
            <div style={{ minWidth: 280, flex: 1 }}>
              <SearchField placeholder="Search events by title or location…" value={q} onChange={setQ} />
            </div>
            <div style={{ minWidth: 200 }}>
              <MultiSelect allLabel="All calendars" icon="calendar_month" value={calFilter} onChange={setCalFilter} options={calOptions} />
            </div>
          </div>
        )}
        primary={{ label: 'New event', icon: 'add', onClick: () => setDialog({ type: 'create', defaultDate: view === 'day' ? cursor : CD.todayIso }) }}
      />

      {/* Toolbar */}
      <div className="cal-toolbar">
        <div className="cal-toolbar-nav">
          <Button variant="outlined" onClick={() => setCursor(CD.todayIso)}>Today</Button>
          <IconButton icon="chevron_left" label="Previous" onClick={() => step(-1)} />
          <IconButton icon="chevron_right" label="Next" onClick={() => step(1)} />
          <h2 className="cal-period">{periodLabel()}</h2>
        </div>
        <SegmentedControl ariaLabel="Calendar view" value={view} onChange={setView} options={CAL_VIEWS} />
      </div>

      {!hasEvents ? (
        <EmptyState icon="calendar_month" title="No events yet"
          description="Add one-off or recurring events to a colour-coded calendar and see them here across month, week, day and agenda views."
          action={<Button variant="filled" color="primary" icon="add" onClick={() => setDialog({ type: 'create', defaultDate: CD.todayIso })}>Add your first event</Button>} />
      ) : (
          <div className="cal-view">
            {view === 'month' && (
              <CalendarGrid month={cursor} today={CD.todayIso} events={vms} maxPerDay={3}
                onDayClick={(iso) => setDialog({ type: 'create', defaultDate: iso })}
                onEventDrop={moveEventDays}
                onEventClick={(id) => { const e = events.find((x) => x.id === id); if (e) setDialog({ type: 'edit', event: e }); }} />
            )}
            {(view === 'week' || view === 'day') && (
              <CalTimeGrid days={view === 'day' ? [cursor] : weekDays} events={vms} todayIso={CD.todayIso}
                onSlotClick={(iso, t) => setDialog({ type: 'create', defaultDate: iso, defaultTime: t })}
                onEventChange={updateEventTimes}
                onEventClick={(id) => { const e = events.find((x) => x.id === id); if (e) setDialog({ type: 'edit', event: e }); }} />
            )}
            {view === 'agenda' && (
              <CalAgenda events={vms} fromIso={`${calParse(cursor).y}-${calPad(calParse(cursor).m + 1)}-01`} todayIso={CD.todayIso}
                onEventClick={(id) => { const e = events.find((x) => x.id === id); if (e) setDialog({ type: 'edit', event: e }); }} />
            )}
          </div>
      )}

      {dialog && (dialog.type === 'create' || dialog.type === 'edit') && (
        <AddCalendarEventModal
          mode={dialog.type}
          event={dialog.event}
          calendars={calendars}
          defaultDate={dialog.defaultDate}
          onClose={() => setDialog(null)}
          onCreate={createEvent}
          onUpdate={(patch) => updateEvent(dialog.event.id, patch)}
          onDelete={deleteEvent}
          onExport={() => requestExportSingle(dialog.event)}
          onEditSeries={openSeries}
        />
      )}
      {dialog && dialog.type === 'series' && (
        <AddCalendarEventModal
          mode="series"
          event={dialog.event}
          calendars={calendars}
          onClose={() => setDialog(null)}
          onUpdate={(payload) => updateSeries(dialog.event.patternId, payload, payload.pattern)}
          onExport={() => exportSingle(dialog.event, 'series')}
          onDeleteSeries={deleteSeries}
        />
      )}
      {managing && (
        <ManageCalendarsModal calendars={calendars} eventCounts={eventCounts}
          onClose={() => setManaging(false)} onCreate={createCalendar} onUpdate={updateCalendar} onDelete={deleteCalendar}
          onExport={exportCalendar}
          onImport={() => { setManaging(false); setImporting(true); }} />
      )}
      {importing && (
        <ImportCalendarModal calendars={calendars}
          onClose={() => setImporting(false)} onImport={importIcs} />
      )}
      {exporting && (
        <ExportCalendarEventsModal calendars={calendars} initial={exportInitial()}
          countFor={(query) => eventsForQuery(query).length}
          onClose={() => setExporting(false)} onExport={exportBulk} />
      )}
      {exportScope && (
        <ExportEventScopeModal event={exportScope}
          onClose={() => setExportScope(null)}
          onChoose={(scope) => { exportSingle(exportScope, scope); setExportScope(null); }} />
      )}
      {toast && CalToast && CalToastStack && (
        <CalToastStack>
          <CalToast key={toast.k} severity={toast.severity} duration={4200} onClose={() => setToast(null)} message={toast.message} />
        </CalToastStack>
      )}
    </div>
  );
};

Object.assign(window, { Calendar });
