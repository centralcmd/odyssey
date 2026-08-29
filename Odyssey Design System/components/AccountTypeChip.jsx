/**
 * Odyssey DS — AccountTypeChip
 * The read display of an account's **type** as a chip — a colored type glyph +
 * label, the sibling of `CustodianChip` so the account detail's metadata grid
 * reads consistently (type and custodian both render as chips, not bare text).
 *
 * It draws its glyph + color + label from the canonical `ACCOUNT_TYPES`
 * registry (read off the DS namespace), the same source the account-type
 * picker and the row avatar use — so a type reads identically everywhere. The
 * glyph is decorative (`aria-hidden`); the label carries the meaning.
 *
 * Pass either `type` (the AccountType enum key, e.g. 'CheckingAccount') or a
 * resolved `accountType` registry object. `size` sm / md (default).
 * `showGroup={false}` drops the trailing Asset / Liability segment. Styled by
 * .odc-typechip — the same chip shell as `.odc-custodian`, with the glyph in
 * the type's categorical color and the group as a muted trailing label (the
 * sibling of the custodian chip's type segment).
 */

/* Minimal fallback so the chip resolves even when the bundle's ACCOUNT_TYPES
   isn't reachable (an isolated specimen). */
const ACCOUNT_TYPE_CHIP_FALLBACK = {
  icon: 'account_balance_wallet', color: 'var(--ink-200)', label: 'Account',
};

export function accountTypeMeta(typeKey) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const reg = NS.ACCOUNT_TYPES;
  if (reg) {
    const hit = reg.find((t) => t.key === typeKey);
    if (hit) return hit;
  }
  return null;
}

export function AccountTypeChip({ type, accountType, size = 'md', showGroup = true, className = '', style }) {
  const meta = accountType || accountTypeMeta(type) || (type ? { ...ACCOUNT_TYPE_CHIP_FALLBACK, label: type } : null);
  if (!meta) return null;
  const sz = size === 'sm' ? ' sm' : '';
  const groupLabel = meta.group === 'asset' ? 'Asset' : meta.group === 'liability' ? 'Liability' : null;

  return (
    <span className={`odc-typechip${sz}${className ? ' ' + className : ''}`} style={style}>
      <span
        className="material-icons odc-typechip-ic"
        style={{ color: meta.color }}
        aria-hidden="true"
      >
        {meta.icon}
      </span>
      <span className="odc-typechip-name">{meta.label}</span>
      {showGroup && groupLabel ? (
        <span className="odc-typechip-group">{groupLabel}</span>
      ) : null}
    </span>
  );
}
