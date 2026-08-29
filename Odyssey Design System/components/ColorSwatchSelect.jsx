/**
 * Odyssey DS — ColorSwatchSelect  (+ CALENDAR_SWATCHES palette)
 * ---------------------------------------------------------------------------
 * The calendar-colour chooser. Deliberately NOT a full colour picker: a
 * `Calendar.Color` is chosen from a small, curated, contrast-vetted palette so
 * that (a) the feature needs no MudColorPicker-class net-new component, and
 * (b) every event chip can guarantee AA-legible label text, because each
 * swatch ships a pre-computed foreground. The stored value stays a hex string
 * (`Calendar.Color`, future-proofing a larger palette) — this control just
 * constrains it to palette membership.
 *
 * The palette is re-mapped onto the Odyssey ramps (sea / tide / mint / coral /
 * violet / amber / ink) rather than the spec's generic Material hues, so a
 * calendar chip sits in the same colour world as the rest of the product.
 * Brand tide is used only in its deep stop, well clear of the bright chrome
 * primary, so a calendar colour never reads as app chrome.
 *
 * Controlled: pass `value` (hex) + `onChange(hex)`. Renders as an ARIA
 * radiogroup — arrow keys move between swatches, Space/Enter selects, a roving
 * tabindex keeps a single tab stop.
 */

export const CALENDAR_SWATCHES = [
  { key: 'blue',   name: 'Blue',   hex: '#0369A1', fg: '#FFFFFF' }, // sea-700
  { key: 'teal',   name: 'Teal',   hex: '#006B5A', fg: '#FFFFFF' }, // tide-deep
  { key: 'green',  name: 'Green',  hex: '#15803D', fg: '#FFFFFF' }, // mint-700
  { key: 'coral',  name: 'Coral',  hex: '#B23B3B', fg: '#FFFFFF' }, // coral-700
  { key: 'violet', name: 'Violet', hex: '#6D28D9', fg: '#FFFFFF' }, // violet-700
  { key: 'slate',  name: 'Slate',  hex: '#4A5670', fg: '#FFFFFF' }, // ink-500
  { key: 'amber',  name: 'Amber',  hex: '#F59E0B', fg: '#0E1525' }, // amber-500 · dark text
  { key: 'sky',    name: 'Sky',    hex: '#7DD3FC', fg: '#0E1525' }, // sea-300 · dark text
];

export const DEFAULT_CALENDAR_COLOR = '#0369A1';

const ODC_SW_BY_HEX = CALENDAR_SWATCHES.reduce((m, s) => { m[s.hex.toUpperCase()] = s; return m; }, {});

export function swatchFor(hex) {
  if (hex && ODC_SW_BY_HEX[String(hex).toUpperCase()]) return ODC_SW_BY_HEX[String(hex).toUpperCase()];
  return ODC_SW_BY_HEX[DEFAULT_CALENDAR_COLOR];
}

export function ColorSwatchSelect({
  value,
  onChange,
  swatches = CALENDAR_SWATCHES,
  disabled = false,
  id,
  ariaLabel = 'Calendar colour',
}) {
  const autoId = React.useId();
  const groupId = id || autoId;
  const selectedHex = (value && String(value).toUpperCase()) || DEFAULT_CALENDAR_COLOR.toUpperCase();
  const selIndex = Math.max(0, swatches.findIndex((s) => s.hex.toUpperCase() === selectedHex));

  const pick = (s) => { if (!disabled && onChange) onChange(s.hex); };

  const onKey = (e, i) => {
    const cols = 8;
    let next = null;
    switch (e.key) {
      case 'ArrowRight': case 'ArrowDown': next = (i + 1) % swatches.length; break;
      case 'ArrowLeft':  case 'ArrowUp':   next = (i - 1 + swatches.length) % swatches.length; break;
      case 'Home': next = 0; break;
      case 'End':  next = swatches.length - 1; break;
      case ' ': case 'Enter': e.preventDefault(); pick(swatches[i]); return;
      default: return;
    }
    void cols;
    if (next != null) {
      e.preventDefault();
      pick(swatches[next]);
      const el = e.currentTarget.parentElement && e.currentTarget.parentElement.querySelector(`[data-idx="${next}"]`);
      if (el) el.focus();
    }
  };

  return (
    <div className="odc-swatchsel" role="radiogroup" aria-label={ariaLabel} id={groupId}>
      {swatches.map((s, i) => {
        const active = s.hex.toUpperCase() === selectedHex;
        return (
          <button
            key={s.key}
            type="button"
            role="radio"
            data-idx={i}
            aria-checked={active}
            aria-label={s.name}
            title={s.name}
            disabled={disabled}
            tabIndex={i === selIndex ? 0 : -1}
            className={`odc-swatch${active ? ' selected' : ''}`}
            style={{ '--sw': s.hex, '--sw-fg': s.fg }}
            onClick={() => pick(s)}
            onKeyDown={(e) => onKey(e, i)}
          >
            {active ? <span className="material-icons odc-swatch-check" aria-hidden="true">check</span> : null}
          </button>
        );
      })}
    </div>
  );
}
