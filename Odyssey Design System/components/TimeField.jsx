/**
 * Odyssey DS — TimeField
 * ---------------------------------------------------------------------------
 * The labelled time-of-day field: the timed sibling of `DateField`. The
 * existing date controls (`DatePicker` / `DateField`) only bind date
 * granularity (`YYYY-MM-DD`), but a calendar event needs a concrete start /
 * end time — so this is the net-new control the Calendar feature adds, built
 * on the same `FieldShell` chrome so a time entry reads and aligns like every
 * other labelled control in a form.
 *
 * Value is a 24-hour `HH:mm` string (tabular monospace, like every other
 * time/number in the app). You can type a time (loose parsing: "9", "9:5",
 * "930", "0930" all normalise) or pick from the step-interval suggestion list;
 * the value is normalised on commit. The list is a portaled popover so it
 * escapes any overflow:hidden ancestor (a modal body / scrollable form).
 *
 * Controlled: pass `value` (`HH:mm` | null) + `onChange(next)` (null on clear).
 *
 *   <TimeField label="Starts" value={t} onChange={setT} step={15} />
 */

const odcTFpad = (n) => String(n).padStart(2, '0');

// Loose parse → { h, m } | null. Accepts "9", "9:5", "9 30", "930", "0930",
// "13:45", trailing am/pm.
function odcParseTime(raw) {
  if (raw == null) return null;
  let s = String(raw).trim().toLowerCase();
  if (!s) return null;
  let mer = null;
  if (/(a|p)m?$/.test(s)) { mer = s.includes('p') ? 'pm' : 'am'; s = s.replace(/\s*(a|p)m?$/, ''); }
  s = s.trim();
  let h, m;
  if (s.includes(':') || s.includes('.') || s.includes(' ')) {
    const parts = s.split(/[:.\s]+/);
    h = parseInt(parts[0], 10);
    m = parts[1] != null ? parseInt(parts[1], 10) : 0;
  } else if (/^\d{3,4}$/.test(s)) {
    h = parseInt(s.slice(0, s.length - 2), 10);
    m = parseInt(s.slice(-2), 10);
  } else if (/^\d{1,2}$/.test(s)) {
    h = parseInt(s, 10); m = 0;
  } else {
    return null;
  }
  if (Number.isNaN(h) || Number.isNaN(m)) return null;
  if (mer === 'pm' && h < 12) h += 12;
  if (mer === 'am' && h === 12) h = 0;
  if (h < 0 || h > 23 || m < 0 || m > 59) return null;
  return { h, m };
}
function odcNormTime(raw) {
  const p = odcParseTime(raw);
  return p ? `${odcTFpad(p.h)}:${odcTFpad(p.m)}` : null;
}

