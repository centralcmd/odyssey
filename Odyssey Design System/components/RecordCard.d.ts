import * as React from 'react';

export interface RecordCardCount {
  /** Material Icons ligature, or a literal glyph such as "§". */
  icon: string;
  /** The count itself. */
  value: React.ReactNode;
  /** Accessible name / tooltip — "Transactions", "Files". */
  label?: string;
}

export interface RecordCardFigure {
  /** The headline number, already formatted (tabular, ISO currency, "−" for negatives). */
  value: React.ReactNode;
  /** Small uppercase caption under it — "Est. value", "USD / month". */
  caption?: React.ReactNode;
  /** Colour role for the value. */
  tone?: 'income' | 'expense' | 'pending' | 'neutral';
}

export interface RecordCardProps {
  /** Material Icons ligature for the record's TYPE, or its type-equivalent — a categorical registry the record always has exactly one of (Accounts: account type; Subscriptions: billing interval). Never derived state. */
  icon?: string;
  /** The type's (or type-equivalent's) colour. Sets --rec on the card, inherited by every icon and single-series chart inside it. Omit for the brand accent. */
  accent?: string;
  /** The type's soft/background tint (usually the accent at 16%). Sets --rec-soft. */
  accentSoft?: string;
  /** The record's name — the one thing a user scans for. */
  name: React.ReactNode;
  /** Status / problem chips beside the name. */
  chips?: React.ReactNode;
  /** The single meta line, as an array of nodes joined with "·" separators. Ellipsised, never wrapped. */
  meta?: React.ReactNode[];
  /** Sub-collection counts, in the same order as the body's sections. */
  counts?: RecordCardCount[];
  /** The right-hand headline figure. Omit for records that have none (journal entries) — never invent one. */
  figure?: RecordCardFigure;
  /** Row actions (an ActionMenu). Sits outside the trigger, so it stays independently clickable. */
  actions?: React.ReactNode;
  /** Body slot 1 — a ProblemAlert / Alert. Always first: it is why the card was opened. */
  alert?: React.ReactNode;
  /** Body slot 2 — the record's full field set, as an InfoTileGrid of InfoTiles. Fields the header already shows are repeated here on purpose: a labelled tile reads as a field, not an echo. Fields with no value render no tile, unless the absence itself is the fact. Tiles never condition on each other — a derived tile (Status) summarises, it does not replace the tiles of the fields it is computed from. */
  details?: React.ReactNode;
  /** Body slot 3 — description / notes / entry text, in one wide InfoTile. */
  content?: React.ReactNode;
  /** Controlled open state — pair with onToggle. A list owns ONE openId and passes `open={openId === r.id}`: opening a record closes its siblings. */
  open?: boolean;
  /** Initial open state when uncontrolled. */
  defaultOpen?: boolean;
  /** Fires with the next open state. */
  onToggle?: (open: boolean) => void;
  /** Closed / archived records: fades the header. */
  dimmed?: boolean;
  /** One-shot attention ring, e.g. when a problems rollup jumps to this record. */
  highlight?: boolean;
  /** Heading level wrapping the trigger (ARIA accordion pattern). 0 to opt out. Default 2. */
  headingLevel?: number;
  className?: string;
  /** Body slot 4 — the sections, each introduced by a SectionDivider. */
  children?: React.ReactNode;
}

/** The expandable record card behind every record list. Row height is fixed at a shared 88px floor; body order is fixed: alert → details → content → sections. */
export declare function RecordCard(props: RecordCardProps): JSX.Element;
