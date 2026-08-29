import * as React from 'react';

export interface CollapsibleProps {
  /** Header label. */
  title: React.ReactNode;
  /** Leading Material Icons ligature. */
  lead?: string;
  /** Alias of `lead` — leading Material Icons ligature. */
  icon?: string;
  /** A muted pill at the end of the header label (e.g. an item count). */
  count?: React.ReactNode;
  /** Optional control pinned to the right of the header (e.g. a "View all" / "New X" button). Stays independently clickable. */
  action?: React.ReactNode;
  /** Controlled open state — pair with onToggle. */
  open?: boolean;
  /** Initial open state when uncontrolled. */
  defaultOpen?: boolean;
  /** Fires with the next open state on header activation. */
  onToggle?: (open: boolean) => void;
  /** Drop the border and horizontal padding (embed inside another surface). */
  flush?: boolean;
  /** Wrap the trigger in a heading of this level (ARIA accordion pattern) for screen-reader navigation. Default 2; pass 0/undefined to opt out (e.g. a non-section "advanced options" reveal). */
  headingLevel?: number;
  children?: React.ReactNode;
}

/** Disclosure — header row that expands a body in place. */
export declare function Collapsible(props: CollapsibleProps): JSX.Element;
