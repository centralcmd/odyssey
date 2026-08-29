/**
 * Odyssey DS — DatePicker
 * Calendar popover for a single date — the transaction Date field, budget
 * period dates, the analyze-candidate dates. Maps to MudDatePicker. The value
 * is an ISO `YYYY-MM-DD` string (matches the codebase's stored dates), shown
 * in tabular monospace like every other date in the app.
 *
 * Controlled: pass `value` (ISO string | null) + `onChange(iso)`. Full grid
 * keyboard support when open: ←/→ day, ↑/↓ week, Home/End week ends,
 * PageUp/PageDown month, Enter/Space select, Esc closes (focus returns to the
 * trigger). `min`/`max` (ISO) disable out-of-range days.
 *
 * The calendar is portaled to <body> and positioned against the trigger, so it
 * escapes any overflow:hidden/auto ancestor (a modal body, card, or scrollable
 * form) instead of being clipped — and flips above the field when there isn't
 * room below.
 */

/* ---- odcUsePopover — fixed-position, portaled popover ----
 * Measures the trigger and renders the panel into <body> so it escapes any
 * overflow:hidden/auto ancestor. Flips above when there isn't room below,
 * clamps to the viewport horizontally, repositions on scroll / resize, and
 * closes on outside-click + Esc. Attach `anchorRef` to the trigger wrapper
 * and `popRef` to the portaled panel; spread `floatStyle` onto the panel. */
function odcUsePopover({ align = 'start', gap = 6, matchWidth = false } = {}) {
  const { useState, useRef, useCallback, useLayoutEffect } = React;
  const [open, setOpen] = useState(false);
  const [box, setBox] = useState(null);
  const anchorRef = useRef(null);
  const popRef = useRef(null);

  const place = useCallback(() => {
    const a = anchorRef.current;
    if (!a) return;
    const r = a.getBoundingClientRect();
    const pop = popRef.current;
    const vw = window.innerWidth;
    const vh = window.innerHeight;
    const ph = pop ? pop.offsetHeight : 0;
    const pw = pop ? pop.offsetWidth : r.width;
    const roomBelow = vh - r.bottom;
    const roomAbove = r.top;
    let top;
    let placement;
    if (roomBelow >= ph + gap || roomBelow >= roomAbove) {
      top = r.bottom + gap;
      placement = 'down';
    } else {
      top = Math.max(gap, r.top - gap - ph);
      placement = 'up';
    }
    let left = align === 'end' ? r.right - pw : r.left;
    left = Math.min(Math.max(gap, left), Math.max(gap, vw - pw - gap));
    setBox({ top, left, width: matchWidth ? r.width : null, placement });
  }, [align, gap, matchWidth]);

  useLayoutEffect(() => {
    if (!open) { setBox(null); return undefined; }
    place();
    const onScroll = (e) => { if (popRef.current && popRef.current.contains(e.target)) return; place(); };
    const onResize = () => place();
    const onDoc = (e) => {
      const a = anchorRef.current;
      const p = popRef.current;
      if ((a && a.contains(e.target)) || (p && p.contains(e.target))) return;
      setOpen(false);
    };
    const onKey = (e) => {
      if (e.key !== 'Escape') return;
      // Capture-phase + stopPropagation: Esc closes only this popover — a
      // Modal underneath (bubble-phase document listener) never sees it —
      // and keyboard focus returns to the trigger.
      e.stopPropagation();
      setOpen(false);
      const t = anchorRef.current && anchorRef.current.querySelector('button:not([disabled]), input, select, textarea, [tabindex]');
      if (t) t.focus();
    };
    window.addEventListener('scroll', onScroll, true);
    window.addEventListener('resize', onResize);
    document.addEventListener('mousedown', onDoc);
    document.addEventListener('keydown', onKey, true);
    return () => {
      window.removeEventListener('scroll', onScroll, true);
      window.removeEventListener('resize', onResize);
      document.removeEventListener('mousedown', onDoc);
      document.removeEventListener('keydown', onKey, true);
    };
  }, [open, place]);

  const floatStyle = box
    ? { position: 'fixed', top: box.top, left: box.left, right: 'auto', bottom: 'auto', margin: 0, width: box.width || undefined }
    : { position: 'fixed', top: 0, left: 0, visibility: 'hidden' };
  const toggle = useCallback(() => setOpen((o) => !o), []);
  return { open, setOpen, toggle, anchorRef, popRef, floatStyle, placement: box ? box.placement : 'down' };
}

