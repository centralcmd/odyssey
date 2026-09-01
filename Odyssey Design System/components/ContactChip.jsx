/**
 * Odyssey DS — ContactChip
 * The read display of a **contact** as a chip — a colored type glyph +
 * the contact name — the canonical way a linked/tagged contact reads
 * anywhere it's surfaced: the Journal's linked-contacts row, a photo's
 * tagged People (Contacts of type Person), transaction merchants, etc.
 *
 * NOTE on "People": a person tagged on a record is a Contact whose type is
 * `Person`, NOT an app User. So people are rendered with this chip (person glyph
 * in the Person category color), never a User/Avatar token.
 *
 * Composition mirrors CustodianChip: it is built from the chip visual language
 * (.odc-chip) plus the canonical ContactType registry — the type icon and
 * color come from `CONTACT_TYPES` (read off the DS namespace), never
 * re-hardcoded. The glyph is decorative (aria-hidden); meaning rides in text.
 *
 * States:
 *   • normal      — type glyph (category color) + name.
 *   • archived    — muted glyph + a visible "(archived)" cue (never color-only).
 *   • unavailable — a since-deleted / no-access link: a link_off glyph + the
 *     word "Unavailable" (a dangling id never errors the surrounding surface).
 *
 * Pass either `contact` ({ name, type, archived?, unavailable? }) or the
 * bare `name` + `type`. `size` sm / md (default). `showType` appends the type
 * label after the name (default false — the glyph already encodes the type).
 * Informational, not a link (no per-contact route in v1): a plain <span>.
 */

/* Fallback registry so the chip resolves a type when the bundle's
   CONTACT_TYPES isn't reachable (an isolated specimen). Mirrors
   components/ContactTypeSelect.jsx. */
const CONTACT_CHIP_FALLBACK = {
  Person:       { label: 'Person',       icon: 'person',          color: 'oklch(0.80 0.15 150)' },
  Organization: { label: 'Organization', icon: 'corporate_fare',  color: 'oklch(0.72 0.16 295)' },
};

export function contactTypeMeta(typeKey) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const reg = NS.CONTACT_TYPES;
  if (reg) {
    const hit = reg.find((t) => t.key === typeKey);
    if (hit) return hit;
    // Legacy value (Merchant/Company/Institution/Other) — folds to Organization (§15).
    return reg.find((t) => t.key === 'Organization') || CONTACT_CHIP_FALLBACK.Organization;
  }
  return CONTACT_CHIP_FALLBACK[typeKey] || CONTACT_CHIP_FALLBACK.Organization;
}

export function ContactChip({ contact, name, type, size = 'md', showType = false, className = '', style }) {
  const cp = contact || { name, type };
  const sz = size === 'sm' ? ' sm' : '';

  // Unavailable: a since-deleted / no-access id — stated in text, never a crash.
  if (cp && cp.unavailable) {
    return (
      <span
        className={`odc-chip entity${sz}${className ? ' ' + className : ''}`}
        title="This contact was deleted or you don't have access."
        style={style}
      >
        <span className="material-icons" aria-hidden="true">link_off</span>
        Unavailable
      </span>
    );
  }
  if (!cp || !cp.name) return null;

  const meta = contactTypeMeta(cp.type);
  const archived = !!cp.archived;
  const typeLabel = meta.label || cp.type || '';
  const a11yName = `${cp.name}${typeLabel ? ` (${typeLabel})` : ''}${archived ? ', archived' : ''}`;

  return (
    <span
      className={`odc-chip entity${archived ? ' archived' : ''}${sz}${className ? ' ' + className : ''}`}
      style={style}
    >
      <span className="odc-sr-only">{a11yName}</span>
      <span
        className="material-icons"
        style={{ color: archived ? undefined : meta.color }}
        aria-hidden="true"
      >
        {meta.icon}
      </span>
      <span aria-hidden="true">{cp.name}</span>
      {showType && typeLabel ? (
        <span className="odc-chip-sub" aria-hidden="true">{typeLabel}</span>
      ) : null}
      {archived ? (
        <span className="odc-chip-sub" aria-hidden="true">(archived)</span>
      ) : null}
    </span>
  );
}
