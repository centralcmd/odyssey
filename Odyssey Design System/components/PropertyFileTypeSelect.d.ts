import * as React from 'react';

export interface PropertyFileType {
  /** Enum key — matches the C# PropertyFileType member and the stored value. */
  key: string;
  label: string;
  /** Numeric enum value. `Other` is 0. */
  enumValue: number;
  icon: string;
  color: string;
  soft: string;
}

/**
 * Canonical PropertyFileType registry — name · icon · color · soft tint ·
 * enumValue, in enum order with `Other` (ordinal 0, the default) last. Mirrors
 * the C# enum and `OdysseyData.propertyFileTypes`.
 */
export declare const PROPERTY_FILE_TYPES: PropertyFileType[];

export interface PropertyFileTypeSelectProps {
  /** Selected enum key (e.g. "Deed"). */
  value?: string;
  onChange?: (value: string, event: React.MouseEvent<HTMLButtonElement>) => void;
  label?: string;
  placeholder?: string;
  types?: PropertyFileType[];
  help?: string;
  error?: string;
  required?: boolean;
  disabled?: boolean;
  className?: string;
  id?: string;
}

/**
 * Single-select pre-wired to the PropertyFileType vocabulary (documents attached
 * to a property) — each option carries its Material icon in its category color.
 * A typed wrapper over `Select`.
 */
export declare function PropertyFileTypeSelect(props: PropertyFileTypeSelectProps): JSX.Element;
