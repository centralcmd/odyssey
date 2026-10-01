import * as React from 'react';

export interface TransactionTagIcon {
  /** Material Icons ligature name — the value the API stores (`^[a-z0-9_]{1,64}$`). */
  key: string;
  /** Human label — the cell's accessible name and the caption text. */
  label: string;
}

/** The selectable catalogue, in display order. Mirrors `TransactionTagIcons.All`
 *  in Odyssey.Dtos/Finance. The default (`local_offer`) is NOT a member. */
export declare const TRANSACTION_TAG_ICONS: TransactionTagIcon[];

/** The default glyph (`local_offer`) — never stored; a tag with `icon: null` uses it. */
export declare const TRANSACTION_TAG_ICON_DEFAULT: string;

interface TagLike { id?: string; transactionTagId?: string; name?: string; label?: string; icon?: string | null; }

export declare const TransactionTagIcons: {
  Default: string;
  All: TransactionTagIcon[];
  /** Ordinal, case-sensitive catalogue membership. `isKnown('local_offer')` is false. */
  isKnown(key: unknown): boolean;
  /** Read-side projection: known key → key; null / unknown → null. */
  normalize(key: string | null | undefined): string | null;
  /** Glyph to draw for a stored icon: known key → key; else the default. */
  glyph(key: string | null | undefined): string;
  /** Label for a key; null / unknown → "Default". */
  labelFor(key: string | null | undefined): string;
  /** Sorted copy by name (ordinal, case-insensitive), ties by id — the API's tag order. */
  order<T extends TagLike>(tags: T[] | null | undefined): T[];
  /** The display-icon rule: first ordered tag with a known icon, else the default.
   *  Client preview only — render the server's `displayIcon` wherever it exists. */
  resolve(tags: TagLike[] | null | undefined): string;
};

export interface TagIconPickerProps {
  /** Selected key, or null for Default. Unknown values render as Default. */
  value?: string | null;
  /** Fires with the chosen key, or null when Default is picked. */
  onChange?: (key: string | null) => void;
  /** Override the catalogue (defaults to TRANSACTION_TAG_ICONS). */
  icons?: TransactionTagIcon[];
  disabled?: boolean;
  id?: string;
  /** Accessible name for the radiogroup. Default "Icon". */
  ariaLabel?: string;
  /** Id of a helper/error line describing the group. */
  ariaDescribedby?: string;
  className?: string;
}

/** Closed grid of the transaction-tag icon catalogue with a leading Default
 *  cell. ARIA radiogroup: 2-D arrow keys, Home/End, roving tab stop; the
 *  caption names the selection and previews the hovered/focused cell in words. */
export declare function TagIconPicker(props: TagIconPickerProps): JSX.Element;
