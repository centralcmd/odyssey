export interface TooltipProps {
  /** The tooltip text. Keep it short — one line. */
  label: string;
  /** The trigger element the tooltip describes. */
  children?: React.ReactNode;
}

/** A hover/focus label on an inverted bubble. */
export declare function Tooltip(props: TooltipProps): JSX.Element;
