import * as React from 'react';

export interface Coordinate {
  /** Latitude in decimal degrees (−90…90), or null when empty. */
  lat: number | null;
  /** Longitude in decimal degrees (−180…180), or null when empty. */
  lng: number | null;
}

export interface CoordinateFieldProps {
  /** Controlled `{ lat, lng }` pair. Each may be a number, a numeric string, or null. */
  value?: { lat?: number | string | null; lng?: number | string | null } | null;
  /** Fires with the next `{ lat, lng }` pair (values parsed to number | null). */
  onChange?: (value: Coordinate) => void;
  /** Label for the latitude field. Default "Latitude". */
  latLabel?: string;
  /** Label for the longitude field. Default "Longitude". */
  lngLabel?: string;
  /** Helper text below the pair. Suppressed while a range error shows. */
  help?: string;
  /** Explicit error message below the pair. */
  error?: string;
  required?: boolean;
  optional?: boolean;
  disabled?: boolean;
  className?: string;
}

/**
 * Paired latitude / longitude entry — two `NumberField`s in a `FormRow`, each
 * with its geographic range enforced (lat −90…90, lng −180…180) and an inline
 * out-of-range error. Value is a `{ lat, lng }` pair of number | null.
 */
export declare function CoordinateField(props: CoordinateFieldProps): JSX.Element;
