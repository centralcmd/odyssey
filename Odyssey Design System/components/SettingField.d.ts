import * as React from 'react';

export interface SettingFieldProps {
  /** The setting's label — rendered on the field's outline (MudBlazor `Variant.Outlined`). */
  label: React.ReactNode;
  /** `id` of the control inside, so the label is a real `<label for>`. */
  htmlFor?: string;
  /** Explicit `id` for the label element — for a control with no single focusable input, which points `aria-labelledby` here instead. */
  labelId?: string;
  /** What the setting does. Always visible, first on the helper line — never behind a disclosure. */
  help?: React.ReactNode;
  /** Provenance: who last changed the value and when. Renders dimmer at the end of the helper line. */
  meta?: React.ReactNode;
  /** Blocking message. Renders above the helper line and turns the outline coral; does not displace `help`. */
  error?: React.ReactNode;
  /**
   * Informational message in an amber band below the helper line, opening with
   * the literal word "Advisory". `role="status"` — for a value that will save but
   * carries a cost (memory, payload, third-party spend) or looks wrong on a
   * heuristic. Never gates the primary action, never sets `aria-invalid`; a value
   * that cannot be accepted is an `error` instead.
   */
  advisory?: React.ReactNode;
  /**
   * Marks a setting that moves one way only, with a marker in the outline beside
   * the label. Use where the opposite direction is refused, not just discouraged:
   * a cap whose cost survives being lowered back (`lower-only`), or a control
   * that fails open when it fills, so a smaller number weakens it
   * (`raise-only`). The reason belongs in `help`.
   */
  bound?: 'lower-only' | 'raise-only';
  /** Show the unsaved-change dot on the helper line. */
  dirty?: boolean;
  /** Span both columns of a two-column setting grid — for a control whose width is set by its content (a URL, a name). */
  wide?: boolean;
  className?: string;
  id?: string;
  /** Exactly one control. Its own border, background and padding are flattened inside the frame. */
  children?: React.ReactNode;
}

/**
 * One setting as a self-contained field block in the MudBlazor
 * `Variant.Outlined` shape: label on the outline, control inside, description +
 * "last changed" stamp on one always-visible helper line below. The half-width
 * alternative to `SettingRow` — lets a section card hold a grid of related
 * settings instead of one card per setting.
 */
export declare function SettingField(props: SettingFieldProps): JSX.Element;
