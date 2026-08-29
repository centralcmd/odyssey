import * as React from 'react';

export interface PageHeaderChipSpec {
  label: React.ReactNode;
  /** Chip tone (e.g. 'income' | 'expense' | 'info' | 'warning' | 'error' | 'outline'). */
  tone?: string;
  /** Leading Material Icons ligature on the chip. */
  icon?: string;
  /** Leading status dot. */
  dot?: boolean;
}

export interface PageHeaderActionSpec {
  label: React.ReactNode;
  /** Leading / trailing Material Icons ligature. */
  icon?: string;
  iconRight?: string;
  /** Button variant. Default 'outlined'. */
  variant?: 'filled' | 'outlined' | 'text' | 'danger';
  onClick?: (e: React.MouseEvent) => void;
}

export interface PageHeaderMenuItemSpec {
  icon?: string;
  label?: React.ReactNode;
  onClick?: (e: React.MouseEvent) => void;
  /** Tint the row coral for destructive verbs. */
  danger?: boolean;
  /** Render a divider row instead of an item. */
  divider?: boolean;
}

export interface PageHeaderSignalSpec {
  /** Toggle tint + glyph: warning (amber) / error (coral) / info (sea). Default 'warning'. */
  severity?: 'warning' | 'error' | 'info';
  /** Problem count shown in the toggle's badge. */
  count?: number;
  /** Toggle label. Default 'Attention'. */
  label?: React.ReactNode;
  /** The rollup panel that opens above the other regions. */
  region?: React.ReactNode;
  defaultOpen?: boolean;
}

export interface PageHeaderProps {
  title: React.ReactNode;
  /** Sub-line under the title — the page's running tally. */
  sub?: React.ReactNode;
  /** Third line below the sub: a wrapping row of Chips. Array entries become
   *  <Chip>s; valid elements render as-is. */
  chips?: PageHeaderChipSpec[] | React.ReactNode;
  /** Leading icon — a Material Icons name renders in a tinted tile; a node
   *  renders as-is (custom avatar / badge). */
  icon?: string | React.ReactNode;
  /** Adds the Overview toggle + drop-in region. */
  overview?: React.ReactNode;
  /** Adds the Search toggle + drop-in region. The search Field should fill the
   *  region width (flex:1, min-width ~280) — never cap it with a max-width. */
  search?: React.ReactNode;
  /** Adds a passive reference/lookup toggle + region. */
  info?: React.ReactNode;
  /** Reference toggle label / icon. Defaults 'Reference' / 'menu_book'. */
  infoLabel?: string;
  infoIcon?: string;
  overviewDefaultOpen?: boolean;
  searchDefaultOpen?: boolean;
  infoDefaultOpen?: boolean;
  /** Severity-tinted problem-rollup toggle at the FRONT of the cluster. */
  signal?: PageHeaderSignalSpec;
  /** Overflow "More" menu — rendered rightmost / last. An array becomes a
   *  more_vert ActionMenu; a node renders as-is. */
  menu?: PageHeaderMenuItemSpec[] | React.ReactNode;
  /** Extra secondary actions, before the primary. Array entries become
   *  Buttons; a node renders as-is. */
  actions?: PageHeaderActionSpec[] | React.ReactNode;
  /** The filled create verb — `{label, icon, onClick}` or a node. */
  primary?: { label: React.ReactNode; icon?: string; onClick?: (e: React.MouseEvent) => void } | React.ReactNode;
  /** Wrap a region-less header in the canonical surface card. (Headers with
   *  regions always card.) */
  card?: boolean;
}

/**
 * The shared page-header scaffold every Odyssey screen mounts first: title +
 * sub + chips on the left, the action cluster on the right (Signal → Overview
 * → Search → Reference → secondary actions → primary verb → overflow menu).
 * A toggle's filled/outlined state IS its open indicator. Open regions drop
 * in below the title row, each separated by a divider.
 */
export declare function PageHeader(props: PageHeaderProps): JSX.Element;
