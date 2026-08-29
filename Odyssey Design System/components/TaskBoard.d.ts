import * as React from 'react';

export interface TaskBoardTask {
  id: string;
  /** Column key — one of the `columns` keys (Backlog / Doing / Done). */
  status: string;
  /** Ascending sort order within the column. */
  position?: number;
  /** Used in the live-region announcement. */
  title?: string;
  [key: string]: any;
}

export interface TaskBoardColumn {
  key: string;
  label: string;
}

export interface TaskBoardProps {
  /** The full task set; the board buckets by `status` and sorts by `position`. */
  tasks?: TaskBoardTask[];
  /** Columns, in order. Default Backlog · Doing · Done (Archived is off-board). */
  columns?: TaskBoardColumn[];
  /** Render the card body for a task (include a TodoStatusChip for text status). */
  renderCard?: (task: TaskBoardTask) => React.ReactNode;
  /** Called on every move (drag or keyboard). `toIndex` is the 0-based slot in the target column. */
  onMove?: (id: string, toStatus: string, toIndex: number) => void;
  /** Placeholder text for an empty column. */
  emptyColumnText?: string;
  className?: string;
  style?: React.CSSProperties;
}

/**
 * Three-column kanban board (Backlog · Doing · Done) with dual-path moves —
 * HTML5 drag-and-drop plus an always-present keyboard button cluster (up / down
 * / prev-column / next-column) — every move announced via a polite live region.
 * Columns are labelled landmark regions with text counts.
 */
export declare function TaskBoard(props: TaskBoardProps): JSX.Element;
