/**
 * Odyssey DS — Card
 * The surface primitive. `outlined` drops the drop-shadow and relies on the
 * border (use for forms, per the system); the default elevated style suits
 * stat tiles. `flush` removes the default 16px padding for edge-to-edge
 * content (tables, media). Forwards its ref to the underlying element.
 * Styled by .odc-card.
 */
export const Card = React.forwardRef(function Card({ outlined = false, flush = false, className = '', style, children, ...rest }, ref) {
  const cls = `odc-card${outlined ? ' outlined' : ''}${flush ? ' flush' : ''}${className ? ' ' + className : ''}`;
  return (
    <div ref={ref} className={cls} style={style} {...rest}>
      {children}
    </div>
  );
});
