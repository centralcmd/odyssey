/**
 * Odyssey DS — Pager
 * The shared list pager (spec: "OdsPager") that sits below every server-paged
 * FLAT-TABLE list page (Transactions, Files, Users, Contacts, Currencies,
 * Exchange rates, Transaction tags, …). It is the CANONICAL home of the
 * rows-per-page control — always rendered, even on a table with no toolbar — so
 * a page can never end up with no way to change the page size.
 *
 * Anatomy (left → right): a rows-per-page selector + the one canonical summary
 * ("Showing X–Y of N", or "0 results" when empty), then the nav cluster —
 * first / previous / next / last.
 *
 * Rows-per-page presets are 25 · 100 · 1000 · All (default 25). "All" fetches
 * every matching row (the client virtualizes them); the pager then reports one
 * page. Changing the size resets the page to 1 — the OWNER page does that, the
 * same way it resets on a filter/search/sort change. When the page also has a
 * search bar, mount a `PageSizeSelect` mirror in the toolbar bound to the SAME
 * pageSize state; the two stay in sync because they read/write one value.
 *
 * The summary is rendered as TEXT (never colour/icon alone). It is also the
 * string a page pushes to its `LiveAnnouncer`; the Pager does not own a
 * page-level live region (single-owner rule) — pass `announce` only if the page
 * has no announcer of its own.
 *
 * Accessibility contract (pinned, spec §3.4):
 *   • a `<nav aria-label>` landmark ("Pagination" by default);
 *   • first / prev / next / last are real <button>s with plain-text names;
 *   • AT A BOUND a nav button stays FOCUSABLE + ENABLED but `aria-disabled="true"`
 *     and its activation is a NO-OP — it is never given the native `disabled`
 *     attribute (which would drop it from the tab order and lose focus,
 *     violating WCAG 2.4.3);
 *   • activating Prev/Next keeps focus on the pressed button; if that press
 *     reaches a bound (button becomes aria-disabled) focus moves to the
 *     opposite, still-active button so focus is never lost;
 *   • hit targets ≥ 24×24 px, visible :focus-visible ring.
 *
 * Controlled: pass `page` (1-based) + `pageSize` (number | 'all') + `totalCount`
 * and handle `onPageChange(nextPage)` + `onPageSizeChange(nextSize)`.
 * `TotalPages` is derived, never passed.
 */

/* ---- Internal: the rows-per-page dropdown. Self-contained (opens UPWARD, since
   the pager lives at the bottom of a list); bundle components can't reference
   each other, so the toolbar `PageSizeSelect` mirror carries its own copy. ---- */
