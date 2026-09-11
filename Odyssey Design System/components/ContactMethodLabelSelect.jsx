/**
 * Odyssey DS — ContactMethodLabelSelect
 * The Label picker on a contact method (address / email / phone). Unlike every
 * other registry picker, its option list is **derived from two inputs**: the
 * method's `kind` AND the parent contact's `ContactType`. A person has no
 * switchboard; an organization has no home address.
 *
 * The vocabulary and its per-type scope live in `ContactLabelScope` below —
 * the design system's mirror of the C# `ContactLabelScope` (Odyssey.Dtos).
 * The server validates against it, the client filters *and* validates with it;
 * a client-side copy of the rule is a defect, not a convenience.
 *
 * Value is the enum member name ('Switchboard'), never the ordinal. The trigger
 * renders its placeholder when `value` is not in the offered set — a stored
 * label that has become invalid (a concurrent type switch, a hand-edited row)
 * reads as *empty*, so the message on it is "Label is required", never a
 * message naming a value that is nowhere on screen.
 */

/* Every label reuses the neutral categorical foreground — a label names a
   channel, it does not encode status, so it gets no hue of its own. */
const LABEL_FG = 'oklch(0.74 0.02 250)';
const L = (key, label, icon, ordinal) => ({ key, label, icon, ordinal, color: LABEL_FG });

/** AddressLabel — 1–19 person/shared band, 20+ organization band. */
export const ADDRESS_LABELS = [
  L('Home', 'Home', 'home', 1),
  L('Work', 'Work', 'work', 2),
  L('Billing', 'Billing', 'receipt_long', 3),
  L('Other', 'Other', 'category', 4),
  L('Postal', 'Postal', 'markunread_mailbox', 5),
  L('Visiting', 'Visiting', 'storefront', 20),
  L('Registered', 'Registered', 'account_balance', 21),
  L('Branch', 'Branch', 'apartment', 22),
];

/** EmailLabel — `Home` is persisted as `Home` and displayed as "Personal". */
export const EMAIL_LABELS = [
  L('Home', 'Personal', 'home', 1),
  L('Work', 'Work', 'work', 2),
  L('Other', 'Other', 'category', 3),
  L('General', 'General', 'alternate_email', 20),
  L('Support', 'Support', 'support_agent', 21),
  L('Sales', 'Sales', 'sell', 22),
  L('Billing', 'Billing', 'receipt_long', 23),
  L('Claims', 'Claims', 'assignment_late', 24),
];

/** PhoneLabel — ordinal 5 is deliberately unused (it was `Fax` in draft v1). */
export const PHONE_LABELS = [
  L('Home', 'Home', 'home', 1),
  L('Work', 'Work', 'work', 2),
  L('Mobile', 'Mobile', 'smartphone', 3),
  L('Other', 'Other', 'category', 4),
  L('Switchboard', 'Switchboard', 'phone_in_talk', 20),
  L('Support', 'Support', 'support_agent', 21),
  L('Sales', 'Sales', 'sell', 22),
  L('Billing', 'Billing', 'receipt_long', 23),
  L('Claims', 'Claims', 'assignment_late', 24),
  L('Emergency', 'Emergency', 'emergency', 25),
  L('Direct', 'Direct', 'phone_forwarded', 26),
];

const REGISTRY = { address: ADDRESS_LABELS, email: EMAIL_LABELS, phone: PHONE_LABELS };

/* Declared in DISPLAY order, per (kind, type) — the default a new method opens
   on is always the first member, so order and default cannot drift apart.
   `Other` is valid for both types everywhere, which is what makes it a legal
   clamp target. Widening a list is free; NARROWING one strands existing rows
   and needs its own remap migration in the same commit. */
const SCOPE = {
  address: {
    Person: ['Home', 'Work', 'Billing', 'Postal', 'Other'],
    Organization: ['Visiting', 'Registered', 'Branch', 'Billing', 'Postal', 'Other'],
  },
  email: {
    Person: ['Home', 'Work', 'Other'],
    Organization: ['General', 'Support', 'Sales', 'Billing', 'Claims', 'Other'],
  },
  phone: {
    Person: ['Home', 'Work', 'Mobile', 'Other'],
    Organization: ['Switchboard', 'Support', 'Sales', 'Billing', 'Claims', 'Emergency', 'Direct', 'Mobile', 'Other'],
  },
};

const byKey = (kind, key) => (REGISTRY[kind] || []).find((l) => l.key === key) || null;

/** The one place the vocabulary's per-type scope is expressed. */
export const ContactLabelScope = {
  /** Every member of a kind's enum, in ordinal order. */
  all: (kind) => REGISTRY[kind] || [],
  /** The offered set for a (kind, contactType), in display order. */
  labelsFor: (kind, type) => ((SCOPE[kind] && SCOPE[kind][type]) || []).map((k) => byKey(kind, k)).filter(Boolean),
  /** The label a new method opens on — the first member of the offered set. */
  defaultFor: (kind, type) => {
    const list = ContactLabelScope.labelsFor(kind, type);
    return list.length ? list[0].key : '';
  },
  /** Write-path check. Server and client run this same predicate. */
  isValidFor: (kind, key, type) => ContactLabelScope.labelsFor(kind, type).some((l) => l.key === key),
  /** Import + type-switch remap: resolve to a valid label, never drop the row. */
  clamp: (kind, key, type) => (ContactLabelScope.isValidFor(kind, key, type) ? key : 'Other'),
  /**
   * Descriptor for a stored key. The fallback resolves `Other` **by key** — a
   * positional `[^1]` would now render an undefined ordinal as `Branch` /
   * `Claims` / `Direct`: plausible, specific and wrong.
   */
  metaFor: (kind, key) => byKey(kind, key) || byKey(kind, 'Other'),
  /** Ordinal for a member name — the wire contract is the ordinal. */
  ordinalOf: (kind, key) => { const m = byKey(kind, key); return m ? m.ordinal : null; },
};

export function ContactMethodLabelSelect({
  kind = 'phone',
  contactType = 'Person',
  value,
  onChange,
  label = 'Label',
  placeholder = 'Select label…',
  ...rest
}) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { RegistrySelect } = NS;
  if (!RegistrySelect) return null;
  return (
    <RegistrySelect
      value={value}
      onChange={onChange}
      label={label}
      placeholder={placeholder}
      types={ContactLabelScope.labelsFor(kind, contactType)}
      {...rest}
    />
  );
}
