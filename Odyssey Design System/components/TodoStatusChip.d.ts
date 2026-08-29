import * as React from 'react';

export type TodoStatusKey = 'Backlog' | 'Doing' | 'Done' | 'Archived';

export interface TodoStatusMeta {
  key: TodoStatusKey;
  /** Visible label — the state meaning, conveyed as text (a11y). */
  label: string;
  tone: 'outline' | 'info' | 'income';
  /** Numeric enum order (Backlog 0 · Doing 1 · Done 2 · Archived 3). */
  value: number;
  dot: boolean;
  icon: string;
}

/** Canonical to-do status vocabulary (Backlog · Doing · Done · Archived). */
export declare const TODO_STATUSES: TodoStatusMeta[];

export interface TodoStatusChipProps {
  /** The task's single kanban status. Default 'Backlog'. */
  status?: TodoStatusKey;
  /** Lead with the status glyph instead of the status dot. */
  showIcon?: boolean;
  /** sm = compact · md = default. */
  size?: 'sm' | 'md';
  className?: string;
  style?: React.CSSProperties;
}

/**
 * A to-do task's kanban status (Backlog / Doing / Done / Archived) as one chip,
 * meaning conveyed as visible text. Tasks' sibling of SubscriptionStatusChip.
 */
export declare function TodoStatusChip(props: TodoStatusChipProps): JSX.Element;
