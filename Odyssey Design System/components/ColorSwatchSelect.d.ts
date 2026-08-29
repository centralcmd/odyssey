import * as React from 'react';

export interface CalendarSwatch {
  /** Stable key (e.g. 'blue'). */
  key: string;
  /** Human name — the swatch's accessible label ("Blue"). */
  name: string;
  /** The stored value: a 6-digit hex string (Calendar.Color). */
  hex: string;
  /** Pre-vetted foreground baked into the palette — every swatch clears WCAG
   *  1.4.3 for chip label text, so a chip never has to compute contrast. */
  fg: string;
}

/** The curated, contrast-vetted calendar palette. Each entry ships a baked
 *  foreground, so a chip painted with `hex`/`fg` always meets AA. Mapped onto
 *  the Odyssey ramps (sea / tide / mint / coral / violet / amber / ink) rather
 *  than generic Material hues — see the calendar-module card note. */
export declare const CALENDAR_SWATCHES: CalendarSwatch[];

/** The default swatch hex (Blue). */
export declare const DEFAULT_CALENDAR_COLOR: string;

/** Look a swatch up by its stored hex; falls back to the default swatch so a
 *  legacy / unknown value still renders a legible chip. */
export declare function swatchFor(hex: string | null | undefined): CalendarSwatch;

export interface ColorSwatchSelectProps {
  /** Selected swatch hex (Calendar.Color). */
  value?: string | null;
  /** Fires with the chosen swatch hex. */
  onChange?: (hex: string) => void;
  /** Override the palette (defaults to CALENDAR_SWATCHES). */
  swatches?: CalendarSwatch[];
  disabled?: boolean;
  /** Explicit id for the group; auto-generated if omitted. */
  id?: string;
  /** Accessible name for the radiogroup. Default "Calendar colour". */
  ariaLabel?: string;
}

/** A single-select grid of curated colour swatches — the calendar-colour
 *  chooser. NOT a free hex/HSV picker: only palette membership is selectable,
 *  which is what lets every chip guarantee a contrast-safe foreground. Renders
 *  as an ARIA radiogroup (arrow keys move, Space/Enter select). */
export declare function ColorSwatchSelect(props: ColorSwatchSelectProps): JSX.Element;
