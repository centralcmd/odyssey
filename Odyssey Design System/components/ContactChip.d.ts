import * as React from 'react';

export interface ContactTypeMeta {
  key: string;
  label: string;
  icon: string;
  color: string;
  soft?: string;
}

/** Slim contact projection this chip reads. */
export interface ContactRef {
  id?: string;
  name?: string;
  /** ContactType key: Merchant | Person | Organization | Company | Institution | Other. */
  type?: string;
  /** Muted "(archived)" cue when set. */
  archived?: boolean | string | null;
  /** Render the "Unavailable" state for a since-deleted / no-access id. */
  unavailable?: boolean;
}

/** Resolve a ContactType key to its registry meta (icon · color · label). */
export declare function contactTypeMeta(typeKey?: string): ContactTypeMeta;

export interface ContactChipProps {
  /** The contact to display (preferred). */
  contact?: ContactRef;
  /** Bare name, when you don't have a projection object. */
  name?: string;
  /** Bare ContactType key, paired with `name`. */
  type?: string;
  /** sm = compact · md = default. */
  size?: 'sm' | 'md';
  /** Append the type label after the name. Default false (the glyph encodes it). */
  showType?: boolean;
  className?: string;
  style?: React.CSSProperties;
}

/**
 * Read display of a contact as a chip — colored type glyph + name, drawn
 * from the CONTACT_TYPES registry. The canonical way a linked or tagged
 * contact reads anywhere (Journal links, tagged People — Contacts of
 * type Person — transaction merchants). Handles archived + unavailable states.
 */
export declare function ContactChip(props: ContactChipProps): JSX.Element | null;
