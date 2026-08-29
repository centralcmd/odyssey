/**
 * Odyssey DS — RecordTable
 * The sortable, expandable, editable record table behind every admin/ledger
 * screen (Transaction tags, Contacts, Currencies, Exchange rates, Users).
 * It owns ALL the view machinery so a page only declares its columns and
 * supplies the detail / edit panels:
 *
 *   • sortable headers — uncontrolled by default (give `defaultSort`, the
 *     table sorts via each column's `sortValue`), or CONTROLLED: bind `sort`
 *     ({key,dir}) + `onSortChange` and the parent owns the state — header
 *     clicks raise a complete {key,dir} and the toolbar SortSelect and the
 *     headers stay in sync off one value. On a column CHANGE the new
 *     direction resolves from the column's `sortType`
 *     ('text'|'number'|'date'|'status') via the shared SortHelpers default-
 *     direction rule — never an unconditional asc
 *   • click a row (or "View details") to expand it into a read-only detail
 *     panel — `renderDetail(row)`; accordion by default, `multiOpen` to keep
 *     several open
 *   • an inline Edit panel that swaps in over the detail — `renderEdit(row,
 *     { save, cancel })`; rows being edited never auto-collapse on sort
 *   • a trailing actions cell: the `more_vert` overflow menu (items from
 *     `actions(row, ctx)`) + the expand chevron
 *
 * Each column: { key, header, sortable?, sortType?('text'|'number'|'date'|'status'),
 *   defaultDir?, align?('right'), width?, className?,
 *   cell?(row, ctx), sortValue?(row) }. `cell`'s ctx is { expanded, editing,
 *   justSaved } — use justSaved to show the "Saved" flash chip.
 *
 * Self-contained (renders the overflow menu + sort headers itself); pairs with
 * the SortHeader / ActionMenu / MetaTile atoms when you hand-roll a table.
 * Maps to a MudTable with MudTableSortLabel + expandable detail rows.
 */

/* ---- Internal: the row overflow menu (kebab). Self-contained copy of the
   ActionMenu atom — bundle components can't reference each other. ---- */
function RTMenu({ items }) {
  const { useState, useRef, useEffect } = React;
  const menuNoteId = React.useId();
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
            <React.Fragment key={i}>
              <button role="menuitem" tabIndex={-1} className={`acct-menu-item ${it.danger ? 'danger' : ''}`}
                aria-disabled={it.disabled ? true : undefined}
                aria-describedby={it.note ? `${menuNoteId}-${i}` : undefined}
                onClick={() => { if (it.disabled) return; closeMenu(true); it.onClick && it.onClick(); }}>
                <span className="material-icons" aria-hidden="true" style={{ fontSize: 18 }}>{it.icon}</span>
                <span>{it.label}</span>
              </button>
              {/* Why a disabled action is unavailable — as text, never the dimmed
                  state alone. The item keeps aria-disabled rather than the
                  disabled attribute, so it stays in the roving-focus order and
                  the reason is reachable instead of skipped. */}
              {it.note ? <p className="acct-menu-note" id={`${menuNoteId}-${i}`}>{it.note}</p> : null}
            </React.Fragment>
          ))}
        </div>
      )}
    </div>
  );
}

/* ---- Internal: one record row + its expandable detail / edit panel ---- */
function RTRow({ row, rk, columns, leading, expanded, editing, justSaved, onToggle, actionItems, renderDetail, editCtx, renderEdit, colSpan }) {
  const clickToggle = () => { if (!editing) onToggle(rk); };
  return (
    <React.Fragment>
      <tr className={`${expanded ? 'expanded' : ''} ${editing ? 'editing' : ''}`.trim() || undefined} onClick={clickToggle}>
        {leading && <td>{leading(row)}</td>}
        {columns.map((c) => {
          const tdCls = [c.align === 'right' ? 'numeric' : '', c.className || ''].filter(Boolean).join(' ');
          return (
            <td key={c.key} className={tdCls || undefined}>
              {c.cell ? c.cell(row, { expanded, editing, justSaved }) : row[c.key]}
            </td>
          );
        })}
        <td>
          <div className="ua-row-actions" onClick={(e) => e.stopPropagation()}>
            <RTMenu items={actionItems} />
            <button className="ua-expand-btn" aria-label={expanded ? 'Collapse row' : 'Expand row'} aria-expanded={expanded} onClick={clickToggle} disabled={editing}>
              <span className={`material-icons ua-chev ${expanded ? 'open' : ''}`} aria-hidden="true" style={{ fontSize: 22 }}>expand_more</span>
            </button>
          </div>
        </td>
      </tr>
      {expanded && (
        <tr className="ua-detail-row">
          <td className="ua-detail-cell" colSpan={colSpan}>
            {editing && renderEdit ? renderEdit(row, editCtx) : renderDetail(row, { expanded })}
          </td>
        </tr>
      )}
    </React.Fragment>
  );
}

