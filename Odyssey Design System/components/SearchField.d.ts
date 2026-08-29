export interface SearchFieldProps {
  /** Current query string. */
  value?: string;
  /** Fires with the next string value first, the native event second. */
  onChange?: (value: string, event: React.ChangeEvent<HTMLInputElement>) => void;
  /** Placeholder text. Defaults to "Search…". */
  placeholder?: string;
  /** Leading Material Icons ligature. Defaults to "search"; override only for a domain-specific search. */
  icon?: string;
  /** Show the clear (×) button when there's a value. Defaults to true. Calls onChange(''). */
  clearable?: boolean;
  disabled?: boolean;
  className?: string;
  /** Explicit id; auto-generated if omitted. */
  id?: string;
}

/**
 * Canonical search / filter input — a `Field` pre-set with a leading search
 * glyph, a clear button, and search semantics. Use for every filter-bar and
 * page search box.
 */
export declare function SearchField(props: SearchFieldProps): JSX.Element;
