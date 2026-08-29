import * as React from 'react';

/** A rows-per-page preset — one of the offered sizes, or `'all'` (fetch every
 *  matching row; the client virtualizes them). */
export type PageSize = number | 'all';

export interface PagerProps {
  /** Current page, 1-based. */
  page?: number;
  /** Rows per page — a preset number or `'all'`. Default 25. */
  pageSize?: PageSize;
  /** The offered presets. Default `[25, 100, 1000, 'all']`. */
  pageSizeOptions?: PageSize[];
  /** Total matching, authorized records (from `PagedResult.TotalCount`). Drives the summary and the last-page bound. */
  totalCount?: number;
  /** Fires with the next 1-based page. Never fires past a bound (activation there is a no-op). */
  onPageChange?: (nextPage: number) => void;
  /** Fires with the next page size. The owner resets `page` to 1 in response (same as a filter change). */
  onPageSizeChange?: (nextSize: PageSize) => void;
  /** Render the rows-per-page selector. Default true — the footer is its canonical, always-present home. */
  showPageSize?: boolean;
  /** Label before the rows-per-page selector. Default "Rows per page". */
  pageSizeLabel?: string;
  /**
   * A fetch is in flight. Nav buttons become `aria-disabled` no-ops (never
   * native `disabled`, so focus is kept), the size selector is disabled, and the
   * summary shows a busy indicator.
   */
  loading?: boolean;
  /** Accessible name for the `<nav>` landmark. Defaults to "Pagination". */
  label?: string;
  /**
   * Make the summary its own `aria-live="polite"` region. Leave FALSE (default)
   * when the page hosts a `LiveAnnouncer` — the page pushes the summary string
   * there so it isn't announced twice (single live-region owner).
   */
  announce?: boolean;
  className?: string;
  id?: string;
}

/**
 * The shared list pager below every server-paged FLAT-TABLE list page. The
 * canonical, always-present home of the rows-per-page control (25 / 100 / 1000
 * / All, default 25) plus the one summary "Showing X–Y of N" ("0 results" when
 * empty), then first / previous / next / last nav.
 *
 * Changing the size is the owner page's job to reset to page 1; when a search
 * bar is present, mirror the size in the toolbar with `PageSizeSelect` bound to
 * the same state. "All" reports a single page over every matching row.
 *
 * Bound behaviour is pinned for accessibility: at the first/last page the
 * relevant nav button stays focusable and enabled but `aria-disabled="true"`
 * with a no-op activation (never the native `disabled` attribute), and focus
 * moves to the opposite button when a press reaches a bound so focus is never
 * lost. `TotalPages` is derived from `totalCount / pageSize`, never passed.
 */
export declare function Pager(props: PagerProps): JSX.Element;
