import * as React from 'react';

export interface ContactAlias {
  /** Server id (GUID). */
  id: string;
  /** The alternative name, ≤ 128 chars, trimmed and whitespace-collapsed. */
  value: string;
  /**
   * What kind of alias it is — **free text**, ≤ 64 chars ("maiden name",
   * "nickname", "trading as"). Deliberately not an enum: a user names the
   * naming relationship in their own words. Consequence: it is metadata only —
   * not searched, not filterable, not localised.
   */
  label?: string | null;
  /** Present on the wire (parent id); unused by this component. */
  contactId?: string;
}

export interface ContactAliasesProps {
  /** The contact's aliases, **in the order the API returned them** (no client re-sort). */
  aliases?: ContactAlias[];
  /** Caller holds `contacts.create` — gates the empty-state copy and the add dialog. */
  canCreate?: boolean;
  /** Caller holds `contacts.update` — gates Edit. */
  canUpdate?: boolean;
  /** Caller holds `contacts.delete` — gates Delete. Distinct from `canUpdate` on purpose. */
  canDelete?: boolean;
  /** The parent contact is archived: no add, no edit, no delete. */
  archived?: boolean;
  /** Per-contact cap; the 33rd alias is refused. Default 32. */
  cap?: number;
  /** `{ nonce }` token from the record card's ⋯ menu "Add alias" item. */
  addRequest?: { nonce: number | string } | null;
  /** Called once the add request has opened the dialog. */
  onConsumeAddRequest?: () => void;
  /** Create. Return an error string to keep the dialog open with it on the value field. */
  onAdd?: (value: string, label: string | null) => void | string;
  /** Replace value + label (a `null` label CLEARS it). Same error contract as `onAdd`. */
  onEdit?: (id: string, value: string, label: string | null) => void | string;
  /** Delete — immediate, no confirmation. Return an error string to have it announced. */
  onDelete?: (id: string) => void | string;
  /**
   * Route announcements to the host's single polite `LiveAnnouncer` instead of
   * this component's own region (the Blazor record card owns one).
   */
  onAnnounce?: (message: string) => void;
  className?: string;
}

/** Case- and accent-insensitive alias equality — what the service, the unique index and this pre-check agree on. */
export declare function aliasEquals(a: string, b: string): boolean;
/** Trim + collapse whitespace: the canonical form stored on write. */
export declare function canonicalAlias(value: string): string;

export declare const CONTACT_ALIAS_CAP: number;
export declare const CONTACT_ALIAS_MAX: number;
export declare const CONTACT_ALIAS_LABEL_MAX: number;

/**
 * The **Aliases** section of a contact record: alternative names (nickname,
 * maiden name, former company name, abbreviation), each with an optional
 * free-text label. Renders bare — the record card emits the `SectionDivider`
 * above it — as tiles in its own grid with the standard `ActionMenu` slot, plus
 * the add / edit `Modal`, the duplicate / cap / validation rejections inline on
 * the value field, and one polite live region for outcomes.
 */
export declare function ContactAliases(props: ContactAliasesProps): JSX.Element;
