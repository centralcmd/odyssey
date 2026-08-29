import * as React from 'react';

export interface InfoTileProps {
  /** Material Icons ligature for the leading chip. Omit for no chip. */
  icon?: string;
  /** Tint the icon chip foreground (e.g. a category color). Defaults to brand accent. */
  iconColor?: string;
  /** Icon chip background; pair with `iconColor`. Defaults to a soft brand tint. */
  iconSoft?: string;
  /** Uppercase overline label. */
  label?: React.ReactNode;
  /** The headline value — text, number, date, or any node. */
  value?: React.ReactNode;
  /** Optional muted caption under the value. */
  foot?: React.ReactNode;
  /** Value type face: mono (numbers/IDs/dates) · text (names) · sm (smaller mono). */
  valueVariant?: 'mono' | 'text' | 'sm';
  /** Span all grid columns and allow the value to wrap (e.g. a notes tile). */
  wide?: boolean;
  /** Card elevation (subtle shadow). Default true. */
  elevated?: boolean;
  className?: string;
  style?: React.CSSProperties;
}

/**
 * A labeled fact/stat tile — icon chip + label, a headline value, and an optional
 * foot caption, on an elevated card. The richer sibling of MetaTile / StatTile.
 * Re-tint a whole grid via the `--odc-infotile-accent` / `--odc-infotile-accent-soft`
 * CSS variables on the grid container.
 */
export declare function InfoTile(props: InfoTileProps): JSX.Element;
