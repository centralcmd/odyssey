export interface RecordBodyProps {
  /** Alert slot — renders first, above everything else. */
  alert?: React.ReactNode;
  /** The record's field set — an `InfoTileGrid` of `InfoTile`s. */
  details?: React.ReactNode;
  /** Prose / chart / summary content below the field set. */
  content?: React.ReactNode;
  /** Record accent (sets `--rec`) — the hue the detail tiles' icon chips inherit. */
  accent?: string;
  /** Soft form of the accent (sets `--rec-soft`). */
  accentSoft?: string;
  /** Rendering inside a `RecordTable` detail cell rather than a `RecordCard`. */
  inTable?: boolean;
  id?: string;
  className?: string;
  style?: React.CSSProperties;
  /** Sections — rendered last, after `content`. */
  children?: React.ReactNode;
}

/**
 * The expanded-record body: one padded column of slots in the fixed order
 * `alert` → `details` → `content` → `children`. Shared by `RecordCard` (which
 * composes it for its own body) and table-based record lists, so a card's
 * expanded body and a table's expanded detail row are the same surface.
 */
export declare function RecordBody(props: RecordBodyProps): JSX.Element;
