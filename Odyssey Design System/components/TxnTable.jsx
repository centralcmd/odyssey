/**
 * Odyssey DS — TxnTable
 * THE transactions ledger — the sortable, expandable, editable table rendered
 * by the Transactions page, the Accounts page's per-account section, the
 * Budgets matched-transactions panel and the Dashboard's recent list. It owns
 * the canonical column set (avatar · Description · Contact · Account ·
 * Tag · Status · Amount · Date · row menu), the status→tone chip mapping and
 * the income/expense amount encoding, so every surface renders a transaction
 * row identically.
 *
 * Data-prop driven — rows are plain denormalized objects; nothing global:
 *   { id, desc, status, amount, date, dir?, icon?, currency?,
 *     contact?, accountLabel?, accountNumber?, tags?, tagLabel? }
 *   • dir defaults from the sign of `amount` (income ≥ 0, expense < 0)
 *   • contact defaults to the leading "·" segment of `desc`
 *   • tags is the multi-tag set — an array of label strings or {id,label}
 *     objects; the Tag column shows up to TT_TAG_CAP chips then a "+N"
 *     overflow. `tagLabel` (single) is still honored as a one-element
 *     fallback so older callers keep working.
 *
 * View machinery (same contract as RecordTable): sorting via the header
 * buttons — uncontrolled by default, or CONTROLLED by binding `sort`
 * ({key,dir}) + `onSortChange` so a toolbar SortSelect and the headers share
 * one state; header clicks raise a complete {key,dir}, and a column CHANGE
 * resolves its direction from the column's data type (text→asc, amount→desc,
 * date→desc, status→asc — the shared SortHelpers rule). Click a row to
 * expand into `renderDetail(t)` (accordion —
 * rows mid-edit never auto-collapse); `renderEdit(t, { save, cancel })` swaps
 * in over the detail; `save(patch)` calls `onSave(id, patch)` and flashes a
 * "Saved" chip. The row menu defaults to View / Edit / Copy ID / Delete —
 * pass `actions(t, ctx)` to replace it.
 *
 * Formatting is injectable: `formatAmount(t)` (default: signed Intl currency
 * via t.currency) and `formatDate(iso)` (default: "Apr 12, 2026").
 * `statusTones` maps status → Chip tone (default New·info / Approved·income /
 * Flagged·expense). No pagination — the MVP renders the filtered list whole.
 *
 * Styled with the kit's `.ua-tbl` table classes (kit.css + admin.css) plus the
 * `.odc-chip` / `.odc-avatar` atoms. Maps to a MudTable with sort labels and
 * expandable detail rows.
 */

/* ---- Internal: row overflow menu — self-contained copy of the ActionMenu
   atom (bundle components can't reference each other). ---- */