const ODC_WEEKDAYS = ['Su', 'Mo', 'Tu', 'We', 'Th', 'Fr', 'Sa'];
const odcPad = (n) => String(n).padStart(2, '0');
const odcToISO = (y, m, d) => `${y}-${odcPad(m + 1)}-${odcPad(d)}`;
const odcParse = (s) => { if (!s) return null; const [y, m, d] = s.split('-').map(Number); return { y, m: m - 1, d }; };
const odcTodayISO = () => { const t = new Date(); return odcToISO(t.getFullYear(), t.getMonth(), t.getDate()); };
const odcAddDays = (iso, n) => {
  const p = odcParse(iso); const dt = new Date(Date.UTC(p.y, p.m, p.d));
  dt.setUTCDate(dt.getUTCDate() + n);
  return odcToISO(dt.getUTCFullYear(), dt.getUTCMonth(), dt.getUTCDate());
};
const odcAddMonths = (iso, n) => {
  const p = odcParse(iso); const dt = new Date(Date.UTC(p.y, p.m, 1));
  dt.setUTCMonth(dt.getUTCMonth() + n);
  const dim = new Date(Date.UTC(dt.getUTCFullYear(), dt.getUTCMonth() + 1, 0)).getUTCDate();
  return odcToISO(dt.getUTCFullYear(), dt.getUTCMonth(), Math.min(p.d, dim));
};
const odcWeekday = (iso) => { const p = odcParse(iso); return new Date(Date.UTC(p.y, p.m, p.d)).getUTCDay(); };

