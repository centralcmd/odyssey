export interface ModalProps {
  /** Controls visibility. Default true. */
  open?: boolean;
  title?: React.ReactNode;
  subtitle?: React.ReactNode;
  /** Optional lead-tile glyph left of the title — a Material Icons ligature, or
   *  any non-ligature character (e.g. "§") rendered as a typographic glyph. */
  icon?: string;
  /** Lead-tile tint. 'warning'/'error' for destructive or confirm dialogs. Default 'brand'. */
  iconTone?: 'brand' | 'warning' | 'error';
  /** Called on Esc, scrim click, and the close button. Omit to hide the close button. */
  onClose?: () => void;
  /** Right-aligned footer actions (typically Buttons). */
  footer?: React.ReactNode;
  /** Wide variant — 1240px / 96vw (batch grids, file analysis). */
  wide?: boolean;
  /** Accessible name used only when there is no `title` (otherwise the dialog is labelled by the title). */
  ariaLabel?: string;
  /** Extra class on the dialog surface — for per-dialog width/layout variants (e.g. 'atm-dialog'). */
  className?: string;
  /** Extra class on the scrollable body (e.g. 'fan-body'). */
  bodyClassName?: string;
  children?: React.ReactNode;
}

/** The dialog shell — scrim, header, scrollable body, footer. Portals to <body>. */
export declare function Modal(props: ModalProps): JSX.Element;
