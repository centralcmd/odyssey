/**
 * Odyssey DS — RecordBody
 * The expanded-record body, extracted from RecordCard so a TABLE-based record
 * list can render the identical detail surface. A RecordCard's expanded body
 * and a RecordTable's expanded detail row are the same thing and must look the
 * same: one padded column of slots, `alert` → `details` → `content` →
 * `children`, in that fixed order.
 *
 * RecordCard composes this for its own body; a table detail panel renders it
 * directly with `inTable` (drops the card's top divider for the table's own
 * accent rule and sits on the surface colour inside the zero-padding cell).
 *
 * `accent` / `accentSoft` set --rec / --rec-soft, which the InfoTileGrid inside
 * `details` reads for its tile icon chips — inside a RecordCard they are
 * already set on the card, so pass them only when rendering standalone.
 * Styled by .odc-record-body in components.css.
 */
export function RecordBody({
  alert,
  details,
  content,
  accent,
  accentSoft,
  inTable = false,
  id,
  className = '',
  style,
  children,
}) {
  const cls = ['odc-record-body', inTable ? 'in-table' : '', className].filter(Boolean).join(' ');
  const st = Object.assign({}, style);
  if (accent) st['--rec'] = accent;
  if (accentSoft) st['--rec-soft'] = accentSoft;
  return (
    <div className={cls} id={id} style={Object.keys(st).length ? st : undefined}>
      {alert}
      {details}
      {content}
      {children}
    </div>
  );
}
