/** The five password requirements a candidate string satisfies (or not). */
export interface PasswordRule {
  /** Stable key for React lists. */
  key: 'len' | 'upper' | 'lower' | 'digit' | 'sym';
  /** Human-readable requirement, e.g. "At least 16 characters". */
  label: string;
  /** Whether the candidate currently satisfies this rule. */
  met: boolean;
}

/**
 * The single client-side mirror of the server's IdentityOptions.Password gate.
 * The one declaration of the minimum length and the rule set — no other file
 * should re-declare either.
 */
export declare const PASSWORD_POLICY: {
  /** Minimum length. Kept equal to IdentityOptions.Password.RequiredLength (16). */
  readonly minLength: number;
  /** The rule set evaluated against a candidate password. */
  rules(candidate: string | null | undefined): PasswordRule[];
  /** True when every rule is met. Drive a submit button's disabled state from this. */
  isSatisfied(candidate: string | null | undefined): boolean;
};

export interface PasswordRulesProps {
  /** The candidate password to evaluate live. */
  password?: string;
  /** 1 (stacked, default — fits the 420px auth card) or 2 (grid, for wide forms). */
  columns?: 1 | 2;
  /** Overrides the list's accessible name. */
  'aria-label'?: string;
  className?: string;
}

/** Live password-requirement checklist. Met state is icon + text, never colour alone. */
export declare function PasswordRules(props: PasswordRulesProps): JSX.Element;
