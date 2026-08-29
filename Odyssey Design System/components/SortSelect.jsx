/**
 * Odyssey DS — SortSelect
 * The filter-bar "Sort by" control from the per-page sorting spec: a compact
 * field selector plus a direction toggle, always emitting a complete
 * `{ key, dir }` sort (the client-side `OdsTableSort`). It sits at the END of
 * a list page's search region (search → filters → sort) and is the single
 * source of truth for the page's active sort — on RecordTable/TxnTable pages
 * bind the same `{key,dir}` to the table's `sort`/`onSortChange` so header
 * clicks and this control stay in sync.
 *
 * `fields` is the page's curated allowlist (never every DTO property):
 *   { key, label, type: 'text'|'number'|'date'|'status', defaultDir?, sortValue? }
 * The `type` drives the field's natural default direction and the TYPED
 * direction labels (§4.4 of the spec):
 *   text   → asc default · "A → Z" / "Z → A"
 *   number → desc default · "Low → High" / "High → Low"
 *   date   → desc default · "Oldest first" / "Newest first"
 *   status → asc default · "Defined order" / "Reversed"
 * Changing the field applies its natural default direction; the toggle flips
 * it. The anatomy is identical regardless of how many fields are offered — a
 * one-field page still shows the same field-select trigger (with its single
 * option) + direction control, so the "Sort by" control reads the same on
 * every list page.
 *
 * Anatomy `variant`s (the spec's §4.2 is `split`; the others are explorations):
 *   split     — "Sort by · <field>" select trigger + direction toggle button
 *   segmented — select trigger + a two-segment asc/desc control
 *   menu      — one combined trigger opening field list + direction section
 *
 * Controlled: pass `sort` ({key,dir}) + `onSort(nextSort)`. Direction is
 * always conveyed as text (typed label + aria word), never icon alone.
 *
 * `SortHelpers` is the shared sorting authority the spec's §8 asks for:
 *   defaultDir(fieldOrType)          → the natural direction for a type
 *   dirLabel(type, dir)              → the typed label ("Newest first")
 *   fieldsFromColumns(columns, keys) → derive the curated field list from a
 *       RecordTable column set (single source of truth on table pages)
 *   sortRows(rows, fields, sort, id) → stable client-side ordering for
 *       hand-rolled list pages: nulls last in BOTH directions, record-id
 *       tiebreak. Apply AFTER search + filters.
 */

const SS_DEFAULT_DIR = { text: 'asc', number: 'desc', date: 'desc', status: 'asc' };
const SS_DIR_LABELS = {
  text:   { asc: 'A → Z',         desc: 'Z → A' },
  number: { asc: 'Low → High',    desc: 'High → Low' },
  date:   { asc: 'Oldest first',  desc: 'Newest first' },
  status: { asc: 'Defined order', desc: 'Reversed' },
};

export const SortHelpers = {
  /** Natural default direction for a field ({type,defaultDir?}) or a bare type string. */
  defaultDir(fieldOrType) {
    if (!fieldOrType) return 'asc';
    if (typeof fieldOrType === 'string') return SS_DEFAULT_DIR[fieldOrType] || 'asc';
    return fieldOrType.defaultDir || SS_DEFAULT_DIR[fieldOrType.type] || 'asc';
  },
  /** Typed, user-facing direction label per §4.4. */
  dirLabel(type, dir) {
    const t = SS_DIR_LABELS[type] || SS_DIR_LABELS.text;
    return t[dir] || dir;
  },
  /**
   * Derive SortSelect `fields` from a RecordTable/FilesTable column set so the
   * dropdown and the header sort can never diverge. `keys` (optional) curates
   * and orders the subset offered in the dropdown.
   */
  fieldsFromColumns(columns = [], keys) {
    const list = columns
      .filter((c) => c.sortable)
      .map((c) => ({
        key: c.key,
        label: typeof c.header === 'string' && c.header ? c.header : c.key,
        type: c.sortType || 'text',
        defaultDir: c.defaultDir,
      }));
    if (!keys) return list;
    const byKey = {};
    list.forEach((f) => { byKey[f.key] = f; });
    return keys.map((k) => byKey[k]).filter(Boolean);
  },
  /**
   * Stable client-side ordering for hand-rolled pages, from the same `fields`
   * list that feeds the dropdown. Null/empty keys sort LAST in both
   * directions; ties resolve on the record id, then input order.
   */
  sortRows(rows = [], fields = [], sort, getId) {
    if (!sort || !sort.key) return rows;
    const f = fields.find((x) => x.key === sort.key);
    if (!f) return rows;
    const val = f.sortValue || ((r) => r[sort.key]);
    const mul = sort.dir === 'desc' ? -1 : 1;
    const idOf = getId || ((r) => r.id);
    const tie = (a, b, ai, bi) => {
      const ia = String(idOf(a)), ib = String(idOf(b));
      return ia < ib ? -1 : ia > ib ? 1 : ai - bi;
    };
    return rows
      .map((r, i) => [r, i])
      .sort(([a, ai], [b, bi]) => {
        const va = val(a), vb = val(b);
        const ea = va == null || va === '', eb = vb == null || vb === '';
        if (ea || eb) return ea && eb ? tie(a, b, ai, bi) : (ea ? 1 : -1);
        if (va < vb) return -1 * mul;
        if (va > vb) return 1 * mul;
        return tie(a, b, ai, bi);
      })
      .map(([r]) => r);
  },
};

