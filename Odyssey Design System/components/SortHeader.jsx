/**
 * Odyssey DS — SortHeader
 * A sortable `<th>` for the record tables (Transactions, Users, Contacts,
 * Currencies, …). Renders a MudTableSortLabel-style header button with an
 * arrow that points up (asc) / down (desc) and brightens on the active column.
 *
 * Controlled: pass the shared `sort` ({ key, dir }) and an `onSort(key)`
 * handler — the component renders the indicator, the parent owns the ordering.
 * `align="right"` right-aligns the header for numeric columns (amounts, dates).
 *
 * RecordTable renders these for you from its `columns`; reach for SortHeader
 * directly only when you hand-roll a `<thead>`.
 */
export function SortHeader({ label, sortKey, sort, onSort, align, style }) {
  const active = sort.key === sortKey;
  return (
    <th
      scope="col"
      className={align === 'right' ? 'numeric' : ''}
      style={style}
      aria-sort={active ? (sort.dir === 'asc' ? 'ascending' : 'descending') : 'none'}
    >
      <button
        type="button"
        className={`ua-sort ${align === 'right' ? 'right' : ''} ${active ? 'active' : ''}`}
        onClick={() => onSort(sortKey)}
      >
        <span>{label}</span>
        <span
          className={`material-icons ua-sort-ic ${active ? 'active' : ''} ${active && sort.dir === 'desc' ? 'desc' : ''}`}
          aria-hidden="true"
          style={{ fontSize: 16 }}
        >
          arrow_upward
        </span>
      </button>
    </th>
  );
}
