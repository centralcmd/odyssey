export interface ReferenceNumberProps {
  /** The stored value. `null` / empty renders nothing — absence is a normal state, not a defect. */
  value?: string | null;
  /** The active list search; the first case-insensitive match is marked. */
  highlight?: string;
  /** Adds a copy-to-clipboard button. */
  copyable?: boolean;
  /** `sm` for a list-row meta line (one line, ellipsized, full value in `title`); `md` for a detail tile (wraps). Default `md`. */
  size?: 'sm' | 'md';
  /** Leading `tag` glyph. Default: on for `sm` (bare meta line), off for `md` (the host tile has its own icon chip). */
  showIcon?: boolean;
  className?: string;
}

/** Read-only mono display of a counterparty identifier (a contract's `referenceNumber`). */
export declare function ReferenceNumber(props: ReferenceNumberProps): JSX.Element | null;
