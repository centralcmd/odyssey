import * as React from 'react';

export interface ReferenceNumberFieldProps {
  /** Default "Reference number". */
  label?: string;
  /** The raw, untrimmed input. */
  value?: string;
  /** Fires with the next string. Also fires once on blur with the trimmed value when trimming changed it. */
  onChange?: (value: string, event?: React.SyntheticEvent) => void;
  onBlur?: (event: React.FocusEvent<HTMLInputElement>) => void;
  placeholder?: string;
  /** Helper line under the input; replaced by any error. */
  help?: string;
  /** A server-side error (the 400 keyed on `referenceNumber`). Live rule errors are computed by the field itself. */
  error?: string;
  disabled?: boolean;
  autoFocus?: boolean;
  className?: string;
  id?: string;
}

export interface ReferenceNumberViolation {
  code: 'reference_number_too_long' | 'reference_number_invalid_characters';
  /** User-facing sentence. Never contains the submitted value. */
  message: string;
  hidden?: { codePoint: string; name: string; count: number };
}

/** The client half of the contract DTO's `referenceNumber` rules. */
export declare const REFERENCE_NUMBER_RULES: {
  /** 64 — mirrors `[StringLength(64)]`. UTF-16 code units, as .NET counts. */
  maxLength: number;
  /** Trim; empty → null. Interior content kept verbatim. */
  normalize(value: string | null | undefined): string | null;
  /** Remove every Cc / Cf / Co / Cn character. */
  stripHidden(value: string): string;
  findHidden(value: string): { codePoint: string; name: string; count: number } | null;
  /** First broken rule, or null. Hidden characters are reported before length. */
  validate(value: string): ReferenceNumberViolation | null;
};

/**
 * Single-line mono input for a counterparty's identifier (a contract's
 * `referenceNumber`): 0/64 counter, live hidden-character and length errors,
 * one-click removal of hidden characters, trim on blur. No native maxLength —
 * a paste is never silently truncated.
 */
export declare function ReferenceNumberField(props: ReferenceNumberFieldProps): JSX.Element;
