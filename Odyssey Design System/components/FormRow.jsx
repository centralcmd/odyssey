/**
 * Odyssey DS — FormRow
 * A simple equal-width column grid for laying out paired form fields side by
 * side inside a dialog — the component form of the kit's `.aam-row2`. Defaults
 * to two columns with the standard 14px form gutter and top-aligned cells (so a
 * field with a helper/error line doesn't drag its neighbour down).
 *
 *   <FormRow><Field … /><Select … /></FormRow>          // two equal columns
 *   <FormRow cols={3}>…</FormRow>                        // three
 *
 * Leave a cell empty with an inline `<div />` when you want one field on its own
 * in a two-column row.
 */
export function FormRow({ cols = 2, gap = 14, align = 'start', className = '', style, children, ...rest }) {
  return (
    <div
      className={`odc-form-row${className ? ' ' + className : ''}`}
      style={{ gridTemplateColumns: `repeat(${cols}, minmax(0, 1fr))`, gap, alignItems: align, ...style }}
      {...rest}
    >
      {children}
    </div>
  );
}
