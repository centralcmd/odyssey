import * as React from 'react';

export interface EventRailProps {
  /** This page holds the newest end of the log — draw the line to a hard stop at the top. */
  capTop?: boolean;
  /** This page holds the oldest end — hard stop at the bottom. */
  capEnd?: boolean;
  className?: string;
  /** `EventRailItem` and `EventRailMarker` children, in display order. */
  children?: React.ReactNode;
}

export interface EventRailMarkerProps {
  /** `tick` (a year), `open` (the present / an open end), `filled` (a closed end). */
  tone?: 'tick' | 'open' | 'filled';
  /** Provenance for the endpoint itself — revealed on hover, like an item's `.odc-er-meta`. */
  meta?: React.ReactNode;
  className?: string;
  /** The marker's label — a year, "Today · 20 Sept 2026", "Contract added …". */
  children?: React.ReactNode;
}

export interface EventRailItemProps {
  /** Material icon name for the node — the entry's kind. */
  icon?: string;
  /** Accessible name and tooltip for the node. Required when the glyph is the only thing naming the kind. */
  iconLabel?: string;
  /** Primary line. */
  title?: React.ReactNode;
  /** When it happened, in the mono face. */
  date?: React.ReactNode;
  /** Secondary line under the title, clamped to two lines. */
  desc?: React.ReactNode;
  /** Typically a `RowActions`; sits inline after the date and reveals on hover. */
  actions?: React.ReactNode;
  /** Node glyph colour. Omit for the neutral default. */
  color?: string;
  /** Highlights this entry — e.g. selected from an overview strip. */
  selected?: boolean;
  className?: string;
  /** Extra body content under `desc`. Anything with `.odc-er-meta` reveals on hover. */
  children?: React.ReactNode;
}

/**
 * Continuous-rail history list — one unbroken line with icon nodes and
 * markers sitting on it. Use for a LOG of events; use `Timeline` for an
 * effective-dated table of values with a figures column.
 * DS-tab card: components/event-rail.html.
 */
export declare function EventRail(props: EventRailProps): JSX.Element;
export declare function EventRailMarker(props: EventRailMarkerProps): JSX.Element;
export declare function EventRailItem(props: EventRailItemProps): JSX.Element;
