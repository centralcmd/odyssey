import { SmartTagScope, SmartTagScopeTerm } from './SmartTagScopeField';

export interface SmartTagScopeSyncPromptProps {
  /** The term as loaded, before this edit. */
  before?: SmartTagScopeTerm;
  /** The term as currently entered in the dialog. */
  after?: SmartTagScopeTerm;
  /** The stored scope, carried forward unless `checked`. */
  scope?: SmartTagScope;
  /** true = the save writes `fromTerm(after)` as the scope. Default false. */
  checked?: boolean;
  onChange?: (checked: boolean) => void;
  className?: string;
}

/**
 * SmartTagScopeSyncPrompt — the "also update the scope?" band shown in the Edit
 * contract dialog when the term dates change. Renders nothing unless the term
 * changed AND the scope differs from what the new term gives. Unchecked by
 * default, so setting an end date on an ongoing contract keeps the scope open.
 */
export declare function SmartTagScopeSyncPrompt(props: SmartTagScopeSyncPromptProps): JSX.Element | null;
