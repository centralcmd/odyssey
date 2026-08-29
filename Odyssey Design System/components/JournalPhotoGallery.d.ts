import * as React from 'react';

export interface JournalPhoto {
  /** The JournalEntryPhoto link-row id. */
  id: string;
  /** Library Photo id this entry links to (v4 — the entry links a Photo, not a raw file). */
  photoId?: string;
  /** Underlying Files-store image id — the read enriches each link with the
   *  library Photo's FileId so the tile can build the `content` URL. Always
   *  non-null on a returned link (links whose PhotoId no longer resolves are
   *  dropped server-side, never returned with an empty FileId). */
  fileId?: string;
  /** Photo title (or filename) — used as the tile's accessible name and caption. */
  name?: string;
  /** Full-res image URL (built from `fileId`). Omit for a striped placeholder tile (mock / pre-load). */
  src?: string;
}

export interface JournalPhotoGalleryProps {
  /** The entry's photos, in display (Position) order. */
  photos?: JournalPhoto[];
  /** Called with the photo when its tile is activated (opens full-res). */
  onOpen?: (photo: JournalPhoto) => void;
  /** Section heading; pass '' to render the grid with no heading. Default 'Photos'. */
  title?: string;
  /** Minimum tile width in px (grid auto-fills). Default 120. */
  minTile?: number;
  /** Text shown when there are no photos. */
  emptyText?: string;
  className?: string;
  style?: React.CSSProperties;
}

/**
 * A responsive thumbnail grid over a journal entry's photos — keyboard-focusable
 * tiles that open the full-res file, count announced as text, `loading="lazy"`
 * images, striped placeholder when no `src`. No EXIF/lightbox in v1.
 */
export declare function JournalPhotoGallery(props: JournalPhotoGalleryProps): JSX.Element;
