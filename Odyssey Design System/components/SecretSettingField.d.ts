import * as React from 'react';

export interface SecretSettingFieldProps {
  /** The credential's label — rendered on the field's outline. */
  label: React.ReactNode;
  /** The settings key (e.g. `Email:Password`), announced to screen readers and used in copy. */
  secretKey?: string;
  /**
   * `credential` — re-issuable at its provider. `derivation` — a key whose loss
   * makes everything already derived with it permanently un-re-derivable;
   * marked in the outline and given the harder confirmation copy.
   */
  kind?: 'credential' | 'derivation';
  /**
   * Which of the store's three results this row is showing. `unreadable` must
   * never be presented as `not-set`: an absent row is a healthy configuration,
   * an undecryptable one is a live fault.
   */
  state?: 'found' | 'not-set' | 'unreadable';
  /** What the credential is for. Always visible, first on the helper line. */
  help?: React.ReactNode;
  /** Provenance: who last set the value and when. Dimmer, at the end of the helper line. */
  meta?: React.ReactNode;
  /** Blocking message above the helper line. Overrides the default `unreadable` copy. */
  error?: React.ReactNode;
  /**
   * What is not working while the value is `not-set`, in the amber advisory
   * band. The post-upgrade gap is otherwise invisible on the page — a release
   * note is not a signal an administrator receives at the moment it matters.
   * Suppressed when `unreadable`, where the error carries the consequence.
   */
  consequence?: React.ReactNode;
  /** One clause naming the affected feature, appended to the default `unreadable` message (e.g. "Transactional mail is not sending."). */
  affects?: string;
  /** Caller lacks the claim: actions and entry are withdrawn, and `lockNote` is appended to the helper line. */
  locked?: boolean;
  /** Why the row is locked. Default: "Requires the security settings claim." */
  lockNote?: React.ReactNode;
  /**
   * Skip the client-side printable-ASCII check for a descriptor that has taken
   * the relaxation. The store's own rule still applies; only this row's
   * pre-flight message is dropped.
   */
  allowNonAscii?: boolean;
  /** Bullets in the `found` mask. Fixed by design — the real length is a disclosure. Default 16. */
  maskLength?: number;
  placeholder?: string;
  /** Label on the commit button. Default "Save". */
  saveLabel?: string;
  /** Save in flight. */
  busy?: boolean;
  id?: string;
  className?: string;
  /** Fires with the entered plaintext. The row clears its own input afterwards. */
  onSave?: (value: string) => void;
  /** Fires on Clear — open `SecretClearDialog` from here; the row does not confirm on its own. */
  onClear?: () => void;
}

/**
 * One encrypted, write-only credential in the `SettingField` shape. Renders the
 * store's three states — a fixed-length dot mask for `found`, an inline entry
 * input for `not-set`, a coral "Cannot be decrypted" for `unreadable` — with
 * an as-you-type printable-ASCII check and a reveal toggle on entry. Replacing
 * a stored value takes an explicit Replace first.
 */
export declare function SecretSettingField(props: SecretSettingFieldProps): JSX.Element;
