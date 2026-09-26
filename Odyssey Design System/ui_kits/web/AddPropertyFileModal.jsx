/* AddPropertyFileModal — attach documents to a property (*Property Documents —
   Backend, Draft v2* §3, §5.1). There is no upload endpoint on the property: a
   document is a FileMetadata in the one Files store, linked here by id. The
   dialog offers the two ways a file gets that id:

     • Upload new — the DS FileUpload. Each file goes through the Files API
       (files.create) and is then attached; the bytes land in Files like any
       other upload, so the same file can later be attached to the loan
       contract that financed the property without a second copy.
     • From Files — pick files already in the store. A file already linked to
       this property is shown but disabled ("Already attached"), which is the
       409 the server would answer. A file whose SERVER-RECORDED content type
       is off the shared document allow-list (DOCUMENT_CONTENT_TYPES — PDF,
       PNG, JPEG, WebP, the one list contract documents use too) is disabled
       with the reason in text: the server would answer 400.

   Every picked file carries a PropertyFileType (default guessed from the name;
   Other is the zero member, so an unset type degrades to Other, never Deed)
   and, behind the same "Add validity" toggle contract uploads use, the four
   optional validity fields. ValidTo before ValidFrom is refused on the
   "Valid to" control, as the service does. */

const APF_TABS = [
  { value: 'upload', label: 'Upload new', icon: 'upload_file' },
  { value: 'library', label: 'From Files', icon: 'folder' },
];

