import * as React from 'react';

/** A method kind — which of a contact's three collections a row belongs to. */
export type ContactMethodKind = 'address' | 'email' | 'phone';

/** A contact type key — the second input the offered label set depends on. */
export type ContactTypeKey = 'Person' | 'Organization';

export interface ContactMethodLabel {
  /** Enum member name — the persisted key and the vCard `X-ODYSSEY-LABEL` value. */
  key: string;
  /** Display string. `EmailLabel.Home` displays as "Personal". */
  label: string;
  /** Material Icons ligature. */
  icon: string;
  /** Enum ordinal — the wire contract (these enums serialize as integers). */
  ordinal: number;
  /** Neutral categorical foreground, shared by every label. */
  color: string;
}

/** `AddressLabel` members, in ordinal order. */
export declare const ADDRESS_LABELS: ContactMethodLabel[];
/** `EmailLabel` members, in ordinal order. */
export declare const EMAIL_LABELS: ContactMethodLabel[];
/** `PhoneLabel` members, in ordinal order. */
export declare const PHONE_LABELS: ContactMethodLabel[];

/**
 * The per-contact-type scope of the three label vocabularies — the design
 * system's mirror of the C# `ContactLabelScope`. One source for the offered
 * set, the per-type default, the write-path check and the clamp.
 */
export declare const ContactLabelScope: {
  /** Every member of a kind's enum, in ordinal order. */
  all(kind: ContactMethodKind): ContactMethodLabel[];
  /** The offered set for a (kind, contactType), in display order. */
  labelsFor(kind: ContactMethodKind, type: ContactTypeKey): ContactMethodLabel[];
  /** The label a new method opens on — always `labelsFor(...)[0]`. */
  defaultFor(kind: ContactMethodKind, type: ContactTypeKey): string;
  /** True when `key` is in the offered set for `type`. */
  isValidFor(kind: ContactMethodKind, key: string, type: ContactTypeKey): boolean;
  /** Returns `key` when valid, otherwise `'Other'`. Never drops a row. */
  clamp(kind: ContactMethodKind, key: string, type: ContactTypeKey): string;
  /** Descriptor for a stored key; unknown keys resolve `Other` **by key**. */
  metaFor(kind: ContactMethodKind, key: string): ContactMethodLabel;
  /** Ordinal for a member name, or null when the name is undefined. */
  ordinalOf(kind: ContactMethodKind, key: string): number | null;
};

export interface ContactMethodLabelSelectProps {
  /** Which collection the row belongs to. Defaults to "phone". */
  kind?: ContactMethodKind;
  /** The PARENT contact's type — half of what decides the option list. */
  contactType?: ContactTypeKey;
  /** Selected enum member name. A value outside the offered set renders the placeholder. */
  value?: string;
  onChange?: (value: string, event: React.MouseEvent<HTMLButtonElement>) => void;
  /** Field label. Defaults to "Label". */
  label?: string;
  /** Trigger placeholder. Defaults to "Select label…". */
  placeholder?: string;
  help?: string;
  /** Error text. Use required-style wording — an out-of-scope value reads as empty. */
  error?: string;
  required?: boolean;
  disabled?: boolean;
  className?: string;
  id?: string;
}

/**
 * The Label picker on a contact method — the only registry select whose
 * options depend on a sibling field (the parent contact's type).
 */
export declare function ContactMethodLabelSelect(props: ContactMethodLabelSelectProps): JSX.Element;
