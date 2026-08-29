import * as React from 'react';

export interface IconButtonProps {
  /** Material Icons ligature name. */
  icon: string;
  /** Accessible name — REQUIRED, since the button shows no text. */
  ariaLabel: string;
  /** sm = 28px · md = 36px (default) · lg = 44px (touch target). */
  size?: 'sm' | 'md' | 'lg';
  /** Tint for destructive actions. */
  danger?: boolean;
  /** Render as an <a> with this href instead of a <button>. */
  href?: string;
  type?: 'button' | 'submit' | 'reset';
  disabled?: boolean;
  onClick?: (e: React.MouseEvent) => void;
}

/** Bare icon-only button — modal close, row actions, menu triggers. */
export declare function IconButton(props: IconButtonProps): JSX.Element;
