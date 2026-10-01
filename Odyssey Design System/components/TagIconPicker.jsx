/**
 * Odyssey DS — TagIconPicker  (+ TransactionTagIcons catalogue)
 * ---------------------------------------------------------------------------
 * The transaction-tag icon chooser. Deliberately a closed grid, not a search
 * over the Material set: a tag's icon is a key from the fixed catalogue the
 * API validates against (`TransactionTagIcons.All` in Odyssey.Dtos/Finance),
 * so the picker offers exactly that list and nothing a server would refuse.
 *
 * The first cell is always **Default** — value `null`, drawn with today's tag
 * glyph (`local_offer`) inside a dashed outline. The API never stores the
 * default key (a request selecting it sends `null`), so the picker emits
 * `null` for it and treats any unknown / stale value as Default, mirroring the
 * server's read-side projection.
 *
 * Controlled: `value` (key | null) + `onChange(key | null)`. ARIA radiogroup
 * with a roving tab stop; arrows move in two dimensions over the live column
 * count, Home/End jump, selection follows focus (the radio pattern). The
 * caption under the grid names the selected icon in words and previews the
 * label of the cell under the pointer or focus, so meaning never rests on the
 * glyph alone.
 *
 * `TransactionTagIcons.resolve(tags)` is the client copy of the server's
 * display-icon rule (§3 of the backend spec): order by name, ordinal
 * case-insensitive, ties by id; first known icon wins; else the default. The
 * server sends `displayIcon` on every transaction — render that. `resolve` is
 * for local previews only (e.g. a transaction being edited), so the two can't
 * drift.
 */

const TTI_ALL = [
  { key: 'shopping_cart', label: 'Groceries' },
  { key: 'restaurant', label: 'Dining' },
  { key: 'local_cafe', label: 'Café' },
  { key: 'directions_car', label: 'Car' },
  { key: 'local_gas_station', label: 'Fuel' },
  { key: 'directions_bus', label: 'Public transport' },
  { key: 'flight', label: 'Travel' },
  { key: 'home', label: 'Housing' },
  { key: 'bolt', label: 'Utilities' },
  { key: 'wifi', label: 'Internet' },
  { key: 'phone_iphone', label: 'Phone' },
  { key: 'medical_services', label: 'Health' },
  { key: 'fitness_center', label: 'Fitness' },
  { key: 'school', label: 'Education' },
  { key: 'child_care', label: 'Children' },
  { key: 'pets', label: 'Pets' },
  { key: 'checkroom', label: 'Clothing' },
  { key: 'movie', label: 'Entertainment' },
  { key: 'subscriptions', label: 'Subscriptions' },
  { key: 'card_giftcard', label: 'Gifts' },
  { key: 'volunteer_activism', label: 'Charity' },
  { key: 'savings', label: 'Savings' },
  { key: 'trending_up', label: 'Investments' },
  { key: 'account_balance', label: 'Bank' },
  { key: 'credit_card', label: 'Card' },
  { key: 'receipt_long', label: 'Bills' },
  { key: 'request_quote', label: 'Tax' },
  { key: 'payments', label: 'Salary' },
  { key: 'work', label: 'Work' },
  { key: 'build', label: 'Repairs' },
  { key: 'shield', label: 'Insurance' },
  { key: 'more_horiz', label: 'Other' },
];
const TTI_DEFAULT = 'local_offer';
const TTI_BY_KEY = TTI_ALL.reduce((m, x) => { m[x.key] = x; return m; }, {});

const ttiName = (t) => String((t && (t.name != null ? t.name : t.label)) || '');
const ttiCmp = (a, b) => {
  const x = ttiName(a).toUpperCase(), y = ttiName(b).toUpperCase();
  if (x < y) return -1;
  if (x > y) return 1;
  const i = String((a && (a.id || a.transactionTagId)) || ''), j = String((b && (b.id || b.transactionTagId)) || '');
  return i < j ? -1 : i > j ? 1 : 0;
};

export const TRANSACTION_TAG_ICONS = TTI_ALL;
export const TRANSACTION_TAG_ICON_DEFAULT = TTI_DEFAULT;

