export interface ImageCropLimits {
  /** Largest file the browser will open to crop — never the stored cap. */
  maxSourceBytes: number;
  /** Largest source the browser will decode, per side. */
  maxSourceDimension: number;
  /** The square the crop is re-encoded to (512). */
  outputDimension: number;
  /** JPEG quality for a `cover` crop (0.85). */
  jpegQuality: number;
  /** The MIME allow-list — still images only, no GIF and no SVG. */
  types: string[];
  /** Human list of the allowed types, for copy ("PNG, JPEG or WebP"). */
  typeLabel: string;
}

/** The result a caller's upload delegate must resolve. `message` is the
 *  server's own ProblemDetails text and is rendered verbatim. */
export interface ImageCropUploadResult {
  ok: boolean;
  message?: string;
  /** The new image version token, where the surface has one. */
  version?: string;
}

export interface ImageCropDialogProps {
  open?: boolean;
  /** Dialog title. Defaults to `Crop {subject}`. */
  title?: string;
  /** What is being cropped, used throughout the copy — "picture" / "logo". */
  subject?: string;
  /** `cover` square-crops a photograph into a circle; `contain` letterboxes a logo on a neutral ground, never cutting it. Chosen from what the image IS. */
  fit?: 'cover' | 'contain';
  /** Material ligature shown in the empty frame before a file is chosen. */
  emptyIcon?: string;
  /** Source caps + allow-list. Defaults to `IMAGE_CROP_LIMITS`. */
  limits?: ImageCropLimits;
  /** The surface's own stored cap in whole megabytes — from the feature's limits class, never a literal at the call site. */
  surfaceMegabytes?: number;
  /** The live instance-wide upload cap in bytes. The dialog resolves `min(this, surfaceMegabytes)` — never `max`. */
  globalCapBytes?: number;
  /** An existing image to open the stage on (a replace). */
  currentSrc?: string | null;
  /** Overrides the metadata-strip footnote. */
  note?: React.ReactNode;
  /** The upload delegate. Receives the re-encoded data URL; resolve `{ ok: false, message }` to render the server's message in the alert region without closing the dialog. */
  onUpload?: (dataUrl: string) => Promise<ImageCropUploadResult> | ImageCropUploadResult;
  onCancel?: () => void;
  /** Fires after a successful upload, with the encoded bytes and the new version token where the delegate returned one. */
  onSaved?: (dataUrl: string, version?: string) => void;
}

/** Source + stored caps shared by every crop surface. */
export declare const IMAGE_CROP_LIMITS: ImageCropLimits;

/** The product's one client-side crop + downscale dialog — a contact's image and the signed-in user's profile picture both mount this. */
export declare function ImageCropDialog(props: ImageCropDialogProps): JSX.Element;