/* ---- Internal: shared fixed-position popover machinery (Select's pattern,
   trimmed — bundle components can't import each other). ---- */
function useSSPopover() {
  const { useState, useRef, useEffect } = React;
  const [open, setOpen] = useState(false);
  const [pos, setPos] = useState(null);
  const btnRef = useRef(null);
  const popRef = useRef(null);

  const openMenu = () => {
    if (btnRef.current) {
      const r = btnRef.current.getBoundingClientRect();
      setPos({ top: r.bottom + 6, left: r.left, width: r.width });
    }
    setOpen(true);
  };
  const close = (restore) => {
    setOpen(false);
    if (restore && btnRef.current) btnRef.current.focus();
  };
  const toggle = () => (open ? setOpen(false) : openMenu());

  useEffect(() => {
    if (!open) return undefined;
    const onDoc = (e) => {
      if (btnRef.current && btnRef.current.contains(e.target)) return;
      if (popRef.current && popRef.current.contains(e.target)) return;
      setOpen(false);
    };
    const onScroll = (e) => { if (popRef.current && popRef.current.contains(e.target)) return; setOpen(false); };
    const onResize = () => setOpen(false);
    const onKey = (e) => { if (e.key === 'Escape') { e.stopPropagation(); close(true); } };
    document.addEventListener('mousedown', onDoc);
    window.addEventListener('scroll', onScroll, true);
    window.addEventListener('resize', onResize);
    document.addEventListener('keydown', onKey, true);
    return () => {
      document.removeEventListener('mousedown', onDoc);
      window.removeEventListener('scroll', onScroll, true);
      window.removeEventListener('resize', onResize);
      document.removeEventListener('keydown', onKey, true);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  return { open, pos, btnRef, popRef, openMenu, close, toggle };
}

function ssListNav(popRef, close) {
  const optBtns = () =>
    popRef.current ? Array.from(popRef.current.querySelectorAll('.odc-select-opt:not([disabled])')) : [];
  const focusAt = (idx) => {
    const btns = optBtns();
    if (!btns.length) return;
    btns[Math.min(Math.max(idx, 0), btns.length - 1)].focus();
  };
  const onListKey = (e) => {
    const btns = optBtns();
    const idx = btns.indexOf(document.activeElement);
    switch (e.key) {
      case 'ArrowDown': e.preventDefault(); focusAt(idx + 1); break;
      case 'ArrowUp': e.preventDefault(); focusAt(idx - 1); break;
      case 'Home': e.preventDefault(); focusAt(0); break;
      case 'End': e.preventDefault(); focusAt(btns.length - 1); break;
      case 'Tab': close(false); break;
      default: break;
    }
  };
  return { focusAt, onListKey, optBtns };
}

export function SortSelect({
  fields = [],
  sort,
  onSort,
  variant = 'split',
  label = 'Sort by',
  disabled = false,
  className = '',
  id,
}) {
  const { useEffect } = React;
  const autoId = React.useId();
  const baseId = id || autoId;
  const listId = `${baseId}-listbox`;

  const first = fields[0];
  const cur = sort && sort.key
    ? sort
    : (first ? { key: first.key, dir: SortHelpers.defaultDir(first) } : { key: null, dir: 'asc' });
  // A header click can activate a sortable column outside the curated list —
  // reflect it honestly (key as label) rather than showing the wrong field.
  const known = fields.find((f) => f.key === cur.key);
  const field = known
    || (sort && sort.key ? { key: cur.key, label: cur.key.charAt(0).toUpperCase() + cur.key.slice(1), type: 'text' } : first);
  if (!field) return null;

  const dir = cur.dir === 'desc' ? 'desc' : 'asc';
  const typed = SortHelpers.dirLabel(field.type, dir);
  const dirWord = dir === 'asc' ? 'ascending' : 'descending';
  const emit = (next) => { if (onSort) onSort(next); };
  const pickField = (f) => emit({ key: f.key, dir: SortHelpers.defaultDir(f) });
  const flip = () => emit({ key: field.key, dir: dir === 'asc' ? 'desc' : 'asc' });

  const pop = useSSPopover();
  const nav = ssListNav(pop.popRef, pop.close);

  // On open, focus the selected field option (or the first).
  useEffect(() => {
    if (!pop.open) return;
    const btns = nav.optBtns();
    if (!btns.length) return;
    const idx = fields.findIndex((f) => f.key === field.key);
    (btns[idx >= 0 ? idx : 0] || btns[0]).focus();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [pop.open]);

  const onTriggerKey = (e) => {
    if (disabled) return;
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') { e.preventDefault(); if (!pop.open) pop.openMenu(); }
  };

  /* ---- Width stability: every label this control could show is rendered as
     an invisible "ghost" stacked under the current one (inline-grid), so the
     control always occupies the width of its WIDEST state and the filter bar
     never reflows when the field or direction changes. ---- */
  const dirLabelSet = [...new Set(fields.flatMap((f) => ['asc', 'desc'].map((d) => SortHelpers.dirLabel(f.type, d))))];
  const stable = (current, ghosts, className) => (
    <span className={`odc-sortsel-stable${className ? ' ' + className : ''}`}>
      <span className="odc-sortsel-cur">{current}</span>
      {ghosts.map((g, i) => <span key={i} className="odc-sortsel-ghost" aria-hidden="true">{g}</span>)}
    </span>
  );

  /* ---- Direction affordances (shared by variants) ---- */
  const dirToggle = (
    <button
      type="button"
      className="odc-sortsel-dir"
      disabled={disabled}
      aria-label={`Sort ${dirWord} — ${typed}. Toggle direction`}
      title={`Sorted ${dirWord}`}
      onClick={flip}
    >
      <span className="material-icons" aria-hidden="true">{dir === 'asc' ? 'arrow_upward' : 'arrow_downward'}</span>
      {stable(typed, dirLabelSet, 'odc-sortsel-dirlabel')}
    </button>
  );

  const dirSeg = (
    <div className="odc-seg odc-sortsel-seg" role="radiogroup" aria-label={`Sort direction for ${field.label}`}>
      {['asc', 'desc'].map((d) => (
        <button
          key={d}
          type="button"
          role="radio"
          aria-checked={dir === d}
          className="odc-seg-btn"
          disabled={disabled}
          aria-label={`Sort ${d === 'asc' ? 'ascending' : 'descending'} — ${SortHelpers.dirLabel(field.type, d)}`}
          onClick={() => dir !== d && emit({ key: field.key, dir: d })}
        >
          <span className="material-icons" aria-hidden="true">{d === 'asc' ? 'arrow_upward' : 'arrow_downward'}</span>
          {stable(SortHelpers.dirLabel(field.type, d), fields.map((f) => SortHelpers.dirLabel(f.type, d)))}
        </button>
      ))}
    </div>
  );

  /* ---- Field option row (shared by split/segmented list + menu) ---- */
  const fieldOpt = (f) => {
    const on = f.key === field.key;
    return (
      <li key={f.key}>
        <button
          type="button"
          role="option"
          aria-selected={on}
          tabIndex={-1}
          className={`odc-select-opt${on ? ' selected' : ''}`}
          onClick={() => { pickField(f); pop.close(true); }}
        >
          <span className="odc-select-tick">
            {on ? <span className="material-icons" aria-hidden="true">check</span> : null}
          </span>
          <span className="odc-select-opt-label">{f.label}</span>
        </button>
      </li>
    );
  };

  /* ---- Combined-menu variant: one trigger, fields + direction in one pop ---- */
  if (variant === 'menu') {
    return (
      <div className={`odc-sortsel menu${className ? ' ' + className : ''}`}>
        <button
          type="button"
          id={baseId}
          ref={pop.btnRef}
          className={`odc-sortsel-trigger${pop.open ? ' open' : ''}`}
          disabled={disabled}
          aria-haspopup="listbox"
          aria-expanded={pop.open}
          aria-controls={pop.open ? listId : undefined}
          onClick={pop.toggle}
          onKeyDown={onTriggerKey}
        >
          <span className="material-icons odc-sortsel-glyph" aria-hidden="true">swap_vert</span>
          <span className="odc-sortsel-prefix">{label}</span>
          {stable(`${field.label} · ${typed}`, fields.flatMap((f) => ['asc', 'desc'].map((d) => `${f.label} · ${SortHelpers.dirLabel(f.type, d)}`)), 'odc-sortsel-val')}
          <span className="material-icons odc-select-chev" aria-hidden="true">expand_more</span>
        </button>
        {pop.open && pop.pos ? (
          <ul className="odc-select-pop" id={listId} ref={pop.popRef} role="listbox"
            aria-label={label} style={{ top: pop.pos.top, left: pop.pos.left, minWidth: Math.max(pop.pos.width, 220) }}
            onKeyDown={nav.onListKey}>
            <li className="odc-sortsel-group" role="presentation">Field</li>
            {fields.map(fieldOpt)}
            <li className="odc-sortsel-sep" role="presentation"></li>
            <li className="odc-sortsel-group" role="presentation">Direction</li>
            {['asc', 'desc'].map((d) => {
              const on = dir === d;
              return (
                <li key={d}>
                  <button type="button" role="option" aria-selected={on} tabIndex={-1}
                    className={`odc-select-opt${on ? ' selected' : ''}`}
                    aria-label={`Sort ${d === 'asc' ? 'ascending' : 'descending'} — ${SortHelpers.dirLabel(field.type, d)}`}
                    onClick={() => { emit({ key: field.key, dir: d }); pop.close(true); }}>
                    <span className="odc-select-tick">
                      {on ? <span className="material-icons" aria-hidden="true">check</span> : null}
                    </span>
                    <span className="material-icons odc-opt-icon" aria-hidden="true">{d === 'asc' ? 'arrow_upward' : 'arrow_downward'}</span>
                    <span className="odc-select-opt-label">{SortHelpers.dirLabel(field.type, d)}</span>
                  </button>
                </li>
              );
            })}
          </ul>
        ) : null}
      </div>
    );
  }

  /* ---- split (spec default) & segmented: field select + direction control ---- */
  return (
    <div className={`odc-sortsel ${variant}${className ? ' ' + className : ''}`}>
      <div className="odc-sortsel-field">
        <button
          type="button"
          id={baseId}
          ref={pop.btnRef}
          className={`odc-sortsel-trigger${pop.open ? ' open' : ''}`}
          disabled={disabled}
          aria-haspopup="listbox"
          aria-expanded={pop.open}
          aria-controls={pop.open ? listId : undefined}
          onClick={pop.toggle}
          onKeyDown={onTriggerKey}
        >
          <span className="odc-sortsel-prefix">{label}</span>
          {stable(field.label, fields.map((f) => f.label), 'odc-sortsel-val')}
          <span className="material-icons odc-select-chev" aria-hidden="true">expand_more</span>
        </button>
        {pop.open && pop.pos ? (
          <ul className="odc-select-pop" id={listId} ref={pop.popRef} role="listbox"
            aria-label={label} style={{ top: pop.pos.top, left: pop.pos.left, minWidth: Math.max(pop.pos.width, 180) }}
            onKeyDown={nav.onListKey}>
            {fields.map(fieldOpt)}
          </ul>
        ) : null}
      </div>
      {variant === 'segmented' ? dirSeg : dirToggle}
    </div>
  );
}
