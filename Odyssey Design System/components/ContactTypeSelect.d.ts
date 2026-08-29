import * as React from 'react';

export interface ContactType {
  /** Enum key — matches the C# ContactType member and the stored value. */
  key: string;
  /** Display label. */
  label: string;
  /** Material Icons ligature. */
  icon: string;
  /** Category color (oklch). */
  color: string;
  /** Soft 16% tint of `color`, for avatar backgrounds. */
  soft: string;
}

/**
 * Canonical ContactType registry — name · icon · color · soft tint, in
 * enum-declaration order (Other last). The design system's single source of
 * truth for the six members on the consumable layer; mirrors the C# enum and
 * `OdysseyData.contactTypes`.
 */
export declare const CONTACT_TYPES: ContactType[];

export interface ContactTypeSelectProps {
  /** Selected enum key (e.g. "Merchant"). */
  value?: string;
  /** Fires with the selected key first, the native event second. */
  onChange?: (value: string, event: React.MouseEvent<HTMLButtonElement>) => void;
  /** Field label. Defaults to "Type". */
  label?: string;
  /** Trigger placeholder when nothing is selected. */
  placeholder?: string;
  /** Override / subset the registry (e.g. drop "Other", or a custom order). */
  types?: ContactType[];
  help?: string;
  error?: string;
  required?: boolean;
  disabled?: boolean;
  className?: string;
  id?: string;
}

/**
 * Single-select pre-wired to the ContactType vocabulary — each option
 * carries its Material icon in its category color. A typed wrapper over `Select`.
 */
export declare function ContactTypeSelect(props: ContactTypeSelectProps): JSX.Element;
