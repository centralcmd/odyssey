export interface ErrorSummaryProblem {
  /** What is wrong, phrased as the row's own label plus the failure (e.g. "Privacy notice URL — must be https://"). */
  label: string;
  /** The section the row sits in, shown as a muted qualifier. */
  section?: string;
  /** `id` of the control to focus. Must belong to a RENDERED row — a filtered or disabled row is a dead end. */
  targetId?: string;
}

export interface ErrorSummaryProps {
  /** How many blocking problems there are. Defaults to `problems.length`. Renders nothing at 0. */
  count?: number;
  /** One entry per blocking field — turns the control into a disclosure listing every problem. */
  problems?: ErrorSummaryProblem[];
  /** Called when pressed with no `problems` list — move focus to the first blocking field. */
  onReview?: () => void;
  /** Called with the picked problem. Defaults to focusing `targetId`. */
  onJump?: (problem: ErrorSummaryProblem) => void;
  /** Singular noun, pluralised automatically. Default "problem". */
  noun?: string;
  /** Action word. Default "Review". */
  action?: string;
  className?: string;
}

/**
 * Compact "n problems · Review" button placed immediately before a disabled
 * primary action, on pages long enough that the blocking field is off-screen.
 * With `problems` it expands to a list that focuses each offending field.
 * Announces nothing itself — the page announces politely on a save attempt.
 */
export declare function ErrorSummary(props: ErrorSummaryProps): JSX.Element | null;
