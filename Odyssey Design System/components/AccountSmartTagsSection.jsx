/**
 * Odyssey DS — AccountSmartTagsSection
 * The per-account "Smart tags" disclosure that lives in the expanded account
 * record, below the Transactions section. It pins a curated set of existing
 * TransactionTags to an account as a saved filter, then surfaces every
 * transaction on that account carrying any of those tags — a persistent,
 * per-account watchlist that saves the user re-filtering the Transactions page.
 *
 * Composite, but self-contained: it renders its own `.odc-collapsible` shell
 * (bundle components can't import each other), owns the tag-management bar
 * (removable chips + an "Add tag" checklist popover that maps check→add /
 * uncheck→remove, mirroring the individual POST/DELETE endpoints) and the
 * five UX states from the spec — NoSmartTags, Loading, NoTransactions,
 * HasTransactions, and an error/retry panel. The transaction table itself is
 * injected via `renderTable(transactions)` so the section stays decoupled from
 * TxnTable's render contract; the consumer passes `<TxnTable hideAccount …/>`.
 *
 * Data-prop driven — nothing global:
 *   tags        configured smart tags: [{id,label|name}] (or plain strings)
 *   tagOptions  every selectable tag:  [{value,label}] / [{id,name}] / strings
 *   transactions  the already-filtered matches (used for count + empty test)
 *   onAddTag(id) / onRemoveTag(id)   fire one tag at a time (idempotent API)
 *   canWrite    gates every add/remove control (read-only viewers still see
 *               the chips + table); loading / error / onRetry drive the states.
 *
 * Header count mirrors `AccountTransactionsSection` — the number of matching
 * transactions, shown as the muted `.odc-collapsible-count` pill. Maps to an
 * OdsCollapsible + the tag chips + OdsTxnTable in the Blazor section.
 *
 * Styled by `.odc-smarttags*` (components.css); chips are the shared
 * `.odc-chip.tag` atom.
 */

/* ---- odcUsePopover — fixed-position, portaled popover (inlined; bundle
   components are standalone and can't import each other). Measures the trigger,
   renders into <body>, flips up when cramped, closes on outside-click + Esc. */
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
  return { open, setOpen, anchorRef, popRef, floatStyle };
}

/* ---- The "Add tag" / manage-tags control: a button that opens a searchable,
   checkable list of every available tag. Checking adds (onAddTag), unchecking
   removes (onRemoveTag) — one call per toggle, matching the per-tag endpoints.
   Disabled tags past the cap can't be newly checked. */
function SmartTagAdder({ options, selectedIds, onAddTag, onRemoveTag, atCap, label = 'Add tag', emptyText = 'No tags match' }) {
  const { useState, useRef, useEffect } = React;
  const { open, setOpen, anchorRef, popRef, floatStyle } = odcUsePopover({ align: 'start' });
  const [query, setQuery] = useState('');
  const inputRef = useRef(null);

  useEffect(() => {
    if (open) setTimeout(() => inputRef.current && inputRef.current.focus(), 20);
    else setQuery('');
  }, [open]);

  const sel = new Set(selectedIds);
  const q = query.trim().toLowerCase();
  const filtered = q ? options.filter((o) => o.label.toLowerCase().includes(q)) : options;

  const toggle = (o) => {
    if (sel.has(o.value)) { if (onRemoveTag) onRemoveTag(o.value); }
    else { if (atCap || !onAddTag) return; onAddTag(o.value); }
  };

  return (
    <span className="odc-smarttags-adder" ref={anchorRef}>
      <button
        type="button"
        className={`odc-smarttags-add${open ? ' open' : ''}`}
        aria-haspopup="listbox"
        aria-expanded={open}
        onClick={() => setOpen((o) => !o)}
      >
        <span className="material-icons" aria-hidden="true">{open ? 'expand_less' : 'add'}</span>
        <span>{label}</span>
      </button>

      {open
        ? ReactDOM.createPortal(
          <div className="odc-smarttags-pop" role="listbox" aria-multiselectable="true" ref={popRef} style={floatStyle}>
            <div className="odc-smarttags-search">
              <span className="material-icons" aria-hidden="true">search</span>
              <input
                ref={inputRef}
                value={query}
                placeholder="Search tags…"
                onChange={(e) => setQuery(e.target.value)}
              />
            </div>
            <div className="odc-smarttags-list">
              {filtered.map((o) => {
                const checked = sel.has(o.value);
                const blocked = !checked && atCap;
                return (
                  <label className={`odc-smarttags-opt odc-check${blocked ? ' blocked' : ''}`} key={o.value}>
                    <input type="checkbox" checked={checked} disabled={blocked} onChange={() => toggle(o)} />
                    <span className="odc-check-box" aria-hidden="true">
                      <span className="material-icons">check</span>
                    </span>
                    <span className="odc-check-label">{o.label}</span>
                  </label>
                );
              })}
              {filtered.length === 0 ? <div className="odc-smarttags-empty-opt">{emptyText}</div> : null}
            </div>
            {atCap ? <div className="odc-smarttags-cap">Tag limit reached — remove one to add another.</div> : null}
          </div>,
          document.body,
        )
        : null}
    </span>
  );
}

