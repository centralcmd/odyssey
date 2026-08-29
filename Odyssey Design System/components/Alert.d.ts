export interface AlertProps {
  /** Sets the color and leading icon. */
  severity?: 'info' | 'success' | 'warning' | 'error';
  children?: React.ReactNode;
}

/** A block-level notification banner. */
export declare function Alert(props: AlertProps): JSX.Element;
