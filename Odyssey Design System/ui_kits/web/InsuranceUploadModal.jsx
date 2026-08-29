/* InsuranceUploadModal — attach already-uploaded documents to a policy OR to a
   specific renewal period (spec §7 endpoints 10 & 13). Uses the DS FileUpload
   (drag/drop + browse + per-file rename and type picker), scoped to an
   insurance policy via a policy-file `guessKind` + the PolicyFileType `kinds`. A target selector routes the attachment to the whole policy
   or one of its renewal periods. Allowed types mirror the §4 allow-list
   (PDF / PNG / JPEG / WebP); the type picker uses the PolicyFileType vocabulary. */

const InsuranceUploadModal = ({ policy, initialTarget, onClose, onUpload }) => {
  const { useState } = React;
  const [files, setFiles] = useState([]);
  const [target, setTarget] = useState(initialTarget || 'policy');
  const [error, setError] = useState(null);

  const renewalOptions = (policy.renewals || [])
    .slice().sort((a, b) => (a.fromDate < b.fromDate ? 1 : -1))
    .map(r => ({ value: r.id, label: `Period ${window.OdysseyHelpers.dateLong(r.fromDate)} → ${window.OdysseyHelpers.dateLong(r.toDate)}` }));
  const targetOptions = [{ value: 'policy', label: 'Whole policy' }, ...renewalOptions];

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
      id: `ipf-new-${Date.now()}-${i}`,
      name: f.name.trim(), kind: f.kind, size: afmFmtSize(f.sizeBytes), uploaded, effectiveDate: null,
    }));
    onUpload(built, target);
  };

  return (
    <Modal
      title="Attach documents"
      subtitle="Attach the certificate, policy wording, invoice or claim documents — to the policy or a specific renewal."
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
      {renewalOptions.length > 0 && (
        <div className="field">
          <div className="label">Attach to</div>
          <Select value={target} onChange={setTarget} options={targetOptions} />
          <div className="helper">Documents attach to the whole policy or to one renewal period.</div>
        </div>
      )}
      <FileUpload files={files} onChange={(next) => { setFiles(next); if (error) setError(null); }} error={error}
        kinds={window.OdysseyData.policyFileTypes} guessKind={guessKind} />
    </Modal>
  );
};

Object.assign(window, { InsuranceUploadModal });
