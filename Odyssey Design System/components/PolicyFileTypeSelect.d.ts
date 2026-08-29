import * as React from 'react';

export interface PolicyFileType {
  /** Enum key — matches the C# PolicyFileType member and stored value. */
  key: string;
  /** Display label. */
  label: string;
  /** Material Icons ligature. */
  icon: string;
  /** Category color (oklch). */
  color: string;
  /** Soft 16% tint of `color`. */
  soft: string;
}

/**
 * Canonical PolicyFileType registry — the documents that attach to an insurance
 * policy or a renewal. Mirrors the C# enum and `OdysseyData.policyFileTypes`.
 */
export declare const POLICY_FILE_TYPES: PolicyFileType[];

export interface PolicyFileTypeSelectProps {
  value?: string;
  onChange?: (value: string, event: React.MouseEvent<HTMLButtonElement>) => void;
  /** Field label. Defaults to "Document type". */
  label?: string;
  placeholder?: string;
  /** Override / subset the registry. */
  types?: PolicyFileType[];
  help?: string;
  error?: string;
  required?: boolean;
  disabled?: boolean;
  className?: string;
  id?: string;
}

/**
 * Single-select pre-wired to the PolicyFileType vocabulary — each option carries
 * its Material icon in its category color. A typed wrapper over `Select`.
 */
export declare function PolicyFileTypeSelect(props: PolicyFileTypeSelectProps): JSX.Element;
