import * as React from 'react';

export interface CustodianContact {
  /** Contact id (either `id` or `contactId` is accepted). */
  id?: string;
  contactId?: string;
  /** Display name. */
  name: string;
  /** ContactType key — drives the option's leading icon. */
  type?: string;
  /** Archived timestamp / flag. Archived contacts are filtered out of
   *  the selectable options (an archived custodian can't be picked). */
  archived?: string | null;
}

export interface CustodianSelectProps {
  /** Selected contact id, or '' / null for no custodian. */
  value?: string | null;
  /** Fires the next id; fires '' when cleared. */
  onChange?: (value: string) => void;
  /** The contacts to choose from; archived ones are filtered out. */
  contacts: CustodianContact[];
  /** Field label. Default "Custodian". */
  label?: string;
  /** Mark the field Optional in text. Default true. */
  optional?: boolean;
  placeholder?: string;
  help?: string;
  /** Error message — links to the input (aria-describedby) and flips aria-invalid. */
  error?: string;
  /** Show an announced loading row while the contact list loads. */
  loading?: boolean;
  disabled?: boolean;
  className?: string;
  id?: string;
}

/**
 * The optional custodian picker for the account create dialog and inline edit
 * grid. Reuses/extends the DS `Combobox` (no inline create, no type restriction);
 * lists active contacts only; clearable + optional. Meets the picker-half
 * of the feature's WCAG 2.2 AA requirements.
 */
export declare function CustodianSelect(props: CustodianSelectProps): JSX.Element;
