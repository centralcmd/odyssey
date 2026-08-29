import * as React from 'react';

/** A rows-per-page preset — one of the offered sizes, or `'all'`. */
export type PageSize = number | 'all';

export interface PageSizeSelectProps {
  /** Current size — a preset number or `'all'`. Default 25. */
  value?: PageSize;
  /** The offered presets. Default `[25, 100, 1000, 'all']`. */
  options?: PageSize[];
  /** Fires with the next size. Bind to the same state the footer `Pager` reads so the two stay in sync. */
  onChange?: (next: PageSize) => void;
  /** Verb before the value ("Show 25"). Pass "" for a bare value. Default "Show". */
  prefix?: string;
  /** Text after the value (e.g. "at a time" for a batch-size control). Default "". */
  suffix?: string;
  /** Accessible-name label for the trigger. Default "Rows per page". */
  label?: string;
  disabled?: boolean;
  className?: string;
  id?: string;
}

/**
 * The toolbar MIRROR of the footer `Pager`'s rows-per-page control — mount it in
 * a list page's search/toolbar region and bind it to the SAME `pageSize` state
 * the footer reads. Additive: the footer selector is the canonical home; this
 * appears only where a search bar exists. Reads "Show 25 ▾"; presets 25 / 100 /
 * 1000 / All. Opens downward (it sits at the top of the list); 40px tall to line
 * up with SearchField / MultiSelect on the toolbar row.
 */
export declare function PageSizeSelect(props: PageSizeSelectProps): JSX.Element;