export function TimeField({
  label,
  value,
  onChange,
  placeholder = 'HH:MM',
  step = 30,
  help,
  error,
  required = false,
  optional = false,
  disabled = false,
  full = true,
  className = '',
  id,
}) {
  const autoId = React.useId();
  const fieldId = id || autoId;
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const FieldShell = NS.FieldShell;

  const [open, setOpen] = React.useState(false);
  const [text, setText] = React.useState(value || '');
  // Whether the user has typed since opening. Until they do, the field shows
  // the full suggestion list (scrolled to the nearest time) rather than
  // treating the pre-filled value as a search query — otherwise a value that
  // isn't on the `step` grid (e.g. 14:32 with step 15) filters to nothing.
  const [dirty, setDirty] = React.useState(false);
  const [box, setBox] = React.useState(null);
  const anchorRef = React.useRef(null);
  const popRef = React.useRef(null);
  const inputRef = React.useRef(null);

  React.useEffect(() => { setText(value || ''); setDirty(false); }, [value]);

  // Build the suggestion list at `step` granularity.
  const options = React.useMemo(() => {
    const out = [];
    const st = step > 0 ? step : 30;
    for (let mins = 0; mins < 24 * 60; mins += st) out.push(`${odcTFpad(Math.floor(mins / 60))}:${odcTFpad(mins % 60)}`);
    return out;
  }, [step]);

  // Highlighted option for keyboard nav, and the currently-visible (filtered)
  // subset the arrow keys walk.
  const [active, setActive] = React.useState(-1);
  const visible = React.useMemo(() => options.filter((t) => {
    if (!dirty) return true;
    const q = text.trim(); if (!q) return true;
    const digits = q.replace(/[^0-9]/g, '');
    return t.startsWith(q) || t.replace(':', '').startsWith(digits);
  }), [options, dirty, text]);

  // Index of the option nearest the current text (handles off-grid values).
  const nearestIndex = React.useCallback(() => {
    if (!visible.length) return -1;
    const p = odcParseTime(text) || { h: 9, m: 0 };
    const target = p.h * 60 + p.m;
    let best = 0; let bestD = Infinity;
    visible.forEach((t, i) => { const [h, m] = t.split(':').map(Number); const d = Math.abs(h * 60 + m - target); if (d < bestD) { bestD = d; best = i; } });
    return best;
  }, [visible, text]);

  // Open the list with a sensible starting highlight (the current value, else
  // the nearest option) — used by click, ArrowDown/Up and Space.
  const openList = React.useCallback(() => {
    const norm = odcNormTime(text);
    const idx = norm ? visible.indexOf(norm) : -1;
    setActive(idx >= 0 ? idx : nearestIndex());
    setOpen(true);
  }, [text, visible, nearestIndex]);

  // Keep the highlight in range as the filtered list shrinks while typing.
  React.useEffect(() => { setActive((a) => (a >= visible.length ? visible.length - 1 : a)); }, [visible.length]);
  // Ensure a sensible highlight whenever the list opens (e.g. opened by click).
  React.useEffect(() => { if (open) setActive((a) => (a >= 0 && a < visible.length ? a : nearestIndex())); }, [open]);
  // Scroll the highlighted option into view as it moves.
  React.useEffect(() => {
    if (!open || active < 0 || !popRef.current) return;
    const el = popRef.current.querySelector(`[data-i="${active}"]`);
    if (el) el.scrollIntoView({ block: 'nearest' });
  }, [open, active]);

  const place = React.useCallback(() => {
    const a = anchorRef.current;
    if (!a) return;
    const r = a.getBoundingClientRect();
    const pop = popRef.current;
    const ph = pop ? pop.offsetHeight : 240;
    const vh = window.innerHeight;
    const below = vh - r.bottom;
    const top = (below >= ph + 6 || below >= r.top) ? r.bottom + 6 : Math.max(6, r.top - 6 - ph);
    setBox({ top, left: r.left, width: r.width });
  }, []);

  React.useLayoutEffect(() => {
    if (!open) { setBox(null); return undefined; }
    place();
    const onScroll = (e) => { if (popRef.current && popRef.current.contains(e.target)) return; place(); };
    const onDoc = (e) => {
      const a = anchorRef.current; const p = popRef.current;
      if ((a && a.contains(e.target)) || (p && p.contains(e.target))) return;
      commit(text); setOpen(false);
    };
    const onKey = (e) => { if (e.key === 'Escape') { e.stopPropagation(); setOpen(false); if (inputRef.current) inputRef.current.focus(); } };
    window.addEventListener('scroll', onScroll, true);
    window.addEventListener('resize', place);
    document.addEventListener('mousedown', onDoc);
    document.addEventListener('keydown', onKey, true);
    return () => {
      window.removeEventListener('scroll', onScroll, true);
      window.removeEventListener('resize', place);
      document.removeEventListener('mousedown', onDoc);
      document.removeEventListener('keydown', onKey, true);
    };
  }, [open, place, text]);

  // Scroll the closest option into view when opening.
  React.useEffect(() => {
    if (!open || !popRef.current) return;
    const p = odcParseTime(text) || { h: 9, m: 0 };
    const target = p.h * 60 + p.m;
    // Nearest option to the current time (handles off-grid values like 14:32).
    let best = null; let bestD = Infinity;
    popRef.current.querySelectorAll('[data-t]').forEach((el) => {
      const [h, m] = el.getAttribute('data-t').split(':').map(Number);
      const d = Math.abs(h * 60 + m - target);
      if (d < bestD) { bestD = d; best = el; }
    });
    if (best) best.scrollIntoView({ block: 'center' });
  }, [open]);

  const commit = (raw) => {
    const norm = odcNormTime(raw);
    setText(norm || '');
    setDirty(false);
    if (onChange) onChange(norm);
  };
  const pick = (t) => { setText(t); setDirty(false); if (onChange) onChange(t); setOpen(false); if (inputRef.current) inputRef.current.focus(); };

  // Native keydown listener bound directly on the input. React's *delegated*
  // onKeyDown does not fire for content portaled outside its root (this field
  // lives inside a body-portaled Modal), so key handling can't rely on the JSX
  // onKeyDown alone. Binding on the element itself fires regardless of where
  // the DOM node sits. Rebinds each render so the closure reads fresh state.
  // Mirror latest active/visible into refs so the native keydown handler reads
  // fresh values (a fast key sequence can outrun the effect rebind).
  const activeRef = React.useRef(active); activeRef.current = active;
  const visibleRef = React.useRef(visible); visibleRef.current = visible;

  React.useEffect(() => {
    const el = inputRef.current;
    if (!el) return undefined;
    const handler = (e) => {
      const a = activeRef.current; const vis = visibleRef.current;
      if (e.key === 'ArrowDown') { e.preventDefault(); if (!open) openList(); else setActive((x) => Math.min(vis.length - 1, x + 1)); }
      else if (e.key === 'ArrowUp') { e.preventDefault(); if (!open) openList(); else setActive((x) => Math.max(0, x - 1)); }
      else if (e.key === 'Enter') { e.preventDefault(); if (open && a >= 0 && a < vis.length) pick(vis[a]); else { commit(el.value); setOpen(false); } }
      else if (e.key === ' ') { e.preventDefault(); if (!open) openList(); else if (a >= 0 && a < vis.length) pick(vis[a]); }
      else if (e.key === 'Home' && open) { e.preventDefault(); setActive(0); }
      else if (e.key === 'End' && open) { e.preventDefault(); setActive(vis.length - 1); }
    };
    el.addEventListener('keydown', handler);
    return () => el.removeEventListener('keydown', handler);
  });

  const control = (
    <div className={`odc-timefield${full ? ' full' : ''}${disabled ? ' disabled' : ''}${error ? ' error' : ''}`} ref={anchorRef}>
      <span className="material-icons odc-timefield-ic" aria-hidden="true">schedule</span>
      <input
        id={fieldId}
        ref={inputRef}
        className="odc-timefield-input"
        type="text"
        inputMode="numeric"
        autoComplete="off"
        role="combobox"
        aria-expanded={open}
        aria-haspopup="listbox"
        aria-activedescendant={open && active >= 0 ? `${fieldId}-opt-${active}` : undefined}
        disabled={disabled}
        placeholder={placeholder}
        value={text}
        onBeforeInput={(e) => {
          // Cancel a literal space at the insertion source — the most reliable
          // cross-browser guard against a Tab-selected value being wiped by
          // Space. Space instead opens the list / picks the highlight. Reads
          // refs so it agrees with the native keydown handler.
          if (e.data === ' ') {
            e.preventDefault();
            const a = activeRef.current; const vis = visibleRef.current;
            if (!open) { openList(); }
            else if (a >= 0 && a < vis.length) { pick(vis[a]); }
          }
        }}
        onChange={(e) => {
          const v = e.target.value;
          // A whitespace-only value never replaces an existing time (defensive:
          // stray Space / IME). Use the clear button to empty the field.
          if (v.trim() === '' && text) { if (!open) openList(); return; }
          setText(v); setDirty(true); if (!open) setOpen(true);
        }}
        onFocus={() => { setDirty(false); }}
        onClick={() => (open ? null : openList())}
        onBlur={() => { commit(text); setOpen(false); }}
      />
      {text ? (
        <button type="button" className="odc-timefield-clear" aria-label="Clear time"
          onMouseDown={(e) => e.preventDefault()}
          onClick={() => { setText(''); if (onChange) onChange(null); }}>
          <span className="material-icons" aria-hidden="true">close</span>
        </button>
      ) : null}
      {open && typeof document !== 'undefined'
        ? ReactDOM.createPortal(
          <div className="odc-timefield-pop" role="listbox" aria-label="Times" ref={popRef}
            style={box ? { position: 'fixed', top: box.top, left: box.left, width: box.width } : { position: 'fixed', top: 0, left: 0, visibility: 'hidden' }}>
            {visible.map((t, i) => (
                <button key={t} type="button" role="option" id={`${fieldId}-opt-${i}`} data-t={t} data-i={i}
                  aria-selected={odcNormTime(text) === t}
                  className={`odc-timefield-opt${odcNormTime(text) === t ? ' selected' : ''}${i === active ? ' active' : ''}`}
                  onMouseDown={(e) => e.preventDefault()}
                  onMouseEnter={() => setActive(i)}
                  onClick={() => pick(t)}>{t}</button>
              ))}
          </div>,
          document.body,
        )
        : null}
    </div>
  );

  if (FieldShell) {
    return (
      <FieldShell label={label} htmlFor={fieldId} required={required} optional={optional} help={help} error={error} className={className}>
        {control}
      </FieldShell>
    );
  }
  const msg = error || help;
  return (
    <div className={`odc-field${error ? ' error' : ''}${className ? ' ' + className : ''}`}>
      {label ? <label className="odc-field-label" htmlFor={fieldId}>{label}{required ? <span className="odc-field-req" aria-hidden="true">*</span> : null}{optional ? <span className="odc-field-opt">Optional</span> : null}</label> : null}
      {control}
      {msg ? <div className="odc-field-help" role={error ? 'alert' : undefined}>{msg}</div> : null}
    </div>
  );
}
