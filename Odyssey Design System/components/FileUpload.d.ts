export interface UploadFile {
  /** Stable unique id for the row (auto-generated for browser-picked files). */
  uid: string;
  /** Editable display name. */
  name: string;
  /** File-kind key — matches a FileKind.key in the registry. */
  kind: string;
  /** Size in bytes; rendered human-readable. null/undefined shows "—". */
  sizeBytes?: number | null;
}

export interface FileKind {
  /** Stable key stored on each file's `kind`. */
  key: string;
  /** Visible label in the picker. */
  label: string;
  /** Material Icons ligature name. */
  icon: string;
  /** Icon foreground color (any CSS color). */
  color: string;
  /** Icon background tint (any CSS color). */
  soft: string;
}

export interface FileUploadProps {
  /** Controlled file list. Omit to run uncontrolled from `defaultFiles`. */
  files?: UploadFile[];
  /** Initial files when uncontrolled. */
  defaultFiles?: UploadFile[];
  /** Fires with the full next array on every add / rename / retype / remove. */
  onChange?: (files: UploadFile[]) => void;
  /** Native input `accept` filter (e.g. "image/*,.pdf"). */
  accept?: string;
  /** Allow selecting/dropping more than one file. Default true. When false the
   *  field is genuinely single-file: a new pick/drop REPLACES the current file
   *  instead of accumulating. */
  multiple?: boolean;
  /**
   * The effective per-file size cap in megabytes, used to COMPOSE the size
   * clause of the default hint. Where a surface has its own tighter product
   * limit, pass `Math.min(surfaceConstant, serverCap)` — never the constant
   * alone: a surface may tighten the global cap, but must not override a
   * lowered one.
   */
  maxMegabytes?: number;
  /**
   * Secondary line under the dropzone label. Composed from `maxMegabytes` when
   * omitted. Do NOT write a size limit into it as a literal — the cap is a
   * runtime setting, so a typed number goes stale silently and the field ends up
   * claiming a different limit from the one the server enforces.
   */
  hint?: string;
  /** Error message — flips the dropzone to its error state and shows below it. */
  error?: string;
  /** Horizontal, lower-profile dropzone for tight modals. */
  compact?: boolean;
  /** Show the per-row file-kind icon + inline kind picker. Default true. */
  showKinds?: boolean;
  /** Override the file-kind registry (label + icon + colors). */
  kinds?: FileKind[];
  /** Cap the ready-file list height before it scrolls (default 246px). */
  maxHeight?: number | string;
  /** Override the extension→kind guess with a domain vocabulary (name → kind key). */
  guessKind?: (name: string) => string;
  /**
   * Render an extra editor beneath each file row (e.g. validity dates, notes).
   * `patch(partial)` merges the given fields into that file and fires `onChange`.
   */
  renderFileExtra?: (file: UploadFile, patch: (partial: Partial<UploadFile> & Record<string, unknown>) => void) => JSX.Element | null;
  id?: string;
}

/**
 * Drag-and-drop upload field: a click-or-drop dropzone plus a ready-file list
 * with per-row rename, file-kind picker, and remove.
 */
export declare function FileUpload(props: FileUploadProps): JSX.Element;

export declare namespace FileUpload {
  /** Default file-kind registry (Statement / Document / Receipt / Tax). */
  const KINDS: FileKind[];
  /** Format a byte count as "B / KB / MB". */
  function fmtSize(bytes?: number | null): string;
  /** Guess a kind key from a filename extension. */
  function guessKind(name: string): string;
  /** Turn a browser FileList into UploadFile rows. */
  function filesFromList(fileList: FileList | File[] | null): UploadFile[];
  /** The rejection message for a file over the effective cap — composed from the same number as the hint. */
  function overMaxError(name: string, bytes: number, maxMegabytes: number): string;
}
