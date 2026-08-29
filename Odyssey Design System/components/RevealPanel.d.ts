import * as React from 'react';
import { SegmentedOption } from './SegmentedControl';

export interface RevealPanelProps {
  /** Selected value of the header toggle (a `SegmentedControl`). */
  value?: string;
  onChange?: (value: string) => void;
  /** Header toggle options (same shape as `SegmentedControl`). */
  options?: Array<SegmentedOption | string>;
  ariaLabel?: string;
  /** The value(s) that open the connected body. Ignored when `open` is passed. */
  openValue?: string | string[];
  /** Explicit open state — overrides `openValue` (e.g. open only when editable). */
  open?: boolean;
  /** Render a static, non-interactive header instead of the toggle. */
  locked?: boolean;
  /** Header content when `locked` (a status line, a read-only summary). */
  lockedContent?: React.ReactNode;
  /** The revealed body — shown, and visually connected, only when open. */
  children?: React.ReactNode;
  className?: string;
}

/** A segmented toggle whose selection **reveals a connected panel** below it.
 *  When closed it is just the bare toggle; when open, the toggle becomes the
 *  header of one bordered surface with the body attached beneath a divider, so
 *  the choice and the fields it controls read as a single control (recurrence
 *  rules, conditional option groups, "advanced" reveals). Composes the DS
 *  `SegmentedControl` for the header. */
export declare function RevealPanel(props: RevealPanelProps): JSX.Element;
