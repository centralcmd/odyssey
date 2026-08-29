import * as React from 'react';

export interface AccountFileType {
  /** Enum key — matches the C# AccountFileType member and the stored value. */
  key: string;
  label: string;
  /** Numeric enum value. */
  enumValue: number;
  /** Material Icons ligature. */
  icon: string;
  /** Category color (oklch). */
  color: string;
  /** Soft 16% tint of `color`, for avatar backgrounds. */
  soft: string;
}

/**
 * Canonical AccountFileType registry — name · icon · color · soft tint · enumValue,
 * in enum order with `Other` (the default) last. The consumable layer's source of
 * truth for account file types; mirrors the C# enum and `OdysseyData.accountFileTypes`.
 */
export declare const ACCOUNT_FILE_TYPES: AccountFileType[];

export interface AccountFileTypeSelectProps {
  /** Selected enum key (e.g. "Statement"). */
  value?: string;
  onChange?: (value: string, event: React.MouseEvent<HTMLButtonElement>) => void;
  /** Field label. Defaults to "Type". */
  label?: string;
  placeholder?: string;
  /** Override / subset the registry. */
  types?: AccountFileType[];
  help?: string;
  error?: string;
  required?: boolean;
  disabled?: boolean;
  className?: string;
  id?: string;
}

/**
 * Single-select pre-wired to the AccountFileType vocabulary (files attached to an
 * account) — each option carries its Material icon in its category color. A typed
 * wrapper over `Select`.
 */
export declare function AccountFileTypeSelect(props: AccountFileTypeSelectProps): JSX.Element;
