export interface ProfilePictureFieldProps {
  /** The image URL, already keyed by `version` (e.g. `/api/profile-images/{id}?v={version}`). */
  src?: string | null;
  /** The read DTO's image-version token. `null` = no picture: render the monogram and issue no request. It is both the presence signal and the cache key. */
  version?: string | null;
  /** Monogram shown whenever there is no picture, or an image fails to load. */
  initials?: string;
  /** Descriptive alt — the picture is the subject of its own controls here, so it is never decorative. */
  alt?: string;
  /** Monogram tone. Carry the page's own tint across rather than taking the default. */
  tone?: 'neutral' | 'tide' | 'sea' | 'violet' | 'mint' | 'coral' | { bg: string; fg: string };
  label?: string;
  /** Overrides the state sentence under the label. */
  hint?: React.ReactNode;
  /** An upload is in flight. */
  busy?: boolean;
  /** A removal is in flight — the Remove button shows its own busy state. */
  removing?: boolean;
  /** Disables both controls — the pre-claim session window. Never used for a validation failure. */
  disabled?: boolean;
  /** Why the controls are disabled, and how to clear it. Required whenever `disabled` is set. (Rendered by the `row` variant; an `overlay` caller shows it beside the mark.) */
  disabledReason?: React.ReactNode;
  /** `row` (default) = labelled control with text buttons. `overlay` = the mark IS the control, actions revealed on hover/focus with a persistent camera badge. */
  variant?: 'row' | 'overlay';
  /** Open the crop dialog. */
  onAdd?: () => void;
  /** Raise the removal. The caller owns the confirmation and the live-region announcement. */
  onRemove?: () => void;
  className?: string;
}

/** The signed-in user's own profile-picture control: `lg` preview + Add/Change + Remove. Self-scoped — it takes no target user. */
export declare function ProfilePictureField(props: ProfilePictureFieldProps): JSX.Element;