export function DatePicker({
  value,
  onChange,
  placeholder = 'Select date',
  disabled = false,
  min,
  max,
  align = 'start',
  full = false,
  id,
}) {
  const autoId = React.useId();
  const fieldId = id || autoId;
  const { open, setOpen, anchorRef, popRef, floatStyle } = odcUsePopover({ align });
  const [view, setView] = React.useState(() => odcParse(value) || odcParse(odcTodayISO()));
  const [focusISO, setFocusISO] = React.useState(value || odcTodayISO());
  const triggerRef = React.useRef(null);

  const today = odcTodayISO();
  const outOfRange = (iso) => (min && iso < min) || (max && iso > max);

  const openCal = () => {
    const base = value || today;
    const p = odcParse(base);
    setView({ y: p.y, m: p.m });
    setFocusISO(base);
    setOpen(true);
  };
  const close = (restore) => {
    setOpen(false);
    if (restore && triggerRef.current) triggerRef.current.focus();
  };

  // Keep keyboard focus on the active day as it moves.
  React.useEffect(() => {
    if (!open) return;
    const el = popRef.current && popRef.current.querySelector(`[data-iso="${focusISO}"]`);
    if (el) el.focus();
  }, [open, focusISO, popRef]);

  const moveFocus = (nextISO) => {
    const p = odcParse(nextISO);
    if (p.y !== view.y || p.m !== view.m) setView({ y: p.y, m: p.m });
    setFocusISO(nextISO);
  };

  const pick = (iso) => {
    if (outOfRange(iso)) return;
    if (onChange) onChange(iso);
    close(true);
  };

  // Mirror focusISO into a ref so the document keydown handler always reads the
  // latest value — otherwise a fast Arrow→Enter picks the pre-move date (the
  // effect rebinds on the next render, which may lag the next key).
  const focusRef = React.useRef(focusISO);
  focusRef.current = focusISO;

  const onGridKey = (e) => {
    const f = focusRef.current;
    switch (e.key) {
      case 'ArrowLeft': e.preventDefault(); moveFocus(odcAddDays(f, -1)); break;
      case 'ArrowRight': e.preventDefault(); moveFocus(odcAddDays(f, 1)); break;
      case 'ArrowUp': e.preventDefault(); moveFocus(odcAddDays(f, -7)); break;
      case 'ArrowDown': e.preventDefault(); moveFocus(odcAddDays(f, 7)); break;
      case 'Home': e.preventDefault(); moveFocus(odcAddDays(f, -odcWeekday(f))); break;
      case 'End': e.preventDefault(); moveFocus(odcAddDays(f, 6 - odcWeekday(f))); break;
      case 'PageUp': e.preventDefault(); moveFocus(odcAddMonths(f, -1)); break;
      case 'PageDown': e.preventDefault(); moveFocus(odcAddMonths(f, 1)); break;
      case 'Enter': case ' ': e.preventDefault(); pick(f); break;
      case 'Escape': e.preventDefault(); close(true); break;
      default: break;
    }
  };

  // Drive grid navigation from a DOCUMENT-level keydown while open. Two reasons
  // this can't hang off the day buttons' React onKeyDown: (1) the calendar is
  // body-portaled (e.g. inside a Modal), and React's delegated onKeyDown does
  // not fire for content outside its root; (2) DOM focus does not reliably land
  // or stay inside a portaled popover, so key events may target the trigger,
  // not a day. A document listener catches the keys regardless of where focus
  // sits; the roving highlight is shown via the `.kbd` class on `focusISO`
  // rather than `:focus`. Capture phase + preventDefault also stops Enter/Space
  // from re-triggering the trigger button. Rebinds each render for fresh state.
  React.useEffect(() => {
    if (!open) return undefined;
    document.addEventListener('keydown', onGridKey, true);
    return () => document.removeEventListener('keydown', onGridKey, true);
  });

  // Build the 6×7 day grid for the viewed month.
  const firstWeekday = new Date(Date.UTC(view.y, view.m, 1)).getUTCDay();
  const gridStart = odcAddDays(odcToISO(view.y, view.m, 1), -firstWeekday);
  const cells = Array.from({ length: 42 }, (_, i) => {
    const iso = odcAddDays(gridStart, i);
    const p = odcParse(iso);
    return { iso, day: p.d, inMonth: p.m === view.m };
  });
  const weeks = Array.from({ length: 6 }, (_, i) => cells.slice(i * 7, i * 7 + 7));
  const monthLabel = new Date(Date.UTC(view.y, view.m, 1))
    .toLocaleString('en-US', { month: 'long', year: 'numeric', timeZone: 'UTC' });

  return (
    <div className={`odc-dp${full ? ' full' : ''}`} ref={anchorRef}>
      <button
        type="button"
        id={fieldId}
        ref={triggerRef}
        className={`odc-dp-trigger${value ? '' : ' placeholder'}`}
        aria-haspopup="dialog"
        aria-expanded={open}
        disabled={disabled}
        onClick={() => (open ? close(false) : openCal())}
      >
        <span className="material-icons" aria-hidden="true">calendar_today</span>
        <span className="odc-dp-val">{value || placeholder}</span>
      </button>
      {open
        ? ReactDOM.createPortal(
          <div className={`odc-dp-pop ${align}`} role="dialog" aria-label="Choose date" ref={popRef} style={floatStyle}>
            <div className="odc-dp-head">
              <button type="button" className="odc-iconbtn sm" aria-label="Previous month"
                onClick={() => setView((v) => { const n = odcParse(odcAddMonths(odcToISO(v.y, v.m, 1), -1)); return { y: n.y, m: n.m }; })}>
                <span className="material-icons" aria-hidden="true">chevron_left</span>
              </button>
              <div className="odc-dp-month" aria-live="polite">{monthLabel}</div>
              <button type="button" className="odc-iconbtn sm" aria-label="Next month"
                onClick={() => setView((v) => { const n = odcParse(odcAddMonths(odcToISO(v.y, v.m, 1), 1)); return { y: n.y, m: n.m }; })}>
                <span className="material-icons" aria-hidden="true">chevron_right</span>
              </button>
            </div>
            <div className="odc-dp-grid" role="grid" aria-label={monthLabel}>
              {/* role="row" wrappers keep the ARIA grid tree valid (grid → row →
                  columnheader/gridcell); display:contents preserves the 7-column
                  CSS grid layout on .odc-dp-grid. */}
              <div role="row" style={{ display: 'contents' }}>
                {ODC_WEEKDAYS.map((w) => <div key={w} className="odc-dp-wd" role="columnheader">{w}</div>)}
              </div>
              {weeks.map((week, wi) => (
              <div key={wi} role="row" style={{ display: 'contents' }}>
              {week.map((c) => {
                const selected = c.iso === value;
                const disabledDay = outOfRange(c.iso);
                return (
                  <button
                    key={c.iso}
                    type="button"
                    role="gridcell"
                    data-iso={c.iso}
                    tabIndex={c.iso === focusISO ? 0 : -1}
                    aria-selected={selected}
                    aria-current={c.iso === today ? 'date' : undefined}
                    disabled={disabledDay}
                    className={`odc-dp-day${c.inMonth ? '' : ' muted'}${selected ? ' selected' : ''}${c.iso === focusISO ? ' kbd' : ''}${c.iso === today ? ' today' : ''}`}
                    onClick={() => pick(c.iso)}
                    onKeyDown={onGridKey}
                  >
                    {c.day}
                  </button>
                );
              })}
              </div>
              ))}
            </div>
            <div className="odc-dp-foot">
              <button type="button" className="odc-btn text" onClick={() => pick(today)}>Today</button>
              {value ? <button type="button" className="odc-btn text" onClick={() => { if (onChange) onChange(null); close(true); }}>Clear</button> : null}
            </div>
          </div>,
          document.body,
        )
        : null}
    </div>
  );
}
