/* AddFileModal — dialog opened from the Files page "Add file" button and the
   per-account "Add file" action menu.

   Built on the DS Modal shell (portal + scrim + header + scrollable body +
   footer, Esc / scrim-click / focus trap). The upload surface is the DS
   FileUpload component (dropzone + ready-file list with inline rename / kind
   picker / remove); this modal adds the account selector, the account file-type
   vocabulary (via `kinds`), and the optional per-file validity editor (Valid
   from / to · Issued · Issued by) through FileUpload's `renderFileExtra` slot.

   onCreate(accountId, files[]) — files match the AccountFile shape in data.js:
     { id, name, kind, size, uploaded, validFrom, validTo, issuedAt, issuedBy } */

/* File-kind registry for the upload picker. Canonical source is the design
   system (window.OdysseyData.accountFileTypes, seeded from data.js) — the default
   vocabulary is account files. The inline array is a defensive fallback only. */
const AFM_KINDS = (window.OdysseyData && window.OdysseyData.accountFileTypes) || [
  { key: 'Message',   label: 'Message',   icon: 'mail',              color: 'oklch(0.76 0.13 225)',  soft: 'oklch(0.76 0.13 225 / 0.16)' },
  { key: 'Statement', label: 'Statement', icon: 'description',       color: 'oklch(0.79 0.115 188)', soft: 'oklch(0.79 0.115 188 / 0.16)' },
  { key: 'Contract',  label: 'Contract',  icon: 'history_edu',       color: 'oklch(0.72 0.16 295)',  soft: 'oklch(0.72 0.16 295 / 0.16)' },
  { key: 'Tax',       label: 'Tax',       icon: 'request_quote',     color: 'oklch(0.75 0.16 330)',  soft: 'oklch(0.75 0.16 330 / 0.16)' },
  { key: 'Other',     label: 'Other',     icon: 'insert_drive_file', color: 'oklch(0.74 0.02 250)',  soft: 'oklch(0.74 0.02 250 / 0.16)' },
];

const afmToday = () => {
  const d = new Date();
  const p = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
};

const afmFmtSize = (bytes) => {
  if (bytes == null) return '—';
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) {
    const kb = bytes / 1024;
    return `${kb < 10 ? kb.toFixed(1) : Math.round(kb)} KB`;
  }
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
};

/* Kind guess by extension + vocabulary — passed to FileUpload's `guessKind` so
   dropped/browsed files land tagged for the surface that opened the modal. */
const afmGuessKind = (name, vocab = 'account') => {
  const ext = (name.split('.').pop() || '').toLowerCase();
  const isImage = ['jpg', 'jpeg', 'png', 'heic', 'webp', 'gif', 'tiff'].includes(ext);
  if (vocab === 'transaction') {
    if (isImage) return 'Receipt';
    if (ext === 'pdf') return 'Invoice';
    return 'Other';
  }
  // account vocabulary — Receipt/Invoice don't exist here
  if (ext === 'pdf') return 'Statement';
  return 'Other';
};


/* ---- Per-file validity editor -------------------------------------------
   Rendered beneath each file row via FileUpload's `renderFileExtra` slot. A
   quiet toggle reveals the validity metadata grid (Valid from / to · Issued ·
   Issued by); `patch(partial)` merges the fields back onto that file. */
const AfmValidity = ({ file, patch, issuers }) => {
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
            <DateField label="Valid to" value={file.validTo || ''} onChange={(v) => patch({ validTo: v || null })} />
            <DateField label="Issued" value={file.issuedAt || ''} onChange={(v) => patch({ issuedAt: v || null })} />
            <Select label="Issued by" value={file.issuedBy || ''} placeholder="Select issuer…"
              onChange={(v) => patch({ issuedBy: v || null })} options={issuers || []} />
          </div>
          {rangeBad && <div className="helper aam-err">“Valid to” can’t be before “Valid from”.</div>}
        </div>
      )}
    </React.Fragment>
  );
};

const AddFileModal = ({ onClose, onCreate, defaultAccount = '', accounts }) => {
  const { useState } = React;
  const d = window.OdysseyData;
  const acctOptions = (accounts || d.accounts)
    .filter(a => !a.closed)
    .map(a => ({ value: a.id, label: `${a.name} ${a.number}` }));
  const issuers = (d.contacts || []).filter(c => !c.archived).map(c => ({ value: c.id, label: c.name }));

  const [account, setAccount] = useState(defaultAccount || '');
  const [files, setFiles] = useState([]); // { uid, name, kind, sizeBytes, validFrom?, validTo?, issuedAt?, issuedBy? }
  const [errors, setErrors] = useState({});

  // (Esc-to-close, scrim click and focus handling come from the DS Modal shell.)

  const submit = () => {
    const next = {};
    if (!account) next.account = 'Choose which account these files belong to.';
    if (!files.length) next.files = 'Add at least one file.';
    if (files.some(f => !f.name.trim())) next.files = 'Every file needs a name.';
    if (files.some(f => f.validFrom && f.validTo && f.validTo < f.validFrom)) next.files = 'A file’s “Valid to” can’t be before its “Valid from”.';
    if (Object.keys(next).length) { setErrors(next); return; }
    const uploaded = afmToday();
    const out = files.map((f, i) => ({
      id: `nf-${Date.now()}-${i}`,
      name: f.name.trim(),
      kind: f.kind,
      size: afmFmtSize(f.sizeBytes),
      uploaded,
      validFrom: f.validFrom || null,
      validTo: f.validTo || null,
      issuedAt: f.issuedAt || null,
      issuedBy: f.issuedBy || null,
    }));
    onCreate && onCreate(account, out);
  };

  return (
    <Modal
      title="Upload files"
      subtitle="Upload statements, receipts, or documents and attach them to an account."
      icon="cloud_upload"
      className="afm-dialog"
      onClose={onClose}
      footer={
        <React.Fragment>
          <Button variant="text" onClick={onClose}>Cancel</Button>
          <Button variant="filled" color="primary" icon="upload_file" onClick={submit}>
            {files.length > 1 ? `Upload ${files.length} files` : 'Upload'}
          </Button>
        </React.Fragment>
      }>
      {/* Account — always required, preselected from the launch context */}
      <Select
        label="Account"
        value={account}
        onChange={(v) => { setAccount(v); if (errors.account) setErrors(e => ({ ...e, account: undefined })); }}
        options={acctOptions}
        placeholder="Choose an account…"
      />
      {errors.account && <div className="helper aam-err" style={{ marginTop: -8 }}>{errors.account}</div>}

      <FileUpload
        files={files}
        onChange={(nextFiles) => { setFiles(nextFiles); if (errors.files) setErrors(e => ({ ...e, files: undefined })); }}
        error={errors.files}
        kinds={AFM_KINDS}
        guessKind={(name) => afmGuessKind(name, 'account')}
        renderFileExtra={(file, patch) => <AfmValidity file={file} patch={patch} issuers={issuers} />}
      />
    </Modal>
  );
};

Object.assign(window, {
  AddFileModal,
  afmFmtSize, afmGuessKind, afmToday, AFM_KINDS,
});
