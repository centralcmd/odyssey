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
  const H = window.OdysseyHelpers;
  return (
    <AttachDocumentsModal
      subtitle="Keep the signed agreement, an amendment, or correspondence with this contract. Files stay in Files; attaching links them here."
      kinds={window.OdysseyData.contractFileTypes}
      guessKind={ACF_GUESS}
      validity
      attachedIds={(contract.files || []).map(f => f.fileMetadataId)}
      onClose={onClose}
      onSubmit={(items) => {
        const nowIso = new Date().toISOString();
        const today = H.conToday();
        onAttach && onAttach(items.map((f, i) => ({
          id: `cf-up-${Date.now()}-${i}`, fileMetadataId: f.fileMetadataId, kind: f.kind, name: f.name, size: f.size,
          uploaded: today, attachedByUserId: 'u-owner', attachedAtUtc: nowIso,
          validFrom: f.validFrom, validTo: f.validTo, issuedAt: f.issuedAt, issuedBy: f.issuedBy,
        })));
      }}
    />
  );
};

Object.assign(window, { AddContractFileModal, ConFileValidity });
