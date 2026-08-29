import * as React from 'react';
import { ActionMenuItem } from './ActionMenu';
import { AccountFileType } from './AccountFileTypeSelect';

/** One file row — plain data, no store lookups. */
export interface FilesTableRow {
  /** Stable id — row identity, Copy-ID target. */
  id: string;
  /** File display name, e.g. "statement-2026-04.pdf". */
  name: string;
  /** File-kind key (Statement / Receipt / Document / …) — fed to `typeFor`, shown in the Type chip. */
  kind: string;
  /** Preformatted size string ("1.2 MB") — or raw value if you pass `formatSize`. */
  size: string | number;
  /** ISO upload date (YYYY-MM-DD). */
  uploaded: string;
  /** When the document takes effect (ISO date) — e.g. an insurance policy start. Optional. */
  validFrom?: string | null;
  /** When the document expires (ISO date) — e.g. policy end / warranty expiry. Optional. */
  validTo?: string | null;
  /** When the document was issued / signed (ISO date). Optional. */
  issuedAt?: string | null;
  /** Issuing institution — a Contact id (resolved to a name via `issuerFor`). Optional. */
  issuedBy?: string | null;
  /**
   * Optional additive status indicator rendered next to the file name as an
   * OdsChip — e.g. a "Review pending · 12" hint that the file has an open,
   * resumable analysis review. Meaning is carried in `text` (never icon/colour
   * alone); any `icon` is decorative (aria-hidden). Provide `ariaLabel` for the
   * full accessible name (file + count). Absent on rows without a badge, so the
   * table renders exactly as before. Default tone: `pending`.
   */
  statusBadge?: {
    /** Visible chip text, e.g. "Review pending · 12". */
    text: string;
    /** Chip tone — defaults to `pending` (amber). */
    tone?: 'income' | 'expense' | 'pending' | 'info' | 'tag' | 'warning' | 'error' | 'outline' | 'default';
    /** Optional decorative leading Material icon; when omitted a status dot shows. */
    icon?: string;
    /** Full accessible name (file + count) for screen readers. */
    ariaLabel?: string;
  };
}

/** File-kind visuals — the registry shape of `OdysseyData.fileTypeByKey` / ACCOUNT_FILE_TYPES. */
export interface FileKindMeta {
  /** Material icon name. */
  icon: string;
  /** Foreground color (icon + chip text). */
  color: string;
  /** Soft tint background (avatar + chip). */
  soft: string;
}

export interface FilesTableProps {
  /** Rows to render (already filtered by the parent). No pagination — the list renders whole. */
  files: FilesTableRow[];
  /** Resolve a row's kind visuals. Unknown kinds fall back to a neutral document glyph. */
  typeFor?: (file: FilesTableRow) => FileKindMeta | undefined;
  /**
   * File-specific menu items — Preview (opens the viewer dialog) / Download /
   * Analyze / Copy ID — slotted between the built-in Edit and Delete items per
   * the menu convention. "Preview" shows the document; "View details" (built
   * in) expands the record. Host any modals these open OUTSIDE the table.
   * Default: Copy ID.
   */
  actions?: (file: FilesTableRow) => ActionMenuItem[];
  /**
   * Persist an inline edit. The patch is `{ name, kind }` on read-only-metadata
   * surfaces; when `issuers` is supplied it also carries the validity fields
   * `{ validFrom, validTo, issuedAt, issuedBy }` (ISO dates / contact id,
   * each nullable). Enables the Edit menu item + the inline edit panel
   * (RecordTable lifecycle: Save flashes "Saved", Cancel returns to the detail
   * view). Omit for a read-only surface.
   */
  onSave?: (id: string, patch: { name: string; kind: string; validFrom?: string | null; validTo?: string | null; issuedAt?: string | null; issuedBy?: string | null }) => void;
  /** File-kind vocabulary for the edit panel's Document type picker. Default: the canonical ACCOUNT_FILE_TYPES registry. */
  kinds?: AccountFileType[];
  /** Resolve a row's `issuedBy` id to a display name, shown in the detail well. */
  issuerFor?: (file: FilesTableRow) => string | null | undefined;
  /**
   * Contact options (`{ value, label }`) for the edit panel's "Issued by"
   * picker. Supplying this array also reveals the validity-date editors
   * (Valid from / Valid to / Issued). Omit on surfaces that don't track
   * document validity (e.g. transaction attachments).
   */
  issuers?: { value: string; label: string }[];
  /** Detach/delete a file — appends the danger Delete item after a divider. */
  onDelete?: (file: FilesTableRow) => void;
  /** Uploaded cell renderer. Default: "Apr 12, 2026". */
  formatDate?: (iso: string) => React.ReactNode;
  /** Size cell renderer. Default: `file.size` as-is. */
  formatSize?: (file: FilesTableRow) => React.ReactNode;
  /** Initial sort. Default { key:'uploaded', dir:'desc' }. */
  defaultSort?: { key: 'name' | 'kind' | 'size' | 'uploaded'; dir: 'asc' | 'desc' };
  /** Centered content shown when `files` is empty. */
  empty?: React.ReactNode;
  /** Extra class on the table. */
  className?: string;
}

/**
 * The files surface — a preset of RecordTable shared by Accounts, Transactions
 * & the Files page. Inherits the record-row lifecycle: sortable headers,
 * click-to-expand MetaTile detail (File name · Document type · Size ·
 * Uploaded), inline Edit panel, Saved flash, and the conventional overflow
 * menu (View details · Edit · Preview/Download/Analyze/Copy ID · — · Delete).
 */
export declare function FilesTable(props: FilesTableProps): JSX.Element;