export function RecordTable({
  rows = [],
  columns = [],
  rowKey = (r) => r.id,
  leading,
  defaultSort,
  sort: sortProp,
  onSortChange,
  multiOpen = false,
  keepDirOnColumnChange = false,
  tiebreak,
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
  // Controlled sort: when `sort` is bound the parent owns the state and the
  // internal seed/DefaultSort is superseded; unbound keeps legacy behaviour.
  const controlled = sortProp !== undefined;
  const sort = controlled ? (sortProp || defaultSort || { key: null, dir: 'asc' }) : internalSort;
  const applySort = (next) => {
    if (controlled) { onSortChange && onSortChange(next); }
    else setInternalSort(next);
  };
  const [openIds, setOpenIds] = useState([]);
  const [editIds, setEditIds] = useState([]);
  const [savedIds, setSavedIds] = useState([]);

  // Column-change default direction: the shared SortHelpers rule when the
  // column declares a sortType (text/status→asc, number/date→desc); legacy
  // asc (or keepDirOnColumnChange) otherwise. Read off the DS namespace —
  // bundle components can't import each other.
  const defaultDirFor = (col) => {
    if (!col || !col.sortType) return keepDirOnColumnChange ? sort.dir : 'asc';
    const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
    if (NS.SortHelpers) return NS.SortHelpers.defaultDir(col.defaultDir ? { type: col.sortType, defaultDir: col.defaultDir } : col.sortType);
    return { text: 'asc', status: 'asc', number: 'desc', date: 'desc' }[col.sortType] || 'asc';
  };

  // Header click: toggle direction on the active column; on a new column the
  // direction comes from defaultDirFor. Always raises a COMPLETE {key,dir}.
  // Sorting collapses every open row EXCEPT those mid-edit.
  const toggleSort = (key) => {
    setOpenIds((curr) => curr.filter((id) => editIds.includes(id)));
    const next = sort.key === key
      ? { key, dir: sort.dir === 'asc' ? 'desc' : 'asc' }
      : { key, dir: defaultDirFor(columns.find((c) => c.key === key)) };
    applySort(next);
  };

  const openRow = (curr, id) => (multiOpen ? [...curr, id] : [...curr.filter((x) => editIds.includes(x)), id]);
  const toggleRow = (id) => setOpenIds((curr) => (curr.includes(id) ? curr.filter((x) => x !== id) : openRow(curr, id)));
  const startEdit = (id) => {
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

  const colByKey = useMemo(() => {
    const m = {}; columns.forEach((c) => { m[c.key] = c; }); return m;
  }, [columns]);

  const sorted = useMemo(() => {
    if (!sort.key) return rows;
    const col = colByKey[sort.key];
    const getVal = col && col.sortValue ? col.sortValue : (r) => r[sort.key];
    const mul = sort.dir === 'asc' ? 1 : -1;
    return [...rows].sort((a, b) => {
      const va = getVal(a), vb = getVal(b);
      if (va < vb) return -1 * mul;
      if (va > vb) return 1 * mul;
      return tiebreak ? tiebreak(a, b) : 0;
    });
  }, [rows, sort, colByKey, tiebreak]);

  const colSpan = (leading ? 1 : 0) + columns.length + 1;

  return (
    <table className={`tbl ua-tbl ${className}`.trim()} aria-label={ariaLabel || undefined}>
      <thead>
        <tr>
          {leading && <th scope="col" style={{ width: 36 }}></th>}
          {columns.map((c) => {
            if (!c.sortable) {
              return <th key={c.key} scope="col" className={c.align === 'right' ? 'numeric' : undefined} style={c.width ? { width: c.width } : undefined}>{c.header}</th>;
            }
            const active = sort.key === c.key;
            return (
              <th key={c.key} scope="col" className={c.align === 'right' ? 'numeric' : ''}
                aria-sort={active ? (sort.dir === 'asc' ? 'ascending' : 'descending') : 'none'}>
                <button type="button" className={`ua-sort ${c.align === 'right' ? 'right' : ''} ${active ? 'active' : ''}`} onClick={() => toggleSort(c.key)}>
                  <span>{c.header}</span>
                  <span className={`material-icons ua-sort-ic ${active ? 'active' : ''} ${active && sort.dir === 'desc' ? 'desc' : ''}`} aria-hidden="true" style={{ fontSize: 16 }}>arrow_upward</span>
                </button>
              </th>
            );
          })}
          <th scope="col" style={{ width: 96, textAlign: 'right' }}>Actions</th>
        </tr>
      </thead>
      <tbody>
        {sorted.map((row) => {
          const rk = rowKey(row);
          const expanded = openIds.includes(rk);
          const editing = editIds.includes(rk);
          const ctx = {
            expanded,
            editing,
            toggle: () => toggleRow(rk),
            startEdit: () => startEdit(rk),
            remove: () => doDelete(rk),
          };
          return (
            <RTRow
              key={rk}
              row={row}
              rk={rk}
              columns={columns}
              leading={leading}
              expanded={expanded}
              editing={editing}
              justSaved={savedIds.includes(rk)}
              onToggle={toggleRow}
              actionItems={actions ? actions(row, ctx) : []}
              renderDetail={renderDetail}
              renderEdit={renderEdit}
              editCtx={{ save: (patch) => doSave(rk, patch), cancel: () => endEdit(rk) }}
              colSpan={colSpan}
            />
          );
        })}
        {sorted.length === 0 && (
          <tr><td colSpan={colSpan} style={{ padding: 0 }}>{empty}</td></tr>
        )}
      </tbody>
    </table>
  );
}
