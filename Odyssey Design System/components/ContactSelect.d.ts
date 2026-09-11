import * as React from 'react';

export interface ContactSelectContact {
  /** Contact id (either key is accepted). */
  id?: string;
  contactId?: string;
  name: string;
  /** ContactType key — supplies the row's glyph + color. */
  type?: string;
  /** Archived contacts are not selectable. */
  archived?: string | null;
  /**
   * Person date of death (`YYYY-MM-DD`). Present → the option label gains a
   * ` · Deceased` suffix. The contact stays selectable: recording a death
   * removes no capability.
   */
  dateOfDeath?: string | null;
  /** Organization dissolved date — same treatment, ` · Dissolved`. */
  dissolvedDate?: string | null;
}

export interface ContactCreateKind {
  /** Passed to `onCreate` as the picked type (e.g. "Person"). */
  key: string;
  label: string;
  icon?: string;
}

/** Organization, then Person — the default inline-create rows. */
export declare const CONTACT_CREATE_KINDS: ContactCreateKind[];

export interface ContactSelectProps {
  /** Selected contact id, or '' / null for none. */
  value?: string | null;
  /** Fires with the picked id ('' on clear) and the option. */
  onChange?: (id: string, option?: { value: string; label: string }) => void;
  /** The candidate contacts; archived ones are filtered out. */
  contacts?: ContactSelectContact[];
  /** Pre-built display options — use instead of `contacts` when the caller already has them. */
  options?: Array<{ value: string; label: string; icon?: string; iconColor?: string }>;
  /** Field label. Default "Contact". */
  label?: React.ReactNode;
  optional?: boolean;
  required?: boolean;
  placeholder?: string;
  emptyText?: string;
  help?: React.ReactNode;
  error?: React.ReactNode;
  loading?: boolean;
  disabled?: boolean;
  /** Drop the label / help chrome — for a table cell or a caller's own FieldShell. */
  bare?: boolean;
  /** Offer inline create rows. Gate on the caller's `contacts.create` claim. */
  allowCreate?: boolean;
  /** Create the contact and return the option to select: `(name, kind) => option`. */
  onCreate?: (name: string, kind: string) => string | { value: string; label: string; icon?: string; iconColor?: string } | undefined | null;
  /** Prefix on the create rows. Default "Add". */
  createLabel?: string;
  /** Override the create rows (default: Organization, then Person). */
  createKinds?: ContactCreateKind[];
  ariaLabel?: string;
  className?: string;
  id?: string;
}

/**
 * The canonical contact picker — a `Combobox` over the contact vocabulary with
 * per-type glyphs and optional typed inline create. Use it for every field that
 * links a record to a contact (counterparty, issuer, biller, merchant).
 */
export declare function ContactSelect(props: ContactSelectProps): JSX.Element;