function TTMenu({ items }) {
  const { useState, useRef, useEffect } = React;
  const [open, setOpen] = useState(false);
  const [pos, setPos] = useState(null);
  const ref = useRef(null);
  const btnRef = useRef(null);

  const toggle = () => {
    if (!open && btnRef.current) {
      const r = btnRef.current.getBoundingClientRect();
      setPos({ top: r.bottom + 4, right: window.innerWidth - r.right });
    }
    setOpen((o) => !o);
  };

  useEffect(() => {
    if (!open) return;
    const onDoc = (e) => { if (ref.current && !ref.current.contains(e.target)) setOpen(false); };
    const close = () => setOpen(false);
    // Esc closes the menu and restores trigger focus — capture + stop so an
    // enclosing Modal doesn't close with it.
    const onKey = (e) => {
      if (e.key !== 'Escape') return;
      e.stopPropagation();
      setOpen(false);
      const b = btnRef.current && btnRef.current.querySelector('button');
      if (b) b.focus();
    };
    document.addEventListener('mousedown', onDoc);
    document.addEventListener('keydown', onKey, true);
    window.addEventListener('scroll', close, true);
    window.addEventListener('resize', close);
    return () => {
      document.removeEventListener('mousedown', onDoc);
      document.removeEventListener('keydown', onKey, true);
      window.removeEventListener('scroll', close, true);
      window.removeEventListener('resize', close);
    };
  }, [open]);

  // Menu keyboard pattern (matches Menu.jsx): focus moves to the first item on
  // open; ↑/↓ rove (wrapping), Home/End jump, activating restores the trigger.
  const popRef = useRef(null);
  const itemBtns = () =>
    popRef.current ? Array.from(popRef.current.querySelectorAll('.acct-menu-item:not([disabled])')) : [];
  const focusAt = (idx) => {
    const btns = itemBtns();
    if (!btns.length) return;
    btns[((idx % btns.length) + btns.length) % btns.length].focus();
  };
  useEffect(() => {
    if (open) requestAnimationFrame(() => focusAt(0));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);
  const closeMenu = (restore) => {
    setOpen(false);
    if (restore && btnRef.current) {
      const b = btnRef.current.querySelector('button');
      if (b) b.focus();
    }
  };
  const onPopKey = (e) => {
    const btns = itemBtns();
    const idx = btns.indexOf(document.activeElement);
    switch (e.key) {
      case 'ArrowDown': e.preventDefault(); focusAt(idx + 1); break;
      case 'ArrowUp': e.preventDefault(); focusAt(idx - 1); break;
      case 'Home': e.preventDefault(); focusAt(0); break;
      case 'End': e.preventDefault(); focusAt(btns.length - 1); break;
      case 'Tab': closeMenu(false); break;
      default: break;
    }
  };

  return (
    <div className="acct-menu" ref={ref} onClick={(e) => e.stopPropagation()}>
      <span ref={btnRef}>
        <button type="button" className="odc-iconbtn" aria-label="More actions"
          aria-haspopup="menu" aria-expanded={open} onClick={toggle}>
          <span className="material-icons" aria-hidden="true">more_vert</span>
        </button>
      </span>
      {open && pos && (
        <div className="acct-menu-pop" role="menu" ref={popRef} style={{ top: pos.top, right: pos.right }} onKeyDown={onPopKey}>
          {items.map((it, i) => it.divider ? (
            <div key={i} className="acct-menu-sep" />
          ) : (
            <button key={i} role="menuitem" tabIndex={-1} className={`acct-menu-item ${it.danger ? 'danger' : ''}`}
              onClick={() => { closeMenu(true); it.onClick && it.onClick(); }}>
              <span className="material-icons" aria-hidden="true" style={{ fontSize: 18 }}>{it.icon}</span>
              <span>{it.label}</span>
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

/* ---- Internal: sortable <th> (SortHeader's markup, inlined) ---- */
function TTSort({ label, sortKey, sort, onSort, align }) {
  const active = sort.key === sortKey;
  return (
    <th scope="col" className={align === 'right' ? 'numeric' : ''}
      aria-sort={active ? (sort.dir === 'asc' ? 'ascending' : 'descending') : 'none'}>
      <button type="button" className={`ua-sort ${align === 'right' ? 'right' : ''} ${active ? 'active' : ''}`} onClick={() => onSort(sortKey)}>
        <span>{label}</span>
        <span className={`material-icons ua-sort-ic ${active ? 'active' : ''} ${active && sort.dir === 'desc' ? 'desc' : ''}`} aria-hidden="true" style={{ fontSize: 16 }}>arrow_upward</span>
      </button>
    </th>
  );
}

const TT_DEFAULT_TONES = { New: 'info', Approved: 'income', Flagged: 'expense' };

const ttDir = (t) => t.dir || (t.amount >= 0 ? 'income' : 'expense');
const ttContact = (t) => (t.contact != null && t.contact !== '')
  ? t.contact
  : ((t.desc || '').split(' · ')[0] || '').trim();

/* Normalize a row's tag set → [{id,label}]. Accepts `tags` (strings or
   {id,label|name} objects); falls back to the legacy single `tagLabel`. */
const ttTags = (t) => {
  const src = (t.tags && t.tags.length) ? t.tags
    : (t.tagLabel ? [t.tagLabel] : []);
  return src
    .map((x) => (typeof x === 'string' ? { label: x } : { id: x.id, label: x.label != null ? x.label : x.name }))
    .filter((x) => x.label != null && x.label !== '');
};
/* How many tag chips show before collapsing into "+N" in the dense column. */
const TT_TAG_CAP = 2;

const ttMoney = (t) => {
  let mag;
  try { mag = new Intl.NumberFormat('en-US', { style: 'currency', currency: t.currency || 'USD' }).format(Math.abs(t.amount)); }
  catch (e) { mag = Math.abs(t.amount).toFixed(2); }
  return `${t.amount < 0 ? '−' : '+'}${mag}`;
};
const ttDate = (iso) => {
  const d = new Date(`${iso}T00:00:00`);
  return isNaN(d) ? iso : d.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
};

const ttSortVal = (t, key) => {
  switch (key) {
    case 'desc':         return (t.desc || '').toLowerCase();
    case 'contact': return ttContact(t).toLowerCase();
    case 'account':      return (t.accountLabel || '').toLowerCase();
    case 'tag': {
      const ts = ttTags(t);
      return (ts.length ? ts.map((x) => x.label).join(', ') : '~').toLowerCase();
    }
    case 'status':       return t.status || '';
    case 'amount':       return t.amount;
    case 'date':         return t.date || '';
    default:             return 0;
  }
};

/* The canonical column→data-type map — drives the default direction on a
   column change (shared SortHelpers rule: text/status→asc, number/date→desc). */
const TT_SORT_TYPES = {
  desc: 'text', contact: 'text', account: 'text', tag: 'text',
  status: 'status', amount: 'number', date: 'date',
};

export function TxnTable({
  txns = [],
  hideAccount = false,
  statusTones = TT_DEFAULT_TONES,
  formatAmount = ttMoney,
  formatDate = ttDate,
  defaultSort = { key: 'date', dir: 'desc' },
  sort: sortProp,
  onSortChange,
  actions,
  renderDetail,
  renderEdit,
  onSave,
  onDelete,
  savedFlashMs = 2200,
  empty,
  ariaLabel,
  className = '',
}) {
  const { useState, useMemo } = React;
  const [internalSort, setInternalSort] = useState(defaultSort || { key: null, dir: 'asc' });
  // Controlled sort: when `sort` is bound the parent owns the state (one
  // shared {key,dir} with the toolbar SortSelect); unbound keeps legacy.
  const controlled = sortProp !== undefined;
  const sort = controlled ? (sortProp || defaultSort || { key: null, dir: 'asc' }) : internalSort;
  const applySort = (next) => {
    if (controlled) { onSortChange && onSortChange(next); }
    else setInternalSort(next);
  };
  const [openIds, setOpenIds] = useState([]);
  const [editIds, setEditIds] = useState([]);
  const [savedIds, setSavedIds] = useState([]);

  const defaultDirFor = (key) => {
    const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
    const t = TT_SORT_TYPES[key] || 'text';
    if (NS.SortHelpers) return NS.SortHelpers.defaultDir(t);
    return { text: 'asc', status: 'asc', number: 'desc', date: 'desc' }[t];
  };

  // Sorting collapses every open row EXCEPT those mid-edit. Column change →
  // the field's natural default direction, not an unconditional asc.
  const toggleSort = (key) => {
    setOpenIds((curr) => curr.filter((id) => editIds.includes(id)));
    applySort(sort.key === key
      ? { key, dir: sort.dir === 'asc' ? 'desc' : 'asc' }
      : { key, dir: defaultDirFor(key) });
  };

  // Accordion-ish: opening a row collapses other open rows except those mid-edit.
  const openRow = (curr, id) => [...curr.filter((x) => editIds.includes(x)), id];
  const toggleRow = (id) => {
    if (!renderDetail) return;
    setOpenIds((curr) => (curr.includes(id) ? curr.filter((x) => x !== id) : openRow(curr, id)));
  };
  const startEdit = (id) => {
    if (!renderEdit) return;
    setOpenIds((curr) => (curr.includes(id) ? curr : openRow(curr, id)));
    setEditIds((curr) => (curr.includes(id) ? curr : [...curr, id]));
  };
  const endEdit = (id) => setEditIds((curr) => curr.filter((x) => x !== id));
  const doSave = (id, patch) => {
    onSave && onSave(id, patch);
    endEdit(id);
    setSavedIds((curr) => [...curr, id]);
    setTimeout(() => setSavedIds((curr) => curr.filter((x) => x !== id)), savedFlashMs);
  };
  const doDelete = (id) => {
    endEdit(id);
    setOpenIds((curr) => curr.filter((x) => x !== id));
    onDelete && onDelete(id);
  };

  const sorted = useMemo(() => {
    if (!sort.key) return txns;
    const mul = sort.dir === 'asc' ? 1 : -1;
    return [...txns].sort((a, b) => {
      const va = ttSortVal(a, sort.key), vb = ttSortVal(b, sort.key);
      if (va < vb) return -1 * mul;
      if (va > vb) return 1 * mul;
      return 0;
    });
  }, [txns, sort]);

  const colSpan = hideAccount ? 8 : 9;

  const defaultActions = (t, ctx) => [
    ...(renderDetail && !ctx.editing ? [{ icon: ctx.expanded ? 'close' : 'expand_more', label: ctx.expanded ? 'Collapse' : 'View details', onClick: ctx.toggle }] : []),
    ...(renderEdit ? [{ icon: 'edit', label: 'Edit', onClick: ctx.startEdit }] : []),
    { icon: 'fingerprint', label: 'Copy ID', trailingIcon: 'content_copy', onClick: () => { if (navigator.clipboard) navigator.clipboard.writeText(t.id); } },
    ...(onDelete ? [{ divider: true }, { icon: 'delete', label: 'Delete', danger: true, onClick: ctx.remove }] : []),
  ];

  return (
    <table className={`tbl ua-tbl ${className}`.trim()} aria-label={ariaLabel || undefined}>
      <thead>
        <tr>
          <th scope="col" style={{ width: 36 }}></th>
          <TTSort label="Description"  sortKey="desc"         sort={sort} onSort={toggleSort} />
          <TTSort label="Contact" sortKey="contact" sort={sort} onSort={toggleSort} />
          {!hideAccount && <TTSort label="Account" sortKey="account" sort={sort} onSort={toggleSort} />}
          <TTSort label="Tag"          sortKey="tag"          sort={sort} onSort={toggleSort} />
          <TTSort label="Status"       sortKey="status"       sort={sort} onSort={toggleSort} />
          <TTSort label="Amount"       sortKey="amount"       sort={sort} onSort={toggleSort} align="right" />
          <TTSort label="Date"         sortKey="date"         sort={sort} onSort={toggleSort} align="right" />
          <th scope="col" style={{ width: 96, textAlign: 'right' }}>Actions</th>
        </tr>
      </thead>
      <tbody>
        {sorted.map((t) => {
          const expanded = openIds.includes(t.id);
          const editing = editIds.includes(t.id);
          const justSaved = savedIds.includes(t.id);
          const ctx = {
            expanded,
            editing,
            toggle: () => toggleRow(t.id),
            startEdit: () => startEdit(t.id),
            remove: () => doDelete(t.id),
          };
          const dir = ttDir(t);
          const items = (actions || defaultActions)(t, ctx);
          const clickToggle = () => { if (!editing) toggleRow(t.id); };
          return (
            <React.Fragment key={t.id}>
              <tr className={`${expanded ? 'expanded' : ''} ${editing ? 'editing' : ''}`.trim() || undefined} onClick={clickToggle}>
                <td>
                  {/* Decorative — direction is conveyed by the signed amount
                      and status chip, so this glyph is aria-hidden, no img role. */}
                  <span className={`odc-avatar ${dir === 'income' ? 'mint' : 'coral'}`}>
                    <span className="material-icons" aria-hidden="true">{t.icon || (dir === 'income' ? 'arrow_downward' : 'shopping_cart')}</span>
                  </span>
                </td>
                <td>
                  <span style={{ display: 'inline-flex', alignItems: 'center', gap: 8 }}>
                    {t.desc}{justSaved && <span className="odc-chip income"><span className="odc-chip-dot"></span>Saved</span>}
                  </span>
                </td>
                <td className="muted">{ttContact(t)}</td>
                {!hideAccount && (
                  <td className="muted">{t.accountLabel || '—'} {t.accountNumber && <span style={{ opacity: 0.6 }}>{t.accountNumber}</span>}</td>
                )}
                <td>{(() => {
                  const ts = ttTags(t);
                  if (ts.length === 0) return <span className="muted">—</span>;
                  const shown = ts.slice(0, TT_TAG_CAP);
                  const hidden = ts.slice(TT_TAG_CAP);
                  return (
                    <span className="odc-tagchips">
                      {shown.map((x, i) => <span className="odc-chip tag" key={x.id || i}>{x.label}</span>)}
                      {hidden.length > 0 && <span className="odc-chip tag odc-tagchips-more" title={hidden.map((x) => x.label).join(', ')}>+{hidden.length}</span>}
                    </span>
                  );
                })()}</td>
                <td><span className={`odc-chip ${statusTones[t.status] || 'default'}`}><span className="odc-chip-dot"></span>{t.status}</span></td>
                <td className={`numeric mono ${dir}`}>{formatAmount(t)}</td>
                <td className="numeric muted">{formatDate(t.date)}</td>
                <td>
                  <div className="ua-row-actions" onClick={(e) => e.stopPropagation()}>
                    <TTMenu items={items} />
                    <button className="ua-expand-btn" aria-label={expanded ? 'Collapse row' : 'Expand row'} aria-expanded={expanded} onClick={clickToggle} disabled={editing || !renderDetail}>
                      <span className={`material-icons ua-chev ${expanded ? 'open' : ''}`} aria-hidden="true" style={{ fontSize: 22 }}>expand_more</span>
                    </button>
                  </div>
                </td>
              </tr>
              {expanded && (
                <tr className="ua-detail-row">
                  <td className="ua-detail-cell" colSpan={colSpan}>
                    {editing && renderEdit
                      ? renderEdit(t, { save: (patch) => doSave(t.id, patch), cancel: () => endEdit(t.id) })
                      : renderDetail(t, { expanded })}
                  </td>
                </tr>
              )}
            </React.Fragment>
          );
        })}
        {sorted.length === 0 && (
          <tr><td colSpan={colSpan} style={{ padding: 0 }}>{empty}</td></tr>
        )}
      </tbody>
    </table>
  );
}
