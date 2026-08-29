import * as React from 'react';

/** The client-side sort value — mirrors `OdsTableSort(Key, Dir)`. */
export interface TableSort {
  key: string;
  dir: 'asc' | 'desc';
}

/** A curated sort field — one entry of a page's static sort-key allowlist. */
export interface SortField<TRow = any> {
  key: string;
  label: string;
  /** Drives the natural default direction and the typed direction labels. */
  type: 'text' | 'number' | 'date' | 'status';
  /** Override the type's natural default direction. */
  defaultDir?: 'asc' | 'desc';
  /** Value projection used by SortHelpers.sortRows on hand-rolled pages. */
  sortValue?: (row: TRow) => unknown;
}

export interface SortSelectProps {
  /** The page's curated field list (§6 of the sorting spec) — never every DTO property. */
  fields: SortField[];
  /** The active sort (controlled). Always a complete {key,dir}. */
  sort?: TableSort;
  /** Emits a complete {key,dir} on field change (natural default dir) or direction toggle. */
  onSort?: (next: TableSort) => void;
  /**
   * Anatomy. `split` (spec §4.2, default): field select + direction toggle
   * button with a typed label. `segmented`: field select + asc/desc segment
   * pair. `menu`: one combined trigger (field list + direction section).
   */
  variant?: 'split' | 'segmented' | 'menu';
  /** Visible control label. Default "Sort by". */
  label?: string;
  disabled?: boolean;
  className?: string;
  id?: string;
}

/**
 * The filter-bar "Sort by" control — field selector + direction toggle bound
 * to one {key,dir}. The anatomy is identical regardless of field count — a
 * one-field page still shows the field-select trigger + direction control, so
 * the control reads the same on every list page. Direction labels are typed:
 * A → Z / Low → High / Oldest first /
 * Defined order, per the field's `type`.
 */
export declare function SortSelect(props: SortSelectProps): JSX.Element;

/** Shared sorting authority (spec §8.4/§8.5) — default directions, typed labels, column derivation, stable ordering. */
export declare const SortHelpers: {
  /** Natural default direction for a field or bare type ('text'|'number'|'date'|'status'). */
  defaultDir(fieldOrType: SortField | SortField['type'] | undefined): 'asc' | 'desc';
  /** Typed direction label, e.g. dirLabel('date','desc') → "Newest first". */
  dirLabel(type: SortField['type'], dir: 'asc' | 'desc'): string;
  /** Derive the curated field list from RecordTable columns (single source of truth); `keys` curates/orders the subset. */
  fieldsFromColumns(columns: Array<{ key: string; header?: React.ReactNode; sortable?: boolean; sortType?: SortField['type']; defaultDir?: 'asc' | 'desc' }>, keys?: string[]): SortField[];
  /** Stable ordering for hand-rolled pages: nulls last both directions, record-id tiebreak. Apply after search + filters. */
  sortRows<TRow>(rows: TRow[], fields: SortField<TRow>[], sort: TableSort | undefined, getId?: (row: TRow) => string): TRow[];
};
