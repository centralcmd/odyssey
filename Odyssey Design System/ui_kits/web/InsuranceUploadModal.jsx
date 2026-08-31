/* InsuranceUploadModal — attach already-uploaded documents to one renewal
   PERIOD (spec §7 endpoint 10). Uses the DS FileUpload (drag/drop + browse +
   per-file rename and type picker), scoped to an insurance policy via a
   policy-file `guessKind` + the PolicyFileType `kinds`. The dialog is always
   scoped to ONE period — a period is a document's only home. Opened from a
   period's own panel the target is fixed (`lockPeriod`) and reads as an
   "Attaching to" line; opened from the row menu it is a picker defaulted to the
   resolved target (the current period, else the latest-ending one), so a user
   filing a late-arriving document can still put it on an older period.
   Allowed types mirror the §4 allow-list
   (PDF / PNG / JPEG / WebP); the type picker uses the PolicyFileType vocabulary. */

const InsuranceUploadModal = ({ policy, renewalId, lockPeriod, onClose, onUpload }) => {
  const { useState } = React;
  const [files, setFiles] = useState([]);
  const [target, setTarget] = useState(renewalId);
  const [error, setError] = useState(null);

  // The dialog refuses to open without a period — there is nowhere to attach to.
  const periods = (policy.renewals || []).slice().sort((a, b) => (a.fromDate < b.fromDate ? 1 : -1));
  const renewal = periods.find(r => r.id === target) || periods.find(r => r.id === renewalId);
  if (!renewal) return null;
  const H = window.OdysseyHelpers;
  const periodLabel = (r) => `Period ${H.dateLong(r.fromDate)} → ${H.dateLong(r.toDate)}`;
  // A picker only where there is a choice to make: several periods, and the
  // target was inferred rather than chosen by the user.
  const choosable = !lockPeriod && periods.length > 1;

  const guessKind = (name) => {
    const isPdf = /\.pdf$/i.test(name);
    const looksClaim = /claim|skade/i.test(name);
    const looksInvoice = /invoice|premium|receipt|faktura/i.test(name);
    const looksTerms = /terms|wording|conditions|vilk/i.test(name);
    return looksClaim ? 'ClaimDocument' : looksInvoice ? 'Invoice' : looksTerms ? 'TermsAndConditions' : (isPdf ? 'PolicyDocument' : 'Other');
  };

  const submit = () => {
    if (!files.length) { setError('Add at least one file.'); return; }
    if (files.some(f => !f.name.trim())) { setError('Every file needs a name.'); return; }
    const uploaded = afmToday();
    const built = files.map((f, i) => ({
      id: `rnf-new-${Date.now()}-${i}`,
      name: f.name.trim(), kind: f.kind, size: afmFmtSize(f.sizeBytes), uploaded, effectiveDate: null,
    }));
    onUpload(built, renewal.id);
  };

  return (
    <Modal
      title="Attach documents"
      subtitle="Attach the certificate, policy wording, invoice or claim documents to a renewal period."
      icon="upload_file"
      className="afm-dialog"
      onClose={onClose}
      footer={
        <React.Fragment>
          <Button variant="text" onClick={onClose}>Cancel</Button>
          <Button variant="filled" color="primary" icon="upload_file" onClick={submit}>
            {files.length > 1 ? `Attach ${files.length} files` : 'Attach'}
          </Button>
        </React.Fragment>
      }>
      {/* The one fact to check before dropping a file, so it sits in the body —
          the subtitle is small secondary text carrying boilerplate. */}
      {choosable ? (
        <div className="field">
          <div className="label">Attaching to</div>
          <Select value={renewal.id} onChange={setTarget}
            options={periods.map(r => ({ value: r.id, label: periodLabel(r) }))} />
          <div className="helper">Defaults to the period in force — pick an earlier one to file a document against it.</div>
        </div>
      ) : (
        <div className="ins-attach-target">
          <MIcon name="event" size={16} />
          <span className="ins-attach-target-label">Attaching to</span>
          <span className="ins-attach-target-value">{periodLabel(renewal)}</span>
        </div>
      )}
      <FileUpload files={files} onChange={(next) => { setFiles(next); if (error) setError(null); }} error={error}
        kinds={window.OdysseyData.policyFileTypes} guessKind={guessKind} />
    </Modal>
  );
};

Object.assign(window, { InsuranceUploadModal });
