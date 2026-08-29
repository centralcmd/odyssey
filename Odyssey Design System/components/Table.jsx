/**
 * Odyssey DS — Table
 * The portable, data-driven table primitive behind every ledger screen
 * (Transactions, Files, Users). Maps to a MudTable + MudTableSortLabel.
 *
 * Declarative: pass `columns` (each with an optional `cell` renderer) and
 * `rows`. Sortable headers are controlled — give `sort` ({key,dir}) and an
 * `onSort(key)` handler; the component renders the indicator, you own the
 * sort. Right-align + monospace numerics with `align:'end'` on a column.
 * Dense rows for nav/embedded tables; `onRowClick` for expandable records.
 */
export function Table({
  columns = [],
  rows = [],
  sort,
  onSort,
  rowKey,
  dense = false,
  onRowClick,
  empty,
  ariaLabel,
  className = '',
}) {
  const cls = [
    'odc-table',
    dense ? 'dense' : '',
    onRowClick ? 'clickable' : '',
    className,
  ].filter(Boolean).join(' ');
  const keyOf = rowKey || ((r, i) => (r && r.id != null ? r.id : i));

  return (
    <table className={cls} aria-label={ariaLabel || undefined}>
      <thead>
        <tr>
          {columns.map((c) => {
            const active = sort && sort.key === c.key;
            const ariaSort = c.sortable
              ? (active ? (sort.dir === 'asc' ? 'ascending' : 'descending') : 'none')
              : undefined;
            return (
              <th
                key={c.key}
                scope="col"
                className={`odc-th${c.align === 'end' ? ' num' : ''}`}
                style={c.width ? { width: c.width } : undefined}
                aria-sort={ariaSort}
              >
                {c.sortable && onSort ? (
                  <button
                    type="button"
                    className="odc-th-btn"
                    data-active={active || undefined}
                    onClick={() => onSort(c.key)}
                  >
                    <span>{c.header}</span>
                    <span className="material-icons odc-th-sort" aria-hidden="true">
                      {active && sort.dir === 'asc' ? 'arrow_upward' : 'arrow_downward'}
                    </span>
                  </button>
                ) : (
                  c.header
                )}
              </th>
            );
          })}
        </tr>
      </thead>
      <tbody>
        {rows.length === 0 && empty ? (
          <tr>
            <td className="odc-table-empty" colSpan={columns.length}>{empty}</td>
          </tr>
        ) : (
          rows.map((row, i) => (
            <tr
              key={keyOf(row, i)}
              onClick={onRowClick ? () => onRowClick(row, i) : undefined}
            >
              {columns.map((c) => {
                const tdCls = [c.align === 'end' ? 'num' : '', c.className || '']
                  .filter(Boolean).join(' ');
                return (
                  <td key={c.key} className={tdCls || undefined}>
                    {c.cell ? c.cell(row, i) : row[c.key]}
                  </td>
                );
              })}
            </tr>
          ))
        )}
      </tbody>
    </table>
  );
}
