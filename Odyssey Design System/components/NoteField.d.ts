export interface NoteFieldProps {
  /** Visible label, rendered on the left of the head row and tied to the textarea via htmlFor. */
  label?: string;
  /** Controlled string value. */
  value?: string;
  /** Fires with the next string value first, the native event second. */
  onChange?: (value: string, event: React.ChangeEvent<HTMLTextAreaElement>) => void;
  placeholder?: string;
  /** Max characters — also enables the `len/max` counter (native maxLength is set too). */
  maxLength?: number;
  /** Visible rows; the textarea stays vertically resizable. Default 3. */
  rows?: number;
  /** Show the character counter when `maxLength` is set. Default true. */
  showCount?: boolean;
  /** Helper text shown below the textarea. */
  help?: string;
  /** Error message — flips the field to its error state and replaces the helper. */
  error?: string;
  /** Adds a `*` after the label (the canonical required marker). */
  required?: boolean;
  /** Adds a muted "Optional" hint after the label. Mutually exclusive with `required`. */
  optional?: boolean;
  disabled?: boolean;
  autoFocus?: boolean;
  className?: string;
  /** Explicit id; auto-generated (React.useId) if omitted. */
  id?: string;
}

/** A labelled multi-line text input with a live character counter — for notes, descriptions and comments. */
export declare function NoteField(props: NoteFieldProps): JSX.Element;
