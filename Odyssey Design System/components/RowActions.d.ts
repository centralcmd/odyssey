import * as React from 'react';

export interface RowAction {
  /** Material Icons glyph name. */
  icon: string;
  /** Accessible name — required, the button has no visible text. */
  label: string;
  onClick?: (e: React.MouseEvent) => void;
  /** Tint for destructive actions (delete / detach). */
  danger?: boolean;
  disabled?: boolean;
  /** Override the cluster's size for this one button. */
  size?: 'sm' | 'md' | 'lg';
  key?: string;
}

export interface RowActionsProps {
  /** The buttons, left to right. Destructive action last. */
  actions?: RowAction[];
  /** Size of every button in the cluster. Default 'sm' (28px). */
  size?: 'sm' | 'md' | 'lg';
  /**
   * Fade in on row hover / focus-within (default). Always visible on touch
   * devices regardless. Pass false to pin the cluster visible.
   */
  reveal?: boolean;
  className?: string;
  /** Extra controls appended after `actions` (e.g. a Menu trigger). */
  children?: React.ReactNode;
}

/**
 * End-of-row icon action cluster for tables and list items. When the host is
 * not a `<tr>`, put `odc-rowactions-host` on the row element so the reveal
 * has something to hang off. DS-tab card: components/rowactions.html.
 */
export declare function RowActions(props: RowActionsProps): JSX.Element;
