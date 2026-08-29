import * as React from 'react';

export interface SeverityIconProps {
  /** Which signal glyph to render. Default 'warning'. */
  severity?: 'warning' | 'error' | 'info';
  /** Icon size in px. Default 18. */
  size?: number;
  /** Extra class on the glyph element. */
  className?: string;
  /** Extra inline styles. */
  style?: React.CSSProperties;
}

/**
 * The signal glyph used by Alerts and the PageHeader problem-rollup toggle.
 * Renders in `currentColor` — tint it from the parent (amber for warning,
 * coral for error, sea for info). The embedded Material Icons subset has no
 * warning triangle, so warning is drawn as an inline SVG; error / info use
 * the font's outline glyphs so all three read identically to the Alert block.
 */
export declare function SeverityIcon(props: SeverityIconProps): JSX.Element;
