import * as React from 'react';

export interface MenuItem {
  /** Visible label. Omitted for dividers/headers. */
  label?: string;
  /** Leading Material Icons ligature name. */
  icon?: string;
  onClick?: () => void;
  /** Destructive action — renders in the error color. */
  danger?: boolean;
  disabled?: boolean;
  /**
   * One line saying why a `disabled` item is unavailable — rendered under the
   * label and wired as the item's `aria-describedby`. An item with a note is
   * marked `aria-disabled` instead of `disabled`, so it keeps its place in the
   * roving-focus order and the reason is reachable rather than skipped.
   * Meaning is carried as text, never by the dimmed state alone.
   */
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
