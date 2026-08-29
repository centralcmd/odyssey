/**
 * Odyssey DS — MetaTile
 * One labelled field in a detail grid — the read-only key/value wells that fill
 * an expanded record's panel (a `.meta-grid` of these sits inside RecordTable's
 * `renderDetail`). Maps to a MudField in read-only display mode.
 *
 * Tiles always sit two per row — there is no spanning variant. Long values
 * (descriptions, GUIDs, file names) wrap to multiple lines inside their tile;
 * the value style owns the multiline behavior (`overflow-wrap: anywhere`).
 *
 * `mono` sets the value in Roboto Mono for IDs / codes / timestamps.
 * `value` is any node — drop a <Chip> in for a status.
 */
export function MetaTile({ label, value, mono, valueClass = '' }) {
  return (
    <div className="meta-tile">
      <div className="meta-tile-label">{label}</div>
      <div className={`meta-tile-value ${mono ? 'mono' : ''} ${valueClass}`.trim()}>{value}</div>
    </div>
  );
}
