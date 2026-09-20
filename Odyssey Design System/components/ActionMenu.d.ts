import * as React from 'react';

export interface ActionMenuItem {
  /** Item glyph — a Material Icons ligature, or any non-ligature character
   *  (e.g. "§") rendered as a typographic glyph. */
  icon?: string;
  /** Item label. */
  label?: React.ReactNode;
  /** A trailing Material icon, pushed to the right edge and revealed on hover
   *  (e.g. a `content_copy` affordance on a "Copy ID" item). */
  trailingIcon?: string;
  /** Click handler — fired after the menu closes. */
  onClick?: () => void;
  /** Tint the item red for destructive actions (Delete). */
  danger?: boolean;
  /** Render a divider rule instead of an action. */
  divider?: boolean;
  /** Unavailable — the item is NOT rendered at all, and a divider it orphans
   *  is dropped with it. An action a record cannot take is absent from its
   *  menu rather than dimmed with an explanation. */
  disabled?: boolean;
  /** @deprecated Accepted for back-compat and ignored: unavailable items are
   *  hidden rather than explained. */
  note?: string;
}

export interface ActionMenuProps {
  /** Ordered list of menu items / dividers. */
  items: ActionMenuItem[];
  /**
   * Accessible name for the trigger. Default "More actions" — pass a
   * record-naming string ("Actions for alias: Hansen") where several menus sit
   * in one grid.
   */
  ariaLabel?: string;
}

/** Row overflow menu (`more_vert` kebab) with a fixed, auto-dismissing popover. */
export declare function ActionMenu(props: ActionMenuProps): JSX.Element;
