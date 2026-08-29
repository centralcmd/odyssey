export interface CardBodyProps {
  className?: string;
  style?: React.CSSProperties;
  children?: React.ReactNode;
}

/**
 * The padded content region inside a `flush` `Card` (20px inset). Pass
 * `style={{ padding: 0 }}` for edge-to-edge content like a table.
 */
export declare function CardBody(props: CardBodyProps): JSX.Element;
