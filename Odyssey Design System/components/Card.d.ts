export interface CardProps {
  /** Drop the shadow, keep the border. Preferred for forms. */
  outlined?: boolean;
  /** Remove the default 16px padding for edge-to-edge content. */
  flush?: boolean;
  className?: string;
  style?: React.CSSProperties;
  children?: React.ReactNode;
}

/** The surface primitive — elevated by default, outlined or flush by flag. */
export declare function Card(props: CardProps): JSX.Element;
