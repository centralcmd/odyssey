/**
 * Odyssey DS — TagMultiSelect
 * The multi-member picker behind the transaction-tag fields AND every entity
 * picker built on it (Journal Contacts, Photos People / Albums, and the four
 * Insurance link collections — insurers, insured accounts, insured contacts,
 * beneficiaries). A field whose control box shows each selected member as a
 * removable chip, with a trigger that opens a searchable, checkable list.
 * Provide `onCreate` to offer an inline "Create …" row for a name that matches
 * nothing (the create affordance the single tag Combobox had).
 *
 * Controlled: `value` is an array of ids; `onChange(nextIds)` fires the full
 * next set on every add / remove. `options` are {value,label,icon?,iconColor?}
 * (or plain strings). The popover is portaled to <body> so it escapes a modal
 * body / card / collapsible overflow, and flips above when there isn't room
 * below. Shares the Field label + help + error chrome. Styled by `.odc-tagms`.
 *
 * Structure (why the control box is not a <button>): the chips and their remove
 * controls live INSIDE the control box, so a real <button> remove control would
 * nest interactive elements. The box is a plain container holding the chip list
 * and a separate focusable trigger — which is what lets each chip carry a real
 * `<button aria-label="Remove …">` (keyboard operable, ≥24 px hit area, focus
 * moved to the next remove control or the trigger after a removal).
 *
 * Entity-picker props:
 *   • `loading`          — an announced "Loading…" row, distinct from "no match"
 *                          (options that arrive asynchronously).
 *   • `unknownLabel`     — the label for a selected id absent from `options`, so
 *                          a raw GUID is never rendered or announced.
 *   • `chipTemplate(id)` — renders the chip BODY for a member (e.g. the DS
 *                          ContactChip with its Archived / Unavailable states).
 *                          The picker keeps owning the remove <button>, and the
 *                          default `.odc-chip` wrapper is not emitted.
 *   • `preserveOnClear(id)` — true for a member the picker must not remove: the
 *                          bulk Clear keeps it (and reports how many were kept)
 *                          AND no remove control is rendered for it. Used for a
 *                          member with no row in the list to have been chosen
 *                          from — an archived or unresolvable link.
 *   • `searchLabel` / `searchPlaceholder` — name the entity being searched.
 *   • `apiRef`           — receives `{ focus() }` so a host can move focus to an
 *                          invalid picker after a failed save.
 */

/* ---- odcUsePopover — fixed-position, portaled popover (inlined; bundle
   components are standalone and can't import each other). Measures the trigger,
   renders into <body>, flips up when cramped, closes on outside-click + Esc. */
function odcUsePopover({ align = 'start', gap = 6, matchWidth = true } = {}) {
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
    if (roomBelow >= ph + gap || roomBelow >= roomAbove) top = r.bottom + gap;
    else top = Math.max(gap, r.top - gap - ph);
    let left = align === 'end' ? r.right - pw : r.left;
    left = Math.min(Math.max(gap, left), Math.max(gap, vw - pw - gap));
    setBox({ top, left, width: matchWidth ? r.width : null });
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
      const t = anchorRef.current && anchorRef.current.querySelector('.odc-tagms-trigger');
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
  return { open, setOpen, anchorRef, popRef, floatStyle };
}

