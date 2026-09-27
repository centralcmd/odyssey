import * as React from 'react';

export interface HomeownerAssociationContact {
  id?: string;
  contactId?: string;
  name: string;
  /** ContactType key. Only `Organization` is selectable. */
  type?: string;
  archived?: string | null;
}

/** The slim, response-only projection on `ExistingProperty.homeownerAssociation`. */
export interface PropertyHomeownerAssociation {
  contactId: string;
  /** Resolved display name, at most 256 characters. */
  name: string;
  /** Archived timestamp, or null when active. */
  archived: string | null;
}

export type HomeownerAssociationState =
  | 'none' | 'ok' | 'missing' | 'archived' | 'not-organization' | 'legacy-archived' | 'legacy-type';

/** Classify the picked contact against the server rules R2–R4 and the §8.3 change-only rule. */
export declare function homeownerAssociationState(
  contact: HomeownerAssociationContact | null | undefined,
  value: string | null | undefined,
  storedValue: string | null | undefined,
): HomeownerAssociationState;

export interface HomeownerAssociationSelectProps {
  /** Selected contact id, or '' / null for no association. */
  value?: string | null;
  /** Fires the next id; '' when cleared. */
  onChange?: (value: string) => void;
  /** Contacts to choose from; only active Organizations are offered. */
  contacts: HomeownerAssociationContact[];
  /** The id the property stores today. A kept legacy link (archived or re-typed) is shown with a notice, not refused. */
  storedValue?: string | null;
  /** Default "Homeowner association". */
  label?: string;
  placeholder?: string;
  help?: string;
  /** Server message keyed RealEstateDetails.HomeownerAssociationId (400 / 422). */
  error?: string;
  loading?: boolean;
  disabled?: boolean;
  /** Inline create; offers one Organization row. Gate on contacts.create. */
  onCreate?: (name: string, kind: string) => string | { value: string; label: string; icon?: string; iconColor?: string } | undefined | null;
  className?: string;
  id?: string;
}

/**
 * Optional, clearable picker linking a real-estate property to its homeowner
 * association (an Organization contact). Reuses the DS Combobox.
 */
export declare function HomeownerAssociationSelect(props: HomeownerAssociationSelectProps): JSX.Element;
