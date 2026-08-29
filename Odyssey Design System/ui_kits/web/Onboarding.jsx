/* =============================================================
   Onboarding.jsx — first-sign-in profile completion gate (/onboarding).

   The blocking form a signed-in user with an incomplete profile is routed to
   before the app body renders (spec §3 / §5). Rendered under a dedicated
   OnboardingLayout — the normal drawer / module rail / nav shell is NOT
   present; navigation away is prevented until the required fields are saved.

   Same field set as the Account → Profile card (shared ProfileFields), plus
   the GDPR transparency notice. On save the required fields are validated
   (First / Last name, Date of birth, Sex); a complete save releases the gate
   and returns the user to their originally-requested route.

   Built from DS atoms only. The gate is a UX / data-completeness control, not
   an authz boundary — see the spec; here it's the client routing rule.
   ============================================================= */

function Onboarding({ onDone, requestedLabel = 'the dashboard' }) {
  const { useState, useEffect, useRef } = React;
  const [profile, setProfile] = useState(EMPTY_PROFILE);
  const [errors, setErrors] = useState({});
  const [attempted, setAttempted] = useState(false);
  const [saving, setSaving] = useState(false);
  const cardRef = useRef(null);

  const onField = (name) => (v) => {
    setProfile((p) => ({ ...p, [name]: v }));
    // Clear a field's error as soon as the user edits it (re-validated on save).
    if (attempted) setErrors((e) => { if (!e[name]) return e; const n = { ...e }; delete n[name]; return n; });
  };

  const { errors: liveErrors, isComplete } = validateProfile(profile);
  const errorCount = Object.keys(liveErrors).length;

  // Manage focus into the first field when the gate opens (spec §3 a11y).
  useEffect(() => { const el = document.getElementById('onb-first'); if (el) el.focus(); }, []);

  const save = () => {
    setAttempted(true);
    if (errorCount > 0) {
      setErrors(liveErrors);
      // Move focus to the first offending field (source order).
      const order = ['firstName', 'lastName', 'middleName', 'title', 'displayName', 'birthDate', 'sex'];
      const first = order.find((k) => liveErrors[k]);
      const map = { firstName: 'onb-first', lastName: 'onb-last', middleName: 'onb-middle', title: 'onb-title', displayName: 'onb-display', birthDate: 'onb-dob', sex: 'onb-sex' };
      const el = first && document.getElementById(map[first]);
      if (el && el.focus) el.focus();
      return;
    }
    setErrors({});
    setSaving(true);
    setTimeout(() => { setSaving(false); onDone && onDone(profile); }, 500);
  };

  return (
    <div className="onb-shell">
      <div className="onb-card" ref={cardRef}>
        <div className="onb-brand">
          <BrandMark size={68} />
        </div>

        <div className="onb-head">
          <h1 className="onb-title">Complete your profile</h1>
          <p className="onb-sub">
            One quick step before you get started. Tell us your name so your entries are attributed to a
            person, not an email address. You’ll return to {requestedLabel} when you’re done.
          </p>
        </div>

        <div className="onb-progress" aria-hidden="true">
          <span className={`onb-progress-bar ${isComplete ? 'done' : ''}`} />
          <span className="onb-progress-txt">{isComplete ? 'Ready to continue' : 'Required fields needed'}</span>
        </div>

        {attempted && errorCount > 0 && (
          <div className="onb-alert">
            <Alert severity="error">
              <b>Check the highlighted fields.</b> {errorCount === 1 ? 'One field needs' : `${errorCount} fields need`} your attention before you can continue.
            </Alert>
          </div>
        )}

        <ProfileFields profile={profile} onField={onField} errors={attempted ? errors : {}} idPrefix="onb" />

        <ProfileTransparencyNote />

        <div className="onb-actions">
          <span className="onb-required-note"><span className="odc-field-req">*</span> Required</span>
          <Button variant="filled" color="primary" iconRight="arrow_forward" loading={saving}
            onClick={save} disabled={saving}>
            Save and continue
          </Button>
        </div>

        <div className="onb-foot">
          <MIcon name="lock" size={13} />
          <span>Your date of birth and sex are visible only to you.</span>
        </div>
      </div>
    </div>
  );
}

Object.assign(window, { Onboarding });
