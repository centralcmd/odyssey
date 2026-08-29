/* =============================================================
   profile-fields.jsx — the shared UserProfile field set + rules.

   Single source of truth for the "User Profile / display name" feature
   (spec §3 / §6 / §9). Consumed by BOTH the Account → Profile card
   (Account.jsx) and the first-sign-in Onboarding gate (Onboarding.jsx),
   so the two surfaces are field-for-field identical by construction.

   Everything here is built from the DS atoms only (Field / Select /
   DateField / Avatar / Chip / MIcon) — no new interactive widget, per the
   spec's "reuse existing accessible components" constraint. Sex is an
   OdsSelect with two OdsOptions; Date of birth is the DS DateField
   (1900-01-01 … today); names/title/display name are DS text Fields.
   ============================================================= */

/* ---- Sex enum (Odyssey.Application.Context.Sex → Male = 1, Female = 2).
   Ordinals mirror Odyssey.Finance.Dtos.Sex so the two never conflate as int;
   the UI conveys the two values in text (no colour/placeholder-only meaning). */
const SEX_OPTIONS = [
  { value: 'Female', label: 'Female' },
  { value: 'Male',   label: 'Male' },
];

/* ---- Field length caps (spec §6 MaxLength). Enforced client-side by
   trimming input; the server re-validates with the matching StringLength. */
const PROFILE_MAX = { firstName: 128, middleName: 128, lastName: 128, title: 128, displayName: 256 };

/* ---- Date-of-birth bounds: >= 1900-01-01 and <= today (spec §9). ---- */
const DOB_MIN = '1900-01-01';
const dobMax = () => new Date().toISOString().slice(0, 10);

/* ---- The seed Owner, complete (matches the demo seed so Jane skips the gate). */
const DEFAULT_PROFILE = {
  firstName: 'Jane',
  middleName: '',
  lastName: 'Sato',
  displayName: '',          // blank ⇒ resolver falls back to firstName ("Jane")
  title: 'Head of Household',
  birthDate: '1990-04-17',
  sex: 'Female',
};

/* ---- An empty profile — the state a brand-new user hits the gate with. ---- */
const EMPTY_PROFILE = { firstName: '', middleName: '', lastName: '', displayName: '', title: '', birthDate: '', sex: '' };

/* ---- Resolution rule (spec §9), owner-side: DisplayName ?? FirstName.
   (The claim-aware Email / "Unknown user" tail lives server-side in the
   resolver; a user always sees their own name, so it never applies here.) */
const resolveProfileName = (p) => ((p && p.displayName) || '').trim() || ((p && p.firstName) || '').trim() || '';
const profileInitials = (p) => {
  const dn = ((p && p.displayName) || '').trim();
  if (dn) { const parts = dn.split(/\s+/); return ((parts[0][0] || '') + (parts.length > 1 ? parts[parts.length - 1][0] : '')).toUpperCase(); }
  const f = ((p && p.firstName) || '').trim(); const l = ((p && p.lastName) || '').trim();
  return ((f[0] || '') + (l[0] || '')).toUpperCase() || '?';
};

/* ---- Validation (spec §9) ----------------------------------------------
   • required: firstName, lastName, birthDate, sex
   • names/display name: reject email-format (CWE-451) + control chars (CWE-117)
   • birthDate: 1900-01-01 … today
   Returns { errors: {field: msg}, isComplete } — isComplete = all required
   present & valid (the server-computed flag the onboarding gate keys on). */
const EMAIL_RE = /^[^@\s]+@[^@\s]+\.[^@\s]+$/;
const CTRL_RE = /[\u0000-\u001F\u007F]/;
const NAME_FIELDS = ['firstName', 'middleName', 'lastName', 'displayName', 'title'];

