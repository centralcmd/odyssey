import * as React from 'react';
import { ActionMenuItem } from './ActionMenu';

/** Per-row context passed to a column's `cell` renderer. */
export interface RecordCellContext {
  /** Row is currently expanded. */
  expanded: boolean;
  /** Row is in edit mode (its panel shows `renderEdit`). */
  editing: boolean;
  /** Row just saved — show the transient "Saved" flash chip. */
  justSaved: boolean;
}

/** Context passed to `actions(row, ctx)` for building the row's overflow menu. */
export interface RecordActionContext {
  expanded: boolean;
  editing: boolean;
  /** Expand / collapse this row. */
  toggle: () => void;
  /** Open this row and switch it into edit mode. */
  startEdit: () => void;
  /** Delete this row (clears its open/edit state, then calls `onDelete`). */
  remove: () => void;
}

/** Context passed to `renderEdit(row, ctx)`. */
export interface RecordEditContext {
  /** Commit a patch — calls `onSave(key, patch)`, exits edit, flashes "Saved". */
  save: (patch: any) => void;
  /** Leave edit mode without saving (row stays expanded on its detail view). */
  cancel: () => void;
}

export interface RecordColumn<Row = any> {
  /** Stable column id; also the default cell value (row[key]) and sort key. */
  key: string;
  /** Header label. */
  header: React.ReactNode;
  /** Render a sortable header — the table sorts via `sortValue` (or row[key]). */
  sortable?: boolean;
  /** Right-align + monospace numerics (amounts, dates, counts). */
  align?: 'left' | 'right';
  /** Fixed column width for a non-sortable header. */
  width?: string;
  /** Extra class on every `<td>` in this column. */
  className?: string;
  /** Cell renderer. Falls back to row[key]. */
  cell?: (row: Row, ctx: RecordCellContext) => React.ReactNode;
  /** Comparable value for sorting this column. Falls back to row[key]. */
  sortValue?: (row: Row) => any;
}

export interface RecordTableProps<Row = any> {
  /** Rows to render (already filtered by the parent). */
  rows: Row[];
  /** Column definitions, left → right (excluding the leading + actions cells). */
  columns: Array<RecordColumn<Row>>;
  /** Stable row identity. Defaults to row.id. */
  rowKey?: (row: Row) => string | number;
  /** Optional leading 36px cell (typically an <Avatar>). */
  leading?: (row: Row) => React.ReactNode;
  /** Initial sort. Omit for an unsorted table. */
  defaultSort?: { key: string; dir: 'asc' | 'desc' };
  /** Keep multiple rows expanded at once (default: accordion — one at a time). */
  multiOpen?: boolean;
  /** On clicking a new column, keep the current direction instead of resetting to asc. */
  keepDirOnColumnChange?: boolean;
  /** Stable secondary comparator applied when the primary sort ties. */
  tiebreak?: (a: Row, b: Row) => number;
  /** Build the row's overflow-menu items. */
  actions?: (row: Row, ctx: RecordActionContext) => ActionMenuItem[];
  /** Read-only panel shown when a row is expanded. */
  renderDetail?: (row: Row, ctx: { expanded: boolean }) => React.ReactNode;
  /** Edit panel shown when a row is in edit mode. Omit for read-only tables. */
  renderEdit?: (row: Row, ctx: RecordEditContext) => React.ReactNode;
  /** Persist a row edit. */
  onSave?: (key: string | number, patch: any) => void;
  /** Remove a row. */
  onDelete?: (key: string | number) => void;
  /** How long the "Saved" flash stays up (ms). Default 2200. */
  savedFlashMs?: number;
  /** Full-width content shown when `rows` is empty (e.g. an <EmptyState>). */
  empty?: React.ReactNode;
  /** Extra class on the `<table>`. */
  className?: string;
}

/** Sortable, expandable, editable record table — the admin/ledger table primitive. */
export declare function RecordTable<Row = any>(props: RecordTableProps<Row>): JSX.Element;
