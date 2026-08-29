/**
 * Odyssey DS — CardBody
 * The padded content region inside a `flush` `Card` — the 20px inset every card
 * body shares. Pair with `CardHeader` for a titled card, or use alone for a
 * simple padded surface. Override the inset with `style={{ padding: 0 }}` for
 * edge-to-edge content (a table or media fills the card), matching the kit's
 * historical `.card-body`. Styled by .odc-card-body.
 */
export function CardBody({ className = '', style, children, ...rest }) {
  return (
    <div className={`odc-card-body${className ? ' ' + className : ''}`} style={style} {...rest}>
      {children}
    </div>
  );
}