export function AccountSmartTagsSection({
  tags = [],
  tagOptions = [],
  transactions = [],
  onAddTag,
  onRemoveTag,
  canWrite = true,
  loading = false,
  error = null,
  onRetry,
  renderTable,
  formatAmount,
  amountOf,
  maxTags = 20,
  title = 'Smart tags',
  icon = 'sell',
  open,
  defaultOpen = false,
  onToggle,
  /** false = bare: no disclosure shell, no header. The host introduces the
      section with its own SectionDivider (RecordCard bodies). */
  chrome = true,
  className = '',
}) {
  const { useState } = React;
  const isControlled = open !== undefined;
  const [internal, setInternal] = useState(defaultOpen);
  const isOpen = isControlled ? open : internal;
  const rid = React.useId();
  const bodyId = `${rid}-body`;

  const toggleOpen = () => {
    const next = !isOpen;
    if (!isControlled) setInternal(next);
    if (onToggle) onToggle(next);
  };

  // Normalize configured tags + the option pool to a common {value,label} shape.
  const cfg = (tags || [])
    .map((t) => (typeof t === 'string' ? { value: t, label: t } : { value: t.id != null ? t.id : t.value, label: t.label != null ? t.label : t.name }))
    .filter((t) => t.value != null);
  const cfgIds = cfg.map((t) => t.value);
  const opts = (tagOptions || [])
    .map((o) => (typeof o === 'string' ? { value: o, label: o } : { value: o.value != null ? o.value : o.id, label: o.label != null ? o.label : o.name }))
    .filter((o) => o.value != null);

  const hasTags = cfg.length > 0;
  const matchCount = transactions.length;
  const atCap = cfg.length >= maxTags;
  // The header pill: matching-transaction count once tags exist and we're settled.
  const showCount = hasTags && !loading && !error;

  // Net total of the watched transactions (income positive, expense negative).
  // `amountOf(t)` extracts the signed number (default: t.amount); `formatAmount(n)`
  // renders it (default: a signed "$ x.xx"). The total shows on the bar once
  // there are matches — a per-account at-a-glance figure for the watched tags.
  const getAmount = typeof amountOf === 'function' ? amountOf : (t) => (t && typeof t.amount === 'number' ? t.amount : 0);
  const total = transactions.reduce((s, t) => s + (getAmount(t) || 0), 0);
  const fmtAmount = typeof formatAmount === 'function'
    ? formatAmount
    : (n) => `${n < 0 ? '−' : '+'}$ ${Math.abs(n).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
  const showTotal = hasTags && !loading && !error && matchCount > 0;

  // ---- Body, by state (mirrors the spec's NoSmartTags / Loading /
  //      NoTransactions / HasTransactions / error matrix). ----
  let body;
  if (error) {
    body = (
      <div className="odc-smarttags-state error" role="alert">
        <span className="material-icons odc-smarttags-state-ic" aria-hidden="true">error_outline</span>
        <div className="odc-smarttags-state-txt">{error}</div>
        {onRetry ? (
          <button type="button" className="odc-btn outlined sm" onClick={onRetry}>
            <span className="material-icons" aria-hidden="true">refresh</span><span>Try again</span>
          </button>
        ) : null}
      </div>
    );
  } else if (!hasTags) {
    body = (
      <div className="odc-empty odc-smarttags-empty">
        <div className="odc-empty-ic"><span className="material-icons" aria-hidden="true">sell</span></div>
        <div className="odc-empty-ttl">No smart tags yet</div>
        <div className="odc-empty-desc">
          {canWrite
            ? 'Pin a tag to watch its transactions on this account without re-filtering the ledger.'
            : 'No tags are being watched on this account.'}
        </div>
        {canWrite ? (
          <div className="odc-empty-actions">
            <SmartTagAdder
              options={opts}
              selectedIds={cfgIds}
              onAddTag={onAddTag}
              onRemoveTag={onRemoveTag}
              atCap={atCap}
              label="Add a tag"
            />
          </div>
        ) : null}
      </div>
    );
  } else if (loading) {
    body = (
      <div className="odc-smarttags-loading" role="status" aria-live="polite">
        <div className="odc-progress indeterminate"><div className="odc-progress-fill" /></div>
        <div className="odc-smarttags-loading-txt">Loading transactions…</div>
      </div>
    );
  } else if (matchCount === 0) {
    body = (
      <div className="odc-empty odc-smarttags-empty">
        <div className="odc-empty-ic muted"><span className="material-icons" aria-hidden="true">search_off</span></div>
        <div className="odc-empty-ttl">No matching transactions</div>
        <div className="odc-empty-desc">No transactions on this account carry the selected tags.</div>
      </div>
    );
  } else {
    body = (
      <div className="odc-smarttags-table">
        {typeof renderTable === 'function' ? renderTable(transactions) : null}
      </div>
    );
  }

  const inner = (
    <SmartTagsInner hasTags={hasTags} cfg={cfg} cfgIds={cfgIds} canWrite={canWrite} opts={opts} atCap={atCap}
      onAddTag={onAddTag} onRemoveTag={onRemoveTag} showTotal={showTotal} total={total}
      matchCount={matchCount} fmtAmount={fmtAmount} body={body} />
  );

  // Bare form (chrome={false}): no disclosure shell and no header — the host
  // introduces the section with its own SectionDivider. Used inside RecordCard
  // bodies, where sections do not collapse and do not act.
  if (chrome === false) {
    return (
      <div className={`odc-smarttags bare${className ? ' ' + className : ''}`}>
        {inner}
      </div>
    );
  }

  return (
    <div className={`odc-collapsible odc-smarttags${className ? ' ' + className : ''}`} data-open={isOpen ? '' : undefined}>
      <div className="odc-collapsible-head">
        <button
          type="button"
          className="odc-collapsible-trigger"
          aria-expanded={isOpen}
          aria-controls={bodyId}
          onClick={toggleOpen}
        >
          <span className="material-icons odc-collapsible-chev" aria-hidden="true">expand_more</span>
          <span className="material-icons odc-collapsible-lead" aria-hidden="true">{icon}</span>
          <span className="odc-collapsible-title">{title}</span>
          {showCount ? <span className="odc-collapsible-count">{matchCount}</span> : null}
        </button>
      </div>

      {isOpen ? (
        <div className="odc-collapsible-body" id={bodyId}>
          {inner}
        </div>
      ) : null}
    </div>
  );
}

function SmartTagsInner({ hasTags, cfg, cfgIds, canWrite, opts, atCap, onAddTag, onRemoveTag, showTotal, total, matchCount, fmtAmount, body }) {
  return (
    <React.Fragment>
          {/* Tag-management bar — shown whenever tags exist (or a writer can add
              the first from the empty state below). Chips remove inline; the
              adder opens the full checklist. */}
          {hasTags ? (
            <div className="odc-smarttags-bar">
              <div className="odc-smarttags-bar-main">
                <span className="odc-smarttags-bar-label">Watching</span>
                <div className="odc-smarttags-chips">
                  {cfg.map((t) => (
                    <span className="odc-chip tag odc-smarttags-chip" key={t.value}>
                      <span className="material-icons odc-smarttags-chip-ic" aria-hidden="true">sell</span>
                      {t.label}
                      {canWrite ? (
                        <button
                          type="button"
                          className="odc-smarttags-x"
                          aria-label={`Stop watching ${t.label}`}
                          onClick={() => onRemoveTag && onRemoveTag(t.value)}
                        >
                          <span className="material-icons" aria-hidden="true">close</span>
                        </button>
                      ) : null}
                    </span>
                  ))}
                  {canWrite ? (
                    <SmartTagAdder
                      options={opts}
                      selectedIds={cfgIds}
                      onAddTag={onAddTag}
                      onRemoveTag={onRemoveTag}
                      atCap={atCap}
                    />
                  ) : null}
                </div>
              </div>
              {showTotal ? (
                <div className={`odc-smarttags-total ${total < 0 ? 'expense' : 'income'}`}>
                  <span className="odc-smarttags-total-lab">{matchCount} {matchCount === 1 ? 'transaction' : 'transactions'}</span>
                  <span className="odc-smarttags-total-val mono">{fmtAmount(total)}</span>
                </div>
              ) : null}
            </div>
          ) : null}

          {body}
    </React.Fragment>
  );
}