export const TransactionTagIcons = {
  Default: TTI_DEFAULT,
  All: TTI_ALL,
  /** Ordinal, case-sensitive membership — the default key is NOT a member. */
  isKnown: (key) => typeof key === 'string' && Object.prototype.hasOwnProperty.call(TTI_BY_KEY, key),
  /** The read-side projection: known key → key, anything else → null. */
  normalize: (key) => (TransactionTagIcons.isKnown(key) ? key : null),
  /** The glyph to draw for a tag's stored icon (null / unknown → default). */
  glyph: (key) => (TransactionTagIcons.isKnown(key) ? key : TTI_DEFAULT),
  /** Human label for a key; null → "Default". */
  labelFor: (key) => (TransactionTagIcons.isKnown(key) ? TTI_BY_KEY[key].label : 'Default'),
  /** The canonical tag order (§3 step 1) — a sorted copy. */
  order: (tags) => (tags || []).slice().sort(ttiCmp),
  /** The display-icon rule (§3). Client preview only — render the server's displayIcon. */
  resolve: (tags) => {
    const hit = TransactionTagIcons.order(tags).find((t) => TransactionTagIcons.isKnown(t && t.icon));
    return hit ? hit.icon : TTI_DEFAULT;
  },
};

export function TagIconPicker({
  value = null,
  onChange,
  icons = TTI_ALL,
  disabled = false,
  id,
  ariaLabel = 'Icon',
  ariaDescribedby,
  className = '',
}) {
  const { useState, useRef } = React;
  const autoId = React.useId();
  const groupId = id || autoId;
  const gridRef = useRef(null);
  const [peek, setPeek] = useState(null); // index under pointer / focus

  const cells = [{ key: null, label: 'Default' }, ...icons];
  const known = value != null && icons.some((x) => x.key === value);
  const selKey = known ? value : null;
  const selIndex = Math.max(0, cells.findIndex((c) => c.key === selKey));

  const pick = (c) => { if (!disabled && onChange) onChange(c.key); };

  const cols = () => {
    const g = gridRef.current;
    if (!g) return 8;
    const t = getComputedStyle(g).gridTemplateColumns;
    return Math.max(1, t ? t.split(' ').filter(Boolean).length : 8);
  };

  const onKey = (e, i) => {
    const n = cells.length;
    let next = null;
    switch (e.key) {
      case 'ArrowRight': next = Math.min(n - 1, i + 1); break;
      case 'ArrowLeft': next = Math.max(0, i - 1); break;
      case 'ArrowDown': next = Math.min(n - 1, i + cols()); break;
      case 'ArrowUp': next = Math.max(0, i - cols()); break;
      case 'Home': next = 0; break;
      case 'End': next = n - 1; break;
      case ' ': case 'Enter': e.preventDefault(); pick(cells[i]); return;
      default: return;
    }
    e.preventDefault();
    if (next === i) return;
    pick(cells[next]);
    const el = gridRef.current && gridRef.current.querySelector(`[data-idx="${next}"]`);
    if (el) el.focus();
  };

  const shown = peek != null && cells[peek] && cells[peek].key !== selKey ? cells[peek] : null;
  const sel = cells[selIndex];

  return (
    <div className={`odc-iconpick${disabled ? ' disabled' : ''} ${className}`.trim()}>
      <div className="odc-iconpick-grid" ref={gridRef} role="radiogroup" id={groupId}
        aria-label={ariaLabel} aria-describedby={ariaDescribedby}
        onMouseLeave={() => setPeek(null)}>
        {cells.map((c, i) => {
          const active = i === selIndex;
          const isDef = c.key == null;
          return (
            <button key={c.key || '__default'} type="button" role="radio" data-idx={i}
              aria-checked={active} aria-label={isDef ? 'Default (tag icon)' : c.label}
              disabled={disabled} tabIndex={i === selIndex ? 0 : -1}
              className={`odc-iconpick-cell${active ? ' selected' : ''}${isDef ? ' default' : ''}`}
              onClick={() => pick(c)} onKeyDown={(e) => onKey(e, i)}
              onMouseEnter={() => setPeek(i)} onFocus={() => setPeek(i)} onBlur={() => setPeek(null)}>
              <span className="material-icons" aria-hidden="true">{isDef ? TTI_DEFAULT : c.key}</span>
              {active ? <span className="odc-iconpick-check" aria-hidden="true"><span className="material-icons">check</span></span> : null}
            </button>
          );
        })}
      </div>
      <div className="odc-iconpick-caption" aria-hidden="true">
        <span className="odc-iconpick-sel">
          <span className="material-icons">{sel.key || TTI_DEFAULT}</span>
          <span>{sel.key ? sel.label : 'Default'}</span>
          {!sel.key ? <span className="odc-iconpick-sub">the tag icon</span> : null}
        </span>
        {shown ? (
          <span className="odc-iconpick-peek">
            <span className="material-icons">{shown.key || TTI_DEFAULT}</span>
            <span>{shown.key ? shown.label : 'Default'}</span>
          </span>
        ) : null}
      </div>
    </div>
  );
}
