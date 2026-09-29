/* AttachDocumentsModal — the one "Attach documents" dialog every document
   surface uses (accounts, transactions, tax statements, contracts, properties).
   Two ways a file gets attached:
     • Upload new — the DS FileUpload; the bytes land in the one Files store.
     • From Files — pick files already in the store. Already-linked files are
       shown disabled ("Already attached"); files off the surface's content-type
       allow-list (when it has one) are disabled with the reason in text.
   Each picked file carries the surface's file type (guessed from the name) and,
   when `validity` is on, the optional Valid from / to · Issued · Issued by
   fields behind the "Add validity" toggle.

   The caller maps the normalized items to its own row shape:
     onSubmit([{ source: 'upload'|'library', fileMetadataId, name, kind, size,
                 validFrom, validTo, issuedAt, issuedBy }]) */

const ADM_TABS = [
  { value: 'upload', label: 'Upload new', icon: 'upload_file' },
  { value: 'library', label: 'From Files', icon: 'folder' },
];
const admCtIcon = (ct) => (ct === 'application/pdf' ? 'picture_as_pdf' : /^image\//.test(ct || '') ? 'image' : ct === 'text/html' ? 'code' : 'description');
const admCtFor = (name) => {
  const H = window.OdysseyHelpers;
  if (H.propContentTypeFor) return H.propContentTypeFor(name);
  const ext = (name.split('.').pop() || '').toLowerCase();
  return ({ pdf: 'application/pdf', png: 'image/png', jpg: 'image/jpeg', jpeg: 'image/jpeg', webp: 'image/webp' })[ext] || 'application/octet-stream';
};
const admCtShort = (ct) => { const H = window.OdysseyHelpers; return H.propContentTypeShort ? H.propContentTypeShort(ct) : ct; };

const AttachDocumentsModal = ({
  subtitle, kinds, guessKind, validity = false, attachedIds = [], restrictTypes = false,
  accept, header, validate, onClose, onSubmit, libraryOnly = false,
}) => {
  const { useState } = React;
  const H = window.OdysseyHelpers;
  const D = window.OdysseyData;
  const issuers = (D.contacts || []).filter(c => !c.archived);
  const Validity = window.ConFileValidity;
  const guess = guessKind || (() => 'Other');
  const allowed = (ct) => !restrictTypes || !H.propContentTypeAllowed || H.propContentTypeAllowed(ct);
  const kindOptions = (kinds || []).map(t => ({ value: t.key, label: t.label, icon: t.icon, iconColor: t.color }));

  const [tab, setTab] = useState(libraryOnly ? 'library' : 'upload');
  const [files, setFiles] = useState([]);
  const [picked, setPicked] = useState({});
  const [q, setQ] = useState('');
  const [error, setError] = useState(null);

  const linked = new Set(attachedIds.filter(Boolean));
  const pool = (D.fileLibraryPool ? D.fileLibraryPool() : []).filter(f => !q || f.name.toLowerCase().includes(q.toLowerCase()));
  const stateOf = (f) => (linked.has(f.id) ? 'attached' : !allowed(f.contentType) ? 'type' : 'ok');
  const pickedList = Object.values(picked);
  const togglePick = (f) => setPicked(p => {
    const n = { ...p };
    if (n[f.id]) delete n[f.id]; else n[f.id] = { fileMetadataId: f.id, name: f.name, size: f.size, kind: guess(f.name) };
    return n;
  });
  const patchPick = (id) => (partial) => setPicked(p => ({ ...p, [id]: { ...p[id], ...partial } }));
  const badRange = (f) => f.validFrom && f.validTo && f.validTo < f.validFrom;
  const vfields = (f) => ({ validFrom: f.validFrom || null, validTo: f.validTo || null, issuedAt: f.issuedAt || null, issuedBy: f.issuedBy || null });
  const today = () => new Date().toISOString().slice(0, 10);

  const submit = () => {
    const outer = validate ? validate() : null;
    if (outer) return;
    if (tab === 'upload') {
      if (!files.length) { setError('Add at least one document to upload.'); return; }
      if (files.some(f => !f.name.trim())) { setError('Every document needs a name.'); return; }
      const rejected = restrictTypes && files.find(f => !allowed(admCtFor(f.name)));
      if (rejected) { setError(`“${rejected.name}” is ${admCtShort(admCtFor(rejected.name))}. Only ${D.DOCUMENT_CONTENT_TYPE_LABEL || 'PDF and image'} files are accepted.`); return; }
      if (files.some(badRange)) { setError('A document’s “Valid to” can’t be before its “Valid from”.'); return; }
      onSubmit && onSubmit(files.map((f, i) => {
        const id = `fm-up-${Date.now()}-${i}`;
        const size = window.afmFmtSize ? window.afmFmtSize(f.sizeBytes) : `${Math.round((f.sizeBytes || 0) / 1024)} KB`;
        if (D.propertyFileLibrary) D.propertyFileLibrary.push({ id, name: f.name.trim(), contentType: admCtFor(f.name), size, uploaded: today(), uploadedByName: 'Owner Demo' });
        return { source: 'upload', fileMetadataId: id, name: f.name.trim(), kind: f.kind || 'Other', size, ...vfields(f) };
      }));
      return;
    }
    if (!pickedList.length) { setError('Pick at least one file to attach.'); return; }
    if (pickedList.some(badRange)) { setError('A document’s “Valid to” can’t be before its “Valid from”.'); return; }
    onSubmit && onSubmit(pickedList.map(f => ({ source: 'library', fileMetadataId: f.fileMetadataId, name: f.name, kind: f.kind || 'Other', size: f.size, ...vfields(f) })));
  };

  const n = tab === 'upload' ? files.length : pickedList.length;
  const cta = tab === 'upload'
    ? (n > 1 ? `Upload and attach ${n}` : 'Upload and attach')
    : (n > 1 ? `Attach ${n} documents` : 'Attach document');

  return (
    <Modal
      title={libraryOnly ? 'Choose from Files' : 'Attach documents'}
      subtitle={subtitle}
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
        {header}
        {libraryOnly ? null : <SegmentedControl full ariaLabel="Where the document comes from" value={tab} onChange={(v) => { setTab(v); setError(null); }} options={ADM_TABS} />}
        {tab === 'upload' ? (
          <FileUpload
            files={files}
            onChange={(next) => { setFiles(next); if (error) setError(null); }}
            error={error}
            kinds={kinds}
            guessKind={guess}
            accept={accept}
            showKinds={kinds ? undefined : false}
            maxMegabytes={(window.__odysseyImportLimits || {}).upload || 64}
            renderFileExtra={validity && Validity ? (file, patch) => <Validity file={file} patch={patch} issuers={issuers} /> : undefined}
          />
        ) : (
          <div className="prop-lib">
            <SearchField placeholder="Search files by name…" value={q} onChange={setQ} />
            <div className="prop-lib-list odc-scroll" role="group" aria-label="Files">
              {pool.length === 0 ? <EmptyLine>No files match “{q}”.</EmptyLine> : pool.map(f => {
                const st = stateOf(f), pk = picked[f.id], off = st !== 'ok';
                const reason = st === 'attached' ? 'Already attached' : st === 'type' ? `${admCtShort(f.contentType)} not accepted` : null;
                return (
                  <div key={f.id} className={`prop-lib-row${pk ? ' on' : ''}${off ? ' off' : ''}`}>
                    <button type="button" className="prop-lib-main" disabled={off} aria-pressed={!!pk} onClick={() => { togglePick(f); if (error) setError(null); }}>
                      <span className="prop-lib-check"><MIcon name={off ? (st === 'attached' ? 'link' : 'block') : pk ? 'check_box' : 'check_box_outline_blank'} size={20} /></span>
                      <span className="prop-lib-ic"><MIcon name={admCtIcon(f.contentType)} size={16} /></span>
                      <span className="prop-lib-text">
                        <span className="prop-lib-name">{f.name}</span>
                        <span className="prop-lib-meta">{admCtShort(f.contentType)} · {f.size} · uploaded {H.conDate ? H.conDate(f.uploaded) : f.uploaded}</span>
                      </span>
                      {reason ? <span className="prop-lib-reason">{reason}</span> : null}
                    </button>
                    {pk && (kindOptions.length || validity) ? (
                      <div className="prop-lib-extra">
                        {kindOptions.length ? <div className="prop-lib-type"><Select label="Document type" value={pk.kind} onChange={(v) => patchPick(f.id)({ kind: v })} options={kindOptions} /></div> : null}
                        {validity && Validity ? <Validity file={pk} patch={patchPick(f.id)} issuers={issuers} /> : null}
                      </div>
                    ) : null}
                  </div>
                );
              })}
            </div>
            {error ? <div className="helper aam-err" role="alert">{error}</div> : null}
            {restrictTypes ? <div className="prop-lib-foot"><MIcon name="info" size={14} />Only {D.DOCUMENT_CONTENT_TYPE_LABEL} files can be attached.</div> : null}
          </div>
        )}
      </div>
    </Modal>
  );
};

Object.assign(window, { AttachDocumentsModal });