function validateProfile(p) {
  const errors = {};
  for (const k of NAME_FIELDS) {
    const v = (p[k] || '').trim();
    if (!v) continue;
    if (EMAIL_RE.test(v)) errors[k] = 'This can’t be an email address.';
    else if (CTRL_RE.test(p[k])) errors[k] = 'Remove line breaks and control characters.';
    else if (v.length > PROFILE_MAX[k]) errors[k] = `Keep this under ${PROFILE_MAX[k]} characters.`;
  }
  if (!(p.firstName || '').trim()) errors.firstName = errors.firstName || 'First name is required.';
  if (!(p.lastName || '').trim()) errors.lastName = errors.lastName || 'Last name is required.';
  if (!p.birthDate) errors.birthDate = 'Date of birth is required.';
  else if (p.birthDate < DOB_MIN) errors.birthDate = 'Enter a date on or after 1 Jan 1900.';
  else if (p.birthDate > dobMax()) errors.birthDate = 'Date of birth can’t be in the future.';
  if (!p.sex) errors.sex = 'Select an option.';
  const isComplete = !!(p.firstName && p.lastName && p.birthDate && p.sex);
  return { errors, isComplete };
}

/* ---- Transparency notice (GDPR Art. 13, spec §3) ------------------------
   Shown on BOTH surfaces at collection. States what the name broadcasts and
   why birth date / sex are collected — visible only to the owner. */
function ProfileTransparencyNote() {
  return (
    <div className="pf-note" role="note">
      <MIcon name="info" size={16} className="pf-note-ic" />
      <div className="pf-note-body">
        <p><b>Your name</b> — your display name, or your first name — is shown to other people in this
          workspace on items you create.</p>
        <p><b>Date of birth and sex</b> are used for retirement and long-term financial planning, and are
          visible only to you.</p>
      </div>
    </div>
  );
}

/* ---- The field set --------------------------------------------------------
   `profile` = the working values, `onField(name)(value)` sets one field,
   `errors` = the per-field messages from validateProfile. `showRequired`
   draws the `*` markers (both surfaces use it). `idPrefix` keeps ids unique
   when two instances mount. */
function ProfileFields({ profile, onField, errors = {}, idPrefix = 'pf' }) {
  const set = (name) => (v) => {
    const capped = typeof v === 'string' && PROFILE_MAX[name] ? v.slice(0, PROFILE_MAX[name]) : v;
    onField(name)(capped);
  };
  return (
    <div className="pf-fields">
      <FormRow cols={2}>
        <Field id={`${idPrefix}-first`} label="First name" required value={profile.firstName}
          onChange={set('firstName')} placeholder="Jane" error={errors.firstName} autoComplete="given-name" />
        <Field id={`${idPrefix}-last`} label="Last name" required value={profile.lastName}
          onChange={set('lastName')} placeholder="Sato" error={errors.lastName} autoComplete="family-name" />
      </FormRow>

      <FormRow cols={2}>
        <Field id={`${idPrefix}-middle`} label="Middle name" optional value={profile.middleName}
          onChange={set('middleName')} placeholder="—" error={errors.middleName} autoComplete="additional-name" />
        <Field id={`${idPrefix}-title`} label="Title" optional value={profile.title}
          onChange={set('title')} placeholder="e.g. Dr., CFO" error={errors.title}
          helper="A free-text title. Not shown in attribution." />
      </FormRow>

      <Field id={`${idPrefix}-display`} label="Display name" optional value={profile.displayName}
        onChange={set('displayName')} placeholder={profile.firstName ? `Defaults to “${profile.firstName}”` : 'How others see you'}
        error={errors.displayName}
        helper="How you appear to others. Leave blank to use your first name." />

      <FormRow cols={2}>
        <DateField id={`${idPrefix}-dob`} label="Date of birth" required value={profile.birthDate || null}
          onChange={(iso) => onField('birthDate')(iso || '')} min={DOB_MIN} max={dobMax()} error={errors.birthDate}
          help="Used for financial planning. Visible only to you." />
        <Select id={`${idPrefix}-sex`} label="Sex" required value={profile.sex}
          onChange={onField('sex')} options={SEX_OPTIONS} placeholder="Select…" error={errors.sex} />
      </FormRow>
    </div>
  );
}

Object.assign(window, {
  SEX_OPTIONS, PROFILE_MAX, DOB_MIN, dobMax,
  DEFAULT_PROFILE, EMPTY_PROFILE,
  resolveProfileName, profileInitials, validateProfile,
  ProfileTransparencyNote, ProfileFields,
});