export function TagMultiSelect({
  label,
  value = [],
  onChange,
  options = [],
  placeholder = 'No tags',
  addLabel = 'Add tag',
  onCreate,
  createLabel = 'Create',
  help,
  error,
  required = false,
  optional = false,
  disabled = false,
  emptyText = 'No tags match',
  loading = false,
  loadingText = 'Loading…',
  searchLabel = 'Search tags',
  searchPlaceholder,
  unknownLabel = 'Unknown',
  chipTemplate,
  preserveOnClear,
  apiRef,
  noun = 'tag',
  className = '',
  id,
}) {
  const { useState, useRef, useEffect } = React;
  const autoId = React.useId();
  const fieldId = id || autoId;
  const helpId = `${fieldId}-help`;
  const labelId = `${fieldId}-label`;
  const msg = error || help;

  const opts = options.map((o) => (typeof o === 'string' ? { value: o, label: o } : o));
  const byVal = Object.fromEntries(opts.map((o) => [o.value, o]));
  // A selected id absent from `options` never renders as a raw id — it reads as
  // `unknownLabel` (or through `chipTemplate`, which knows the real state).
  const selected = value.map((v) => byVal[v] || { value: v, label: unknownLabel, unknown: true });

  const { open, setOpen, anchorRef, popRef, floatStyle } = odcUsePopover({ matchWidth: true });
  const [query, setQuery] = useState('');
  const [announce, setAnnounce] = useState('');
  const inputRef = useRef(null);
  const triggerRef = useRef(null);
  const chipsRef = useRef(null);
  const nonce = useRef(0);
  // Focus target after a removal: the index of the remove control to land on,
  // or -1 for the trigger (the last chip went).
  const pendingFocus = useRef(null);

  // Repeat announcements: an identical live-region string does not re-fire, so
  // every message carries an invisible counter token.
  const say = (text) => { nonce.current += 1; setAnnounce(`${text}${'\u200B'.repeat(nonce.current % 4 + 1)}`); };

  useEffect(() => {
    if (open) setTimeout(() => inputRef.current && inputRef.current.focus(), 20);
    else setQuery('');
  }, [open]);

  useEffect(() => {
    if (!apiRef) return;
    apiRef.current = { focus: () => triggerRef.current && triggerRef.current.focus() };
  }, [apiRef]);

  useEffect(() => {
    const want = pendingFocus.current;
    if (want == null) return;
    pendingFocus.current = null;
    const btns = chipsRef.current ? chipsRef.current.querySelectorAll('.odc-tagms-x') : [];
    const next = want >= 0 && btns[want] ? btns[want] : (btns.length ? btns[btns.length - 1] : null);
    if (next) next.focus();
    else if (triggerRef.current) triggerRef.current.focus();
  }, [value.length]);

  const set = new Set(value);
  const nameOf = (o) => (o.unknown ? unknownLabel : o.label);
  const locked = (v) => !!(preserveOnClear && preserveOnClear(v));

  const toggle = (v) => {
    const next = new Set(set);
    const adding = !next.has(v);
    if (adding) next.add(v);
    else next.delete(v);
    if (onChange) onChange([...next]);
    const o = byVal[v] || { value: v, label: unknownLabel, unknown: true };
    say(`${nameOf(o)} ${adding ? 'added' : 'removed'}. ${next.size} ${noun}${next.size === 1 ? '' : 's'} selected.`);
  };
  const remove = (v, idx) => {
    if (locked(v)) return;
    const rest = value.filter((x) => x !== v);
    // Focus moves to the next chip's remove control, or the trigger when the
    // last chip goes — never lost to <body>.
    pendingFocus.current = rest.length ? Math.min(idx, rest.length - 1) : -1;
    if (onChange) onChange(rest);
    const o = byVal[v] || { value: v, label: unknownLabel, unknown: true };
    say(`${nameOf(o)} removed. ${rest.length} ${noun}${rest.length === 1 ? '' : 's'} selected.`);
  };
  const clear = () => {
    const kept = value.filter((v) => locked(v));
    if (onChange) onChange(kept);
    say(kept.length
      ? `Selection cleared. ${kept.length} ${noun}${kept.length === 1 ? '' : 's'} kept — ${kept.length === 1 ? 'it cannot' : 'they cannot'} be removed here.`
      : 'Selection cleared.');
  };

  const q = query.trim().toLowerCase();
  const filtered = q ? opts.filter((o) => o.label.toLowerCase().includes(q)) : opts;
  const exact = opts.some((o) => o.label.toLowerCase() === q);
  const showCreate = !!onCreate && !!q && !exact && !loading;

  const create = () => {
    const made = onCreate(query.trim());
    if (made != null && onChange) {
      const opt = typeof made === 'string' ? { value: made, label: made } : made;
      if (!set.has(opt.value)) { onChange([...value, opt.value]); say(`${opt.label} created and added.`); }
    }
    setQuery('');
  };

  /* Keyboard inside the popover. Two things make this a native, window-capture
     listener rather than a React onKeyDown: the popover is portaled to <body>,
     OUTSIDE the React root container, so a React handler on it never fires; and
     the Modal traps Tab with a document-level listener, which would pull focus
     back into the dialog before a bubble-phase handler could stop it. Capturing
     on window runs first, so the popover owns its own keys:
       Tab / Shift+Tab  cycle within the popover (search, rows, Clear, Done)
       ↑ / ↓            rove the option rows; from the search field, ↓ enters
       Enter            toggles the focused row
       Esc              closes and returns focus to the trigger (odcUsePopover) */
  const popFocusables = () => (popRef.current
    ? Array.from(popRef.current.querySelectorAll('.odc-tagms-search input, .odc-tagms-opt input[type="checkbox"], .odc-tagms-create, .odc-tagms-foot .odc-btn:not([disabled])'))
    : []);
  /* Keep the roved row visible. The row's checkbox is visually replaced, so the
     browser's own scroll-on-focus has nothing to scroll to — the list is nudged
     by the overshoot instead (never scrollIntoView, which moves the page). */
  const revealRow = (el) => {
    const row = el && el.closest('.odc-tagms-opt');
    const list = popRef.current && popRef.current.querySelector('.odc-tagms-list');
    if (!row || !list) return;
    const r = row.getBoundingClientRect();
    const l = list.getBoundingClientRect();
    if (r.top < l.top) list.scrollTop -= (l.top - r.top) + 4;
    else if (r.bottom > l.bottom) list.scrollTop += (r.bottom - l.bottom) + 4;
  };

  useEffect(() => {
    if (!open) return undefined;
    const onKey = (e) => {
      const pop = popRef.current;
      if (!pop || !pop.contains(e.target)) return;
      const els = popFocusables();
      if (!els.length) return;
      const i = els.indexOf(document.activeElement);
      if (e.key === 'Tab') {
        e.preventDefault();
        e.stopPropagation();
        const n = els.length;
        const next = e.shiftKey ? (i <= 0 ? n - 1 : i - 1) : (i < 0 || i === n - 1 ? 0 : i + 1);
        els[next].focus();
        revealRow(els[next]);
        return;
      }
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        const boxes = els.filter((x) => x.type === 'checkbox');
        if (!boxes.length) return;
        e.preventDefault();
        e.stopPropagation();
        const bi = boxes.indexOf(document.activeElement);
        const target = bi < 0
          ? boxes[e.key === 'ArrowDown' ? 0 : boxes.length - 1]
          : boxes[Math.min(Math.max(bi + (e.key === 'ArrowDown' ? 1 : -1), 0), boxes.length - 1)];
        target.focus();
        revealRow(target);
        return;
      }
      if (e.key === 'Enter') {
        const el = document.activeElement;
        if (el && el.type === 'checkbox') { e.preventDefault(); e.stopPropagation(); el.click(); }
      }
    };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, [open]);

  const removeBtn = (o, idx) => (
    <button
      type="button"
      className="odc-tagms-x"
      aria-label={`Remove ${nameOf(o)}`}
      onClick={(e) => { e.stopPropagation(); remove(o.value, idx); }}
    >
      <span className="material-icons" aria-hidden="true">close</span>
    </button>
  );

  return (
    <div className={`odc-field${error ? ' error' : ''}${className ? ' ' + className : ''}`}>
      {label ? (
        <label className="odc-field-label" id={labelId} htmlFor={fieldId}>
          {label}
          {required ? <span className="odc-field-req" aria-hidden="true">*</span> : null}
          {optional ? <span className="odc-field-opt">Optional</span> : null}
        </label>
      ) : null}

      <div className="odc-tagms" ref={anchorRef}>
        {/* Not a <button>: the chips inside carry their own remove buttons. */}
        <div
          className={`odc-tagms-control${open ? ' open' : ''}${selected.length ? '' : ' placeholder'}${disabled ? ' disabled' : ''}`}
          onMouseDown={(e) => {
            // Clicking the box's own empty space opens the popover; a click on a
            // chip or its remove button does not.
            if (disabled) return;
            if (e.target.closest('.odc-tagms-chip, .odc-tagms-tchip, .odc-tagms-x, .odc-tagms-trigger')) return;
            e.preventDefault();
            setOpen((o) => !o);
            if (triggerRef.current) triggerRef.current.focus();
          }}
        >
          <span className="odc-tagms-chips" ref={chipsRef}>
            {selected.length === 0 ? (
              <span className="odc-tagms-ph">{placeholder}</span>
            ) : (
              selected.map((t, i) => (
                chipTemplate ? (
                  /* The template owns the chip body AND its styling — the
                     default .odc-chip wrapper would double-apply. */
                  <span className="odc-tagms-tchip" key={t.value}>
                    {chipTemplate(t.value)}
                    {locked(t.value) ? null : removeBtn(t, i)}
                  </span>
                ) : (
                  <span className="odc-chip entity odc-tagms-chip" key={t.value}>
                    {t.icon ? <span className="material-icons odc-tagms-chip-ic" style={t.iconColor ? { color: t.iconColor } : undefined} aria-hidden="true">{t.icon}</span> : null}
                    {t.label}
                    {locked(t.value) ? null : removeBtn(t, i)}
                  </span>
                )
              ))
            )}
          </span>
          <button
            type="button"
            id={fieldId}
            ref={triggerRef}
            className="odc-tagms-trigger odc-tagms-add"
            disabled={disabled}
            aria-haspopup="dialog"
            aria-expanded={open}
            aria-invalid={error ? true : undefined}
            aria-describedby={msg ? helpId : undefined}
            onClick={() => setOpen((o) => !o)}
          >
            <span className="material-icons" aria-hidden="true">{open ? 'expand_less' : 'add'}</span>
            {selected.length === 0 ? <span>{addLabel}</span> : <span className="odc-sr-only">{addLabel}</span>}
          </button>
        </div>

        {open
          ? ReactDOM.createPortal(
            /* A labelled GROUP of checkboxes — not a listbox: these are real
               <input type="checkbox"> rows, so role="listbox"/"option" would
               describe a widget that isn't here. */
            <div className="odc-tagms-pop" role="group" aria-label={typeof label === 'string' && label ? label : addLabel} ref={popRef} style={floatStyle}>
              <div className="odc-tagms-search">
                <span className="material-icons" aria-hidden="true">search</span>
                <input
                  ref={inputRef}
                  value={query}
                  aria-label={searchLabel}
                  placeholder={searchPlaceholder || (onCreate ? 'Search or add a tag…' : 'Search tags…')}
                  onChange={(e) => setQuery(e.target.value)}
                  onKeyDown={(e) => { if (e.key === 'Enter' && showCreate) { e.preventDefault(); create(); } }}
                />
              </div>
              <div className="odc-tagms-list">
                {loading ? (
                  <div className="odc-tagms-loading" role="status">
                    <span className="material-icons" aria-hidden="true">hourglass_top</span>
                    <span>{loadingText}</span>
                  </div>
                ) : (
                  <React.Fragment>
                    {filtered.map((o) => (
                      <label className="odc-tagms-opt odc-check" key={o.value}>
                        <input type="checkbox" checked={set.has(o.value)} onChange={() => toggle(o.value)} />
                        <span className="odc-check-box" aria-hidden="true">
                          <span className="material-icons">check</span>
                        </span>
                        <span className="odc-check-label">
                          {o.icon ? <span className="material-icons odc-tagms-opt-ic" style={o.iconColor ? { color: o.iconColor } : undefined} aria-hidden="true">{o.icon}</span> : null}
                          <span>{o.label}</span>
                          {o.sub ? <span className="odc-tagms-opt-sub">{o.sub}</span> : null}
                        </span>
                      </label>
                    ))}
                    {showCreate ? (
                      <button type="button" className="odc-tagms-create" onMouseDown={(e) => { e.preventDefault(); create(); }}>
                        <span className="material-icons" aria-hidden="true">add</span>
                        <span>{`${createLabel} "${query.trim()}"`}</span>
                      </button>
                    ) : null}
                    {filtered.length === 0 && !showCreate ? <div className="odc-tagms-empty">{emptyText}</div> : null}
                  </React.Fragment>
                )}
              </div>
              <div className="odc-tagms-foot">
                <button type="button" className="odc-btn text" onClick={clear} disabled={!value.some((v) => !locked(v))}>Clear</button>
                <button type="button" className="odc-btn text" onClick={() => { setOpen(false); if (triggerRef.current) triggerRef.current.focus(); }}>Done</button>
              </div>
            </div>,
            document.body,
          )
          : null}
      </div>

      {msg ? <div className="odc-field-help" id={helpId} role={error ? 'alert' : undefined}>{msg}</div> : null}
      <div className="odc-sr-only" role="status" aria-live="polite">{announce}</div>
    </div>
  );
}
