import * as React from 'react';

export interface SkeletonProps {
  /** text = a line (use `lines` for a paragraph) · circle = avatars · block = cards/tiles/charts. */
  variant?: 'text' | 'circle' | 'block';
  /** CSS width (e.g. '100%', '120px'). */
  width?: string;
  /** CSS height — required for circle/block; text derives its own height. */
  height?: string;
  /** For variant="text": render N stacked lines (last is shortened). */
  lines?: number;
  className?: string;
  style?: React.CSSProperties;
}

/** Shimmering loading placeholder. Respects prefers-reduced-motion. */
export declare function Skeleton(props: SkeletonProps): JSX.Element;

export interface SkeletonRowProps {
  /** Number of cells in the row. Match your Table's column count. */
  columns?: number;
  /** Per-column-index alignment; pass 'end' to size that cell as a numeric column. */
  align?: Array<'start' | 'end' | undefined>;
}

/** A placeholder <tr> for a loading Table — render a few in <tbody> while rows fetch. */
export declare function SkeletonRow(props: SkeletonRowProps): JSX.Element;
