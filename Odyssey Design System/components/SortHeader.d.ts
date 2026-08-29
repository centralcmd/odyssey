import * as React from 'react';

export interface SortHeaderProps {
  /** Visible column label. */
  label: React.ReactNode;
  /** This column's sort key — compared against `sort.key` to show the active state. */
  sortKey: string;
  /** Shared, controlled sort state. */
  sort: { key: string; dir: 'asc' | 'desc' };
  /** Called with this column's `sortKey` when the header is clicked. */
  onSort: (key: string) => void;
  /** Right-align for numeric columns (amounts, dates). */
  align?: 'left' | 'right';
  /** Extra inline style on the `<th>` (e.g. a fixed width). */
  style?: React.CSSProperties;
}

/** Sortable table header cell with an asc/desc arrow indicator. */
export declare function SortHeader(props: SortHeaderProps): JSX.Element;