const apfCtIcon = (ct) => (ct === 'application/pdf' ? 'picture_as_pdf' : /^image\//.test(ct || '') ? 'image' : ct === 'text/html' ? 'code' : 'description');

/* One pickable library row. Disabled rows say why in text, never colour alone. */
const ApfLibraryRow = ({ file, state, picked, onToggle, onPatch, issuers }) => {
  const H = window.OdysseyHelpers;
  const disabled = state !== 'ok';
  const reason = state === 'attached' ? 'Already attached'
    : state === 'type' ? `${H.propContentTypeShort(file.contentType)} not accepted` : null;
  const Validity = window.ConFileValidity;
  return (
    <div className={`prop-lib-row${picked ? ' on' : ''}${disabled ? ' off' : ''}`}>
      <button type="button" className="prop-lib-main" disabled={disabled} aria-pressed={!!picked} onClick={onToggle}>
        <span className="prop-lib-check"><MIcon name={disabled ? (state === 'attached' ? 'link' : 'block') : picked ? 'check_box' : 'check_box_outline_blank'} size={20} /></span>
        <span className="prop-lib-ic"><MIcon name={apfCtIcon(file.contentType)} size={16} /></span>
        <span className="prop-lib-text">
          <span className="prop-lib-name">{file.name}</span>
          <span className="prop-lib-meta">{H.propContentTypeShort(file.contentType)} · {file.size} · uploaded {H.conDate ? H.conDate(file.uploaded) : file.uploaded}</span>
        </span>
        {reason ? <span className="prop-lib-reason">{reason}</span> : null}
      </button>
      {picked ? (
        <div className="prop-lib-extra">
          <div className="prop-lib-type"><PropertyFileTypeSelect label="Document type" value={picked.kind} onChange={(v) => onPatch({ kind: v })} /></div>
          {Validity ? <Validity file={picked} patch={onPatch} issuers={issuers} /> : null}
        </div>
      ) : null}
    </div>
  );
};

const AddPropertyFileModal = ({ property, attached = [], onClose, onAttach }) => {
  const { useState } = React;
  const H = window.OdysseyHelpers;
  const D = window.OdysseyData;
  const issuers = (D.contacts || []).filter(c => !c.archived);
  const [tab, setTab] = useState('upload');
  const [files, setFiles] = useState([]);
  const [picked, setPicked] = useState({});
  const [q, setQ] = useState('');
  const [error, setError] = useState(null);

  const attachedIds = new Set(attached.map(a => a.fileMetadataId));
  const pool = D.fileLibraryPool().filter(f => !q || f.name.toLowerCase().includes(q.toLowerCase()));
  const stateOf = (f) => (attachedIds.has(f.id) ? 'attached' : !H.propContentTypeAllowed(f.contentType) ? 'type' : 'ok');
  const pickedList = Object.values(picked);
  const togglePick = (f) => setPicked(p => {
    const n = { ...p };
    if (n[f.id]) delete n[f.id]; else n[f.id] = { fileMetadataId: f.id, name: f.name, kind: H.propGuessFileType(f.name) };
    return n;
  });
  const patchPick = (id) => (partial) => setPicked(p => ({ ...p, [id]: { ...p[id], ...partial } }));
  const badRange = (f) => f.validFrom && f.validTo && f.validTo < f.validFrom;

  const nowIso = () => new Date().toISOString();
  const link = (f, i, extra) => ({
    id: `pf-new-${Date.now()}-${i}`, propertyId: property.id, fileMetadataId: f.fileMetadataId, kind: f.kind || 'Other',
    attachedByUserId: 'u-owner', attachedByName: 'Owner Demo', attachedAtUtc: nowIso(),
    validFrom: f.validFrom || null, validTo: f.validTo || null, issuedAt: f.issuedAt || null, issuedBy: f.issuedBy || null, ...(extra || {}),
  });

  const submit = () => {
    if (tab === 'upload') {
      if (!files.length) { setError('Add at least one document to upload.'); return; }
      const rejected = files.find(f => !H.propContentTypeAllowed(H.propContentTypeFor(f.name)));
      if (rejected) { setError(`“${rejected.name}” is ${H.propContentTypeShort(H.propContentTypeFor(rejected.name))}. Property documents accept ${D.DOCUMENT_CONTENT_TYPE_LABEL} only.`); return; }
      if (files.some(badRange)) { setError('A document’s “Valid to” can’t be before its “Valid from”.'); return; }
      const today = new Date().toISOString().slice(0, 10);
      const out = files.map((f, i) => {
        const id = `fm-up-${Date.now()}-${i}`;
        const size = window.afmFmtSize ? window.afmFmtSize(f.sizeBytes) : `${Math.round((f.sizeBytes || 0) / 1024)} KB`;
        D.propertyFileLibrary.push({ id, name: f.name.trim(), contentType: H.propContentTypeFor(f.name), size, uploaded: today, uploadedByName: 'Owner Demo' });
        return link({ ...f, fileMetadataId: id }, i);
      });
      onAttach && onAttach(out);
      return;
    }
    if (!pickedList.length) { setError('Pick at least one file to attach.'); return; }
    if (pickedList.some(badRange)) { setError('A document’s “Valid to” can’t be before its “Valid from”.'); return; }
    onAttach && onAttach(pickedList.map((f, i) => link(f, i)));
  };

  const n = tab === 'upload' ? files.length : pickedList.length;
  const cta = tab === 'upload'
    ? (n > 1 ? `Upload and attach ${n}` : 'Upload and attach')
    : (n > 1 ? `Attach ${n} documents` : 'Attach document');
  const noun = property.type === 'Vehicle' ? 'the registration, an inspection, the insurance certificate or a warranty' : 'the deed, the purchase agreement, a valuation or a warranty';

  return (
    <Modal
      title="Attach documents"
      subtitle={`Keep ${noun} with ${property.name}. Files stay in Files; attaching links them here.`}
      icon="attach_file"
      className="afm-dialog"
      onClose={onClose}
      footer={
        <React.Fragment>
          <Button variant="text" onClick={onClose}>Cancel</Button>
          <Button variant="filled" color="primary" icon={tab === 'upload' ? 'upload_file' : 'link'} disabled={tab === 'library' && n === 0} onClick={submit}>{cta}</Button>
        </React.Fragment>
      }>
      <div className="prop-doc-modal">
        <SegmentedControl full ariaLabel="Where the document comes from" value={tab} onChange={(v) => { setTab(v); setError(null); }} options={APF_TABS} />

        {tab === 'upload' ? (
          <FileUpload
            files={files}
            onChange={(next) => { setFiles(next); if (error) setError(null); }}
            error={error}
            kinds={D.propertyFileTypes}
            guessKind={H.propGuessFileType}
            accept=".pdf,.png,.jpg,.jpeg,.webp,application/pdf,image/png,image/jpeg,image/webp"
            maxMegabytes={(window.__odysseyImportLimits || {}).upload || 64}
            renderFileExtra={(file, patch) => (window.ConFileValidity ? <window.ConFileValidity file={file} patch={patch} issuers={issuers} /> : null)}
          />
        ) : (
          <div className="prop-lib">
            <SearchField placeholder="Search files by name…" value={q} onChange={setQ} />
            <div className="prop-lib-list odc-scroll" role="group" aria-label="Files">
              {pool.length === 0 ? <EmptyLine>No files match “{q}”.</EmptyLine> : pool.map(f => (
                <ApfLibraryRow key={f.id} file={f} state={stateOf(f)} picked={picked[f.id]} issuers={issuers}
                  onToggle={() => { togglePick(f); if (error) setError(null); }} onPatch={patchPick(f.id)} />
              ))}
            </div>
            {error ? <div className="helper aam-err" role="alert">{error}</div> : null}
            <div className="prop-lib-foot"><MIcon name="info" size={14} />Only {D.DOCUMENT_CONTENT_TYPE_LABEL} files can be attached — the same rule as contract documents.</div>
          </div>
        )}
      </div>
    </Modal>
  );
};

Object.assign(window, { AddPropertyFileModal });