function PagerSizeMenu({ value, options, onChange, disabled }) {
  const { useState, useRef, useEffect } = React;
  const [open, setOpen] = useState(false);
  const [menuStyle, setMenuStyle] = useState(null);
  const triggerRef = useRef(null);
  const menuRef = useRef(null);
  // Fixed-positioned menu (escapes the card/table overflow); opens UPWARD since
  // the pager sits at the bottom of the list.
  const place = () => {
    const t = triggerRef.current;
    if (!t) return;
    const r = t.getBoundingClientRect();
    const w = Math.max(150, Math.round(r.width));
    setMenuStyle({
      position: 'fixed',
      bottom: `${Math.round(window.innerHeight - r.top + 6)}px`,
      left: `${Math.max(8, Math.round(r.right - w))}px`,
      minWidth: `${w}px`,
    });
  };
  useEffect(() => {
    if (!open) return undefined;
    const onDoc = (e) => {
      if (triggerRef.current && triggerRef.current.contains(e.target)) return;
      if (menuRef.current && menuRef.current.contains(e.target)) return;
      setOpen(false);
    };
    const onKey = (e) => { if (e.key === 'Escape') setOpen(false); };
    const onReflow = () => setOpen(false);
    document.addEventListener('mousedown', onDoc);
    document.addEventListener('keydown', onKey);
    window.addEventListener('scroll', onReflow, true);
    window.addEventListener('resize', onReflow);
    return () => {
      document.removeEventListener('mousedown', onDoc);
      document.removeEventListener('keydown', onKey);
      window.removeEventListener('scroll', onReflow, true);
      window.removeEventListener('resize', onReflow);
    };
  }, [open]);
  const toggle = () => { if (open) setOpen(false); else { place(); setOpen(true); } };
  const fmt = (v) => (v === 'all' ? 'All' : Number(v).toLocaleString());
  return (
    <div className="odc-rpp">
      <button
        ref={triggerRef}
        type="button"
        className="odc-rpp-trigger"
        aria-haspopup="listbox"
        aria-expanded={open ? 'true' : 'false'}
        aria-label={`Rows per page: ${fmt(value)}`}
        disabled={disabled || undefined}
        onClick={toggle}
      >
        <b>{fmt(value)}</b>
        <span className="material-icons odc-rpp-chev" aria-hidden="true">{open ? 'expand_less' : 'expand_more'}</span>
      </button>
      {open ? (
        <ul ref={menuRef} className="odc-rpp-menu" role="listbox" style={menuStyle}>
          {options.map((o) => (
            <li key={String(o)} role="option" aria-selected={o === value ? 'true' : 'false'}>
              <button
                type="button"
                className={`odc-rpp-opt${o === value ? ' sel' : ''}`}
                onClick={() => { if (onChange) onChange(o); setOpen(false); }}
              >
                <span>{fmt(o)}</span>
                {o === value ? <span className="material-icons" aria-hidden="true">check</span> : <span aria-hidden="true" />}
              </button>
            </li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}

export function Pager({
  page = 1,
  pageSize = 25,
  pageSizeOptions = [25, 100, 1000, 'all'],
  totalCount = 0,
  onPageChange,
  onPageSizeChange,
  showPageSize = true,
  pageSizeLabel = 'Rows per page',
  loading = false,
  label = 'Pagination',
  announce = false,
  className = '',
  id,
}) {
  const { useRef, useLayoutEffect } = React;

  const all = pageSize === 'all';
  const size = all ? Math.max(totalCount, 1) : pageSize;
  const empty = totalCount === 0;
  const totalPages = empty ? 0 : (all ? 1 : Math.ceil(totalCount / size));
  const atFirst = page <= 1 || empty;
  const atLast = page >= totalPages || empty;

  // The single canonical summary string — also what the page announces.
  const first = empty ? 0 : (all ? 1 : (page - 1) * size + 1);
  const last = empty ? 0 : (all ? totalCount : Math.min(page * size, totalCount));
  const summary = empty
    ? '0 results'
    : `Showing ${first.toLocaleString()}\u2013${last.toLocaleString()} of ${totalCount.toLocaleString()}`;

  const prevRef = useRef(null);
  const nextRef = useRef(null);
  const pressed = useRef(null); // 'prev' | 'next' — which button initiated the last nav

  // After a nav settles: if the button the user pressed has reached a bound
  // (now aria-disabled), move focus to the opposite still-active button so
  // focus is never stranded on an inert control.
  useLayoutEffect(() => {
    const who = pressed.current;
    pressed.current = null;
    if (!who || loading) return;
    if (who === 'prev' && atFirst && !atLast && nextRef.current) nextRef.current.focus();
    else if (who === 'next' && atLast && !atFirst && prevRef.current) prevRef.current.focus();
  }, [page, atFirst, atLast, loading]);

  const goto = (target, dir, bound) => {
    if (loading) return; // in-flight: activation is a no-op
    if (bound) return; // aria-disabled no-op
    const t = Math.max(1, Math.min(totalPages, target));
    if (t === page) return;
    pressed.current = dir;
    if (onPageChange) onPageChange(t);
  };

  const btn = (icon, name, bound, onGo, ref) => (
    <button
      ref={ref}
      type="button"
      className="odc-pager-btn"
      aria-label={name}
      aria-disabled={bound || loading ? 'true' : undefined}
      onClick={onGo}
    >
      <span className="material-icons" aria-hidden="true">{icon}</span>
    </button>
  );

  return (
    <nav
      className={`odc-pager${className ? ' ' + className : ''}`}
      aria-label={label}
      aria-busy={loading ? 'true' : undefined}
      id={id}
    >
      <div className="odc-pager-left">
        {showPageSize ? (
          <div className="odc-pager-size">
            <span className="odc-pager-size-label">{pageSizeLabel}</span>
            <PagerSizeMenu value={pageSize} options={pageSizeOptions} onChange={onPageSizeChange} disabled={loading} />
          </div>
        ) : null}
        <span
          className="odc-pager-summary"
          aria-live={announce ? 'polite' : undefined}
        >
          {loading ? <span className="odc-pager-spin" aria-hidden="true" /> : null}
          {summary}
        </span>
      </div>
      <div className="odc-pager-nav">
        {btn('first_page', 'First page', atFirst, () => goto(1, 'prev', atFirst))}
        {btn('chevron_left', 'Previous page', atFirst, () => goto(page - 1, 'prev', atFirst), prevRef)}
        {btn('chevron_right', 'Next page', atLast, () => goto(page + 1, 'next', atLast), nextRef)}
        {btn('last_page', 'Last page', atLast, () => goto(totalPages, 'next', atLast))}
      </div>
    </nav>
  );
}
