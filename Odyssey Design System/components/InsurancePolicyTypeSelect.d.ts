import * as React from 'react';

export interface InsurancePolicyType {
  /** Enum key — matches the C# InsurancePolicyType member and stored value. */
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
 * Canonical InsurancePolicyType registry — name · icon · color · soft tint, in
 * enum order (Other last). The design system's single source of truth on the
 * consumable layer; mirrors the C# enum and `OdysseyData.insurancePolicyTypes`.
 */
export declare const INSURANCE_POLICY_TYPES: InsurancePolicyType[];

export interface InsurancePolicyTypeSelectProps {
  /** Selected enum key (e.g. "Vehicle"). */
  value?: string;
  /** Fires with the selected key first, the native event second. */
  onChange?: (value: string, event: React.MouseEvent<HTMLButtonElement>) => void;
  /** Field label. Defaults to "Policy type". */
  label?: string;
  /** Trigger placeholder when nothing is selected. */
  placeholder?: string;
  /** Override / subset the registry (e.g. a custom order). */
  types?: InsurancePolicyType[];
  help?: string;
  error?: string;
  required?: boolean;
  disabled?: boolean;
  className?: string;
  id?: string;
}

/**
 * Single-select pre-wired to the InsurancePolicyType vocabulary — each option
 * carries its Material icon in its category color. A typed wrapper over `Select`.
 */
export declare function InsurancePolicyTypeSelect(props: InsurancePolicyTypeSelectProps): JSX.Element;
