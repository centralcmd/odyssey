import * as React from 'react';
import { ContactType } from './ContactTypeSelect';

/**
 * The slim, response-only custodian projection carried on an account read DTO.
 * A purpose-built subset of a contact — identifying/display fields only,
 * deliberately WITHOUT the free-text `description` (data-minimisation). Mirrors
 * the server `Custodian` DTO.
 */
export interface Custodian {
  /** The linked contact id (FK target). */
  contactId?: string;
  /** Display name. */
  name: string;
  /** ContactType key — drives the chip's icon + color via the registry. */
  type?: string;
  /** Normalized (UPPER+trim) name; present on the server projection. */
  normalizedName?: string;
  /** Organization / registration number, when the contact has one. */
  organizationNumber?: string | null;
  /** Archived timestamp, or null/absent when active. Truthy → archived chip. */
  archived?: string | null;
}

/** Resolve a ContactType key to its registry meta (icon · color · label). */
export declare function custodianTypeMeta(typeKey?: string): ContactType;

export interface CustodianChipProps {
  /** The custodian to display, or null/undefined for an account with none. */
  custodian?: Custodian | null;
  /** sm = the compact collapsed-row chip · md = the detail chip (default). */
  size?: 'sm' | 'md';
  /** Show the visible type label beside the name. The sr-only accessible name
   *  always includes the type regardless. Default true. */
  showType?: boolean;
  className?: string;
  style?: React.CSSProperties;
}

/**
 * Read-only, informational (non-navigating) display of an account's custodian —
 * the contact that holds it. Composes the chip visual language with the
 * ContactType registry; type and archived state are conveyed in text, the
 * icon is decorative. Renders a "No custodian" text chip when `custodian` is null.
 */
export declare function CustodianChip(props: CustodianChipProps): JSX.Element;
