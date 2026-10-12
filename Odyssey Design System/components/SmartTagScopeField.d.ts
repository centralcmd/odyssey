/** A contract's smart-tag scope: two calendar days (`YYYY-MM-DD`); null = open on that side. */
export interface SmartTagScope {
  start: string | null;
  end: string | null;
}

/** The term fields the scope is compared with (and copied from on create). */
export interface SmartTagScopeTerm {
  startDate?: string | null;
  endDate?: string | null;
  /** Set on a one-off contract — it has no term, so `fromTerm` gives open/open. */
  completionDate?: string | null;
}

export interface SmartTagScopeFieldProps {
  value?: SmartTagScope;
  /** Fires with the next scope, each side already normalised to a calendar day. */
  onChange?: (next: SmartTagScope) => void;
  /** The contract's CURRENT term. Shown for comparison only — never validated against. */
  term?: SmartTagScopeTerm;
  /** Field errors from `SMART_TAG_SCOPE_RULES.validate` or the server (400 bounds, 422 order on `end`). */
  errors?: { start?: string; end?: string };
  disabled?: boolean;
  /** The term-vs-scope strip. Default true. */
  showStrip?: boolean;
  /** "Copy the contract term" · "Remove both limits". Default true. */
  showActions?: boolean;
  className?: string;
}

/** Client half of the server's SmartTagScopeRules (§8 of the backend spec). */
export declare const SMART_TAG_SCOPE_RULES: {
  minDate: '1900-01-01';
  maxDate: '9999-12-31';
  /** Rule 2: client value → calendar day. Offsets convert to UTC before truncation. */
  normalize(input: string | null | undefined): string | null;
  /** Rule 4: the scope a contract is created, seeded and backfilled with. */
  fromTerm(term: SmartTagScopeTerm): SmartTagScope;
  /** Rules 1 + 3: bounds per side, then start ≤ end (keyed `end`). Same day is valid. */
  validate(scope: SmartTagScope): { start?: string; end?: string };
  equals(a: SmartTagScope, b: SmartTagScope): boolean;
  /** A day → `YYYY-MM-DDT00:00:00Z` for `UpdateContract`; null stays null. */
  toWire(day: string | null): string | null;
  formatDay(day: string | null): string;
  /** "1 Sep 2025 – 31 Aug 2026" · "from 1 Sep 2025, no end" · "until 31 Aug 2026" · "any date". */
  formatRange(scope: SmartTagScope): string;
  /** The scope against the term, as one sentence. */
  describe(scope: SmartTagScope, term: SmartTagScopeTerm): string;
};

/**
 * SmartTagScopeField — the editor for a contract's smart-tag scope: "Match
 * from" + "Match until" date fields (empty = open), a term-vs-scope strip, a
 * sentence stating the relation in words, and two shortcuts (copy the term,
 * remove both limits). Independent of the term by design.
 */
export declare function SmartTagScopeField(props: SmartTagScopeFieldProps): JSX.Element;
