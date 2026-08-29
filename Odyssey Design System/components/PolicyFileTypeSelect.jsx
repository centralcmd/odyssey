/**
 * Odyssey DS — PolicyFileTypeSelect
 * Single-select pre-wired to the PolicyFileType vocabulary (the documents that
 * attach to an insurance policy OR an individual renewal): Contract · Invoice ·
 * Terms & conditions · Policy document · Claim document · Other. Each option
 * carries its Material icon in its category color. A typed wrapper over `Select`.
 *
 * Value is the enum key. `POLICY_FILE_TYPES` (exported) is the canonical registry,
 * mirroring `OdysseyData.policyFileTypes` and the C# `PolicyFileType` enum.
 */

export const POLICY_FILE_TYPES = [
  { key: 'Contract',           label: 'Contract',           icon: 'history_edu',       color: 'oklch(0.72 0.16 295)', soft: 'oklch(0.72 0.16 295 / 0.16)' },
  { key: 'Invoice',            label: 'Invoice',            icon: 'receipt',           color: 'oklch(0.80 0.13 85)',  soft: 'oklch(0.80 0.13 85 / 0.16)' },
  { key: 'TermsAndConditions', label: 'Terms & conditions', icon: 'menu_book',         color: 'oklch(0.77 0.14 110)', soft: 'oklch(0.77 0.14 110 / 0.16)' },
  { key: 'PolicyDocument',     label: 'Policy document',    icon: 'shield',            color: 'oklch(0.72 0.16 282)', soft: 'oklch(0.72 0.16 282 / 0.16)' },
  { key: 'ClaimDocument',      label: 'Claim document',     icon: 'assignment_late',   color: 'oklch(0.72 0.16 22)',  soft: 'oklch(0.72 0.16 22 / 0.16)' },
  { key: 'Other',              label: 'Other',              icon: 'insert_drive_file', color: 'oklch(0.74 0.02 250)', soft: 'oklch(0.74 0.02 250 / 0.16)' },
];

export function PolicyFileTypeSelect({ value, onChange, label = 'Document type', types, ...rest }) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { RegistrySelect } = NS;
  if (!RegistrySelect) return null;
  return <RegistrySelect value={value} onChange={onChange} label={label} types={types || POLICY_FILE_TYPES} {...rest} />;
}
