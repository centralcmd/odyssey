import * as React from 'react';

export interface FieldShellProps {
  /** Visible label, rendered above the control. Omit for an unlabelled control. */
  label?: React.ReactNode;
  /** `id` of the control inside — ties the `<label>` to it and ids the helper line as `<htmlFor>-help`. */
  htmlFor?: string;
  /** Adds a `*` after the label (the canonical required marker). */
  required?: boolean;
  /** Adds a muted "Optional" hint after the label. Mutually exclusive with `required`. */
  optional?: boolean;
  /** Helper text shown below the control. */
  help?: React.ReactNode;
  /** Error message — flips the shell to its error state and replaces the helper. */
  error?: React.ReactNode;
  /** Right-aligned slot on the label row — e.g. a character counter. Forces the head row layout. */
  aside?: React.ReactNode;
  /** The control to wrap. */
  children?: React.ReactNode;
  className?: string;
  id?: string;
}

/** The labelled-field wrapper (label + required/optional marker + helper/error line) shared by every form control. */
export declare function FieldShell(props: FieldShellProps): JSX.Element;
