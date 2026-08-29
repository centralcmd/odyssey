/**
 * Odyssey DS — CustodianChip
 * The read-only display of an account's **custodian** — the contact that
 * holds the account (the bank for a bank account, the broker for a brokerage,
 * the provider for a pension). An account links to at most one custodian via a
 * nullable FK to a Contact; this chip is how that link reads on the
 * account card (collapsed row + expanded detail).
 *
 * Composition (per spec FE-4): it is built from the chip visual language plus
 * the canonical ContactType registry — the type icon and color come from
 * `CONTACT_TYPES` (read off the DS namespace), never re-hardcoded. The
 * icon is decorative (`aria-hidden`); all meaning rides in text.
 *
 * It is **informational, not a link** in v1 — there is no per-contact
 * detail route to navigate to — so the chip is a plain `<span>`, not focusable
 * or interactive.
 *
 * Accessibility (the chip half of the feature's WCAG 2.2 AA contract):
 *   • A11Y-5 — the contact **type** is in text (visible label + an
 *     sr-only "Custodian: <name> (<type>)" accessible name), icon decorative.
 *   • A11Y-6 — the **archived** state carries a visible "(archived)" cue in
 *     addition to the muted tone, never color alone.
 *   • A11Y-11 — the **no-custodian** state says "No custodian" in text.
 *
 * `custodian` is the slim Custodian projection — { name, type, archived?, … } —
 * or null/undefined for an account with no custodian. `size` sm (row) / md
 * (detail, default). `showType={false}` drops the visible type label (keeps the
 * sr-only one) for a space-constrained collapsed row. Styled by .odc-custodian.
 */

/* Fallback registry so the chip still resolves a type when the bundle's
   CONTACT_TYPES isn't reachable (e.g. an isolated specimen). Mirrors
   components/ContactTypeSelect.jsx. */
const CUSTODIAN_TYPE_FALLBACK = {
  Merchant:     { label: 'Merchant',     icon: 'storefront',      color: 'oklch(0.79 0.115 188)' },
  Person:       { label: 'Person',       icon: 'person',          color: 'oklch(0.80 0.15 150)' },
  Organization: { label: 'Organization', icon: 'corporate_fare',  color: 'oklch(0.72 0.16 295)' },
  Company:      { label: 'Company',      icon: 'business',        color: 'oklch(0.76 0.13 225)' },
  Institution:  { label: 'Institution',  icon: 'account_balance', color: 'oklch(0.75 0.16 330)' },
  Other:        { label: 'Other',        icon: 'category',        color: 'oklch(0.74 0.02 250)' },
};

export function custodianTypeMeta(typeKey) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const reg = NS.CONTACT_TYPES;
  if (reg) {
    const hit = reg.find((t) => t.key === typeKey);
    if (hit) return hit;
    return reg.find((t) => t.key === 'Other') || CUSTODIAN_TYPE_FALLBACK.Other;
  }
  return CUSTODIAN_TYPE_FALLBACK[typeKey] || CUSTODIAN_TYPE_FALLBACK.Other;
}

export function CustodianChip({ custodian, size = 'md', showType = true, className = '', style }) {
  const sz = size === 'sm' ? ' sm' : '';

  // No-custodian state — A11Y-11: the absence is stated in text.
  if (!custodian) {
    return (
      <span className={`odc-custodian empty${sz}${className ? ' ' + className : ''}`} style={style}>
        <span className="material-icons odc-custodian-ic" aria-hidden="true">account_balance</span>
        <span className="odc-custodian-name">No custodian</span>
      </span>
    );
  }

  const meta = custodianTypeMeta(custodian.type);
  const archived = !!custodian.archived;
  const typeLabel = meta.label || custodian.type || '';

  // The full accessible name, spoken once via sr-only text; the icon and the
  // visible pieces are aria-hidden / decorative so it isn't read twice.
  const a11yName = `Custodian: ${custodian.name}${typeLabel ? ` (${typeLabel})` : ''}${archived ? ', archived' : ''}`;

  return (
    <span
      className={`odc-custodian${archived ? ' archived' : ''}${sz}${className ? ' ' + className : ''}`}
      style={style}
    >
      <span className="odc-custodian-sr">{a11yName}</span>
      <span
        className="material-icons odc-custodian-ic"
        style={{ color: archived ? undefined : meta.color }}
        aria-hidden="true"
      >
        {meta.icon}
      </span>
      <span className="odc-custodian-name" aria-hidden="true">{custodian.name}</span>
      {showType && typeLabel ? (
        <span className="odc-custodian-type" aria-hidden="true">{typeLabel}</span>
      ) : null}
      {archived ? (
        <span className="odc-custodian-archived" aria-hidden="true">(archived)</span>
      ) : null}
    </span>
  );
}
