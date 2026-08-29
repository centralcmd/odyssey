/**
 * Odyssey DS — CoordinateField
 * ---------------------------------------------------------------------------
 * The paired latitude / longitude entry. A geographic coordinate is always two
 * numbers with fixed, different valid ranges (lat −90…90, lng −180…180), so it
 * was being hand-assembled as two loose `NumberField`s in a `FormRow` wherever
 * a place could be pinned (the Photo Library edit dialog). This wraps that pair
 * as one control: it lays the two fields out side by side, enforces each range,
 * and surfaces an inline out-of-range error per field — so every "add coords"
 * spot reads and validates identically instead of re-deriving the ranges.
 *
 * Value is a `{ lat, lng }` pair (each a number, or null when empty). Emits the
 * next pair through `onChange({ lat, lng })`. Accepts string or number in —
 * strings are parsed — so it drops into existing string-backed form state.
 *
 *   <CoordinateField value={{ lat, lng }} onChange={setCoords} optional />
 */

const odcCoordNum = (v) => {
  if (v == null || v === '') return null;
  const n = typeof v === 'number' ? v : parseFloat(v);
  return Number.isNaN(n) ? null : n;
};

export function CoordinateField({
  value,
  onChange,
  latLabel = 'Latitude',
  lngLabel = 'Longitude',
  help,
  error,
  required = false,
  optional = false,
  disabled = false,
  className = '',
}) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const NumberField = NS.NumberField;
  const FormRow = NS.FormRow;

  const lat = value ? value.lat : null;
  const lng = value ? value.lng : null;
  const latN = odcCoordNum(lat);
  const lngN = odcCoordNum(lng);

  const emit = (next) => { if (onChange) onChange(next); };

  const latErr = latN != null && (latN < -90 || latN > 90) ? 'Must be between −90 and 90' : undefined;
  const lngErr = lngN != null && (lngN < -180 || lngN > 180) ? 'Must be between −180 and 180' : undefined;

  const fields = [
    NumberField ? (
      <NumberField key="lat" label={latLabel} value={lat === '' ? null : lat}
        onChange={(v) => emit({ lat: v, lng })} placeholder="−90 … 90"
        min={-90} max={90} step="any" required={required} optional={optional}
        disabled={disabled} error={latErr} />
    ) : null,
    NumberField ? (
      <NumberField key="lng" label={lngLabel} value={lng === '' ? null : lng}
        onChange={(v) => emit({ lat, lng: v })} placeholder="−180 … 180"
        min={-180} max={180} step="any" required={required} optional={optional}
        disabled={disabled} error={lngErr} />
    ) : null,
  ];

  const msg = error || (!latErr && !lngErr ? help : undefined);

  return (
    <div className={`odc-coordfield${className ? ' ' + className : ''}`}>
      {FormRow ? <FormRow cols={2}>{fields}</FormRow> : <div className="odc-form-row" style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 'var(--space-3, 16px)' }}>{fields}</div>}
      {msg ? <div className="odc-field-help" style={{ marginTop: '6px' }}>{msg}</div> : null}
    </div>
  );
}
