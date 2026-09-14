import * as React from 'react';

export interface CardSelectOption {
  value: string;
  label: React.ReactNode;
  /** Material Icons ligature shown above the label. */
  icon?: string;
  /** Optional caption under the label. */
  sub?: React.ReactNode;
  /** Per-option accent (border + icon when selected), overrides the group accent. */
  color?: string;
  /** Per-option border colour when selected/hovered — defaults to `color`. */
  line?: string;
  /** Per-option soft tint used as the selected background. */
  soft?: string;
  disabled?: boolean;
}

export interface CardSelectProps {
  /** Options as {value,label,icon?,sub?,color?,soft?} objects or plain strings. */
  options: Array<CardSelectOption | string>;
  /** Selected value (controlled). */
  value?: string;
  onChange?: (value: string) => void;
  ariaLabel?: string;
  /** Group accent — icon colour, and the border when `accentLine` is absent. */
  accent?: string;
  /** Group border colour when selected/hovered — defaults to `accent`. */
  accentLine?: string;
  /** Group soft tint — selected card background. */
  accentSoft?: string;
  /** 'fit' (default) fills the row; a number fixes the column count. */
  columns?: 'fit' | number;
  /** Cap each card's width, in px — pair with `center` for a balanced two-up. */
  maxItemWidth?: number;
  /** Centre the grid instead of stretching it. */
  center?: boolean;
}

/** Single-select picker drawn as icon-over-label cards. */
export declare function CardSelect(props: CardSelectProps): JSX.Element;
