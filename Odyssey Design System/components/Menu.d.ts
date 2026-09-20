import * as React from 'react';

export interface MenuItem {
  /** Visible label. Omitted for dividers/headers. */
  label?: string;
  /** Leading Material Icons ligature name. */
  icon?: string;
  onClick?: () => void;
  /** Destructive action — renders in the error color. */
  danger?: boolean;
  /** Unavailable — the item is NOT rendered at all (no dimmed row), and any
   *  divider or header it orphans is dropped with it. */
  disabled?: boolean;
  /** @deprecated Accepted for back-compat and ignored: unavailable items are
   *  hidden rather than explained. */
  note?: string;
  /** Renders a hairline separator instead of an item. */
  divider?: boolean;
  /** Renders an uppercase group label instead of an item. */
  header?: string;
}

export interface MenuProps {
  items: MenuItem[];
  /** Horizontal anchor of the popover relative to the trigger. */
  align?: 'start' | 'end';
  /** @deprecated Vertical side is now automatic — the popover (portaled to
   *  <body>) flips above the trigger when there isn't room below. Accepted
   *  for back-compat but ignored. */
  placement?: 'down' | 'up';
  /** Custom trigger element (e.g. a <Button>). Defaults to a more_vert icon button. */
  trigger?: React.ReactElement;
  /** Accessible name for the default icon-button trigger. */
  ariaLabel?: string;
}

/** Overflow / row-actions dropdown. Portaled popover (escapes overflow clipping, flips on collision); self-managing open state, outside-click + Esc to close. */
export declare function Menu(props: MenuProps): JSX.Element;
