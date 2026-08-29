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
}

export interface ActionMenuProps {
  /** Ordered list of menu items / dividers. */
  items: ActionMenuItem[];
}

/** Row overflow menu (`more_vert` kebab) with a fixed, auto-dismissing popover. */
export declare function ActionMenu(props: ActionMenuProps): JSX.Element;
