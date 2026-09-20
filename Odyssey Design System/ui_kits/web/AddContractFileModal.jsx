/* AddContractFileModal — upload documents to a contract (§3 step 4,
   §7 POST …/files). The user uploads one or more files (the signed agreement,
   an amendment, correspondence) straight from their machine — drag-drop or
   browse — and tags each with a `ContractFileType`. Uses the DS `FileUpload`
   (the same control the Files page and account uploads use) with the contract
   file-type `kinds` + `guessKind`, so every upload surface in Odyssey
   behaves identically. Each uploaded file becomes a ContractFile carrying its
   own name + size; the contract's existing documents are listed so duplicates
   are obvious.

   Each row also carries the document's VALIDITY — the period it covers, when
   it was issued and who issued it (a contact) — behind the same quiet
   "Add validity" toggle account uploads use (AddFileModal's AfmValidity). All
   four are optional and travel in the attach body; an omitted one is stored
   null. `ValidTo` before `ValidFrom` is refused here as it is by the service,
   on the "Valid to" control that most likely holds the mistake. */

const ACF_GUESS = (name) => {
  const ext = (name.split('.').pop() || '').toLowerCase();
  if (['eml', 'msg'].includes(ext)) return 'Correspondence';
  return 'Signed'; // the signed agreement is the common upload; user can re-tag
};

/* Per-file validity editor, rendered beneath each row through FileUpload's
   `renderFileExtra` slot — the contract twin of AddFileModal's AfmValidity,
   with the same controls in the same order so the two upload surfaces read
   identically. `patch(partial)` merges the fields back onto that file. */
const ConFileValidity = ({ file, patch, issuers }) => {
  const { useState } = React;
  const hasMeta = !!(file.validFrom || file.validTo || file.issuedAt || file.issuedBy);
  const [showMeta, setShowMeta] = useState(hasMeta);
  const rangeBad = file.validFrom && file.validTo && file.validTo < file.validFrom;
  return (
    <React.Fragment>
      <button type="button" className={`afm-meta-toggle ${showMeta ? 'on' : ''}`}
        onClick={() => setShowMeta(v => !v)}>
        <MIcon name="event" size={14} />
        {showMeta ? 'Hide validity' : 'Add validity'}
      </button>
      {showMeta && (
        <div className="afm-meta">
          <div className="afm-meta-grid">
            <DateField label="Valid from" value={file.validFrom || ''} onChange={(v) => patch({ validFrom: v || null })} />
            <DateField label="Valid to" value={file.validTo || ''} onChange={(v) => patch({ validTo: v || null })}
              placeholder="No end date" help="Leave blank if the document has no expiry." />
            <DateField label="Issued" value={file.issuedAt || ''} onChange={(v) => patch({ issuedAt: v || null })} />
            <ContactSelect label="Issued by" optional allowCreate value={file.issuedBy || ''}
              onChange={(v) => patch({ issuedBy: v || null })} contacts={issuers || []} />
          </div>
          {rangeBad && <div className="helper aam-err">“Valid to” can’t be before “Valid from”.</div>}
        </div>
      )}
    </React.Fragment>
  );
};

const AddContractFileModal = ({ contract, onClose, onAttach }) => {
  const { useState } = React;
  const H = window.OdysseyHelpers;
  const kinds = window.OdysseyData.contractFileTypes;
  const issuers = (window.OdysseyData.contacts || []).filter(c => !c.archived);

  const [files, setFiles] = useState([]); // { uid, name, kind, sizeBytes }
  const [error, setError] = useState(null);

  const existing = (contract.files || []).map(H.conFileRow);

  const submit = () => {
    if (!files.length) { setError('Add at least one document to upload.'); return; }
    if (files.some(f => !f.name.trim())) { setError('Every document needs a name.'); return; }
    if (files.some(f => f.validFrom && f.validTo && f.validTo < f.validFrom)) { setError('A document’s “Valid to” can’t be before its “Valid from”.'); return; }
    const nowIso = new Date().toISOString();
    const today = H.conToday();
    const out = files.map((f, i) => ({
      id: `cf-up-${Date.now()}-${i}`,
      fileMetadataId: `fm-up-${Date.now()}-${i}`, // a freshly stored FileMetadata
      kind: f.kind,
      name: f.name.trim(),
      size: window.afmFmtSize ? window.afmFmtSize(f.sizeBytes) : `${Math.round((f.sizeBytes || 0) / 1024)} KB`,
      uploaded: today,
      attachedByUserId: 'u-owner',
      attachedAtUtc: nowIso,
      // The four validity fields ride on the link row. Untouched → null.
      validFrom: f.validFrom || null,
      validTo: f.validTo || null,
      issuedAt: f.issuedAt || null,
      issuedBy: f.issuedBy || null,
    }));
    onAttach && onAttach(out);
  };

  return (
    <Modal
      title="Upload documents"
      subtitle="Upload the signed agreement, an amendment, or correspondence and attach it to this contract."
      icon="cloud_upload"
      className="afm-dialog"
      onClose={onClose}
      footer={
        <React.Fragment>
          <Button variant="text" onClick={onClose}>Cancel</Button>
          <Button variant="filled" color="primary" icon="upload_file" onClick={submit}>
            {files.length > 1 ? `Upload ${files.length} documents` : 'Upload document'}
          </Button>
        </React.Fragment>
      }>
      <FileUpload
        files={files}
        onChange={(next) => { setFiles(next); if (error) setError(null); }}
        error={error}
        kinds={kinds}
        guessKind={ACF_GUESS}
        maxMegabytes={(window.__odysseyImportLimits || {}).upload || 64}
        renderFileExtra={(file, patch) => <ConFileValidity file={file} patch={patch} issuers={issuers} />}
      />

      {existing.length > 0 && (
        <div className="con-existing-files">
          <div className="con-existing-head">Already attached</div>
          {existing.map(f => {
            const info = H.contractFileTypeInfo(f.kind);
            return (
              <div className="con-existing-row" key={f.id}>
                <span className="con-existing-ic" style={{ background: info.soft, color: info.color }}>
                  <MIcon name={info.icon} size={15} />
                </span>
                <span className="con-existing-name">{f.name}</span>
                <span className="con-existing-kind">{info.label}</span>
              </div>
            );
          })}
        </div>
      )}
    </Modal>
  );
};

Object.assign(window, { AddContractFileModal, ConFileValidity });
