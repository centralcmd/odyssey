import * as React from 'react';

export interface TableColumn<Row = any> {
  /** Stable column id; also the default cell value (row[key]) when no `cell`. */
  key: string;
  /** Header label (string or node). */
  header: React.ReactNode;
  /** 'end' right-aligns the column and renders cells as monospace tabular figures (amounts, dates). */
  align?: 'start' | 'end';
  /** When true, the header renders a sort button — pair with `sort` + `onSort`. */
  sortable?: boolean;
  /** Custom cell renderer. Falls back to row[key] when omitted. */
  cell?: (row: Row, index: number) => React.ReactNode;
  /** Fixed column width, e.g. '1%' for a row-actions cell, '160px', '20%'. */
  width?: string;
  /** Extra class on every <td> in this column. */
  className?: string;
}

export interface TableProps<Row = any> {
  columns: Array<TableColumn<Row>>;
  rows: Row[];
  /** Controlled sort state. The component renders the indicator; you do the sorting. */
  sort?: { key: string; dir: 'asc' | 'desc' };
  /** Called with a column key when a sortable header is clicked. */
  onSort?: (key: string) => void;
  /** Stable row key. Defaults to row.id, then index. */
  rowKey?: (row: Row, index: number) => string | number;
  /** Compact row height — nav-embedded tables, dense lists. */
  dense?: boolean;
  /** Makes rows clickable (cursor + handler) — used for expand-in-place records. */
  onRowClick?: (row: Row, index: number) => void;
  /** Rendered as a single full-width row when `rows` is empty (e.g. an EmptyState). */
  empty?: React.ReactNode;
  className?: string;
}

/** Data-driven table primitive — sortable headers, numeric columns, dense + clickable rows. */
export declare function Table<Row = any>(props: TableProps<Row>): JSX.Element;
