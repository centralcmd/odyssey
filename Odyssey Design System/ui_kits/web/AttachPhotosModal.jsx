/* AttachPhotosModal — the photo twin of AttachDocumentsModal. Two ways a photo
   gets onto an entry:
     • Upload new — the DS FileUpload (images only); uploads join the library.
     • From Photos — pick photos already in the library (window.PHOTOS). Photos
       already on the entry are shown disabled ("Already added").
   onSubmit([{ source: 'upload'|'library', photoId, name, seed, sizeBytes }]) */

const APM_TABS = [
  { value: 'upload', label: 'Upload new', icon: 'upload' },
  { value: 'library', label: 'From Photos', icon: 'photo_library' },
];
const apmSeed = (id) => { let h = 0; const s = String(id || ''); for (let i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) >>> 0; return h; };
const apmBg = (p) => {
  const v = window.plPhotoBg ? window.plPhotoBg(p.seed != null ? p.seed : apmSeed(p.id)) : null;
  return (v && (typeof v === 'string' ? v : v.background)) || 'var(--mud-palette-background-grey)';
};

const AttachPhotosModal = ({ subtitle, attachedIds = [], photos, onUploadFiles, onClose, onSubmit, uploadOnly = false }) => {
  const { useState } = React;
  const [tab, setTab] = useState('upload');
  const [files, setFiles] = useState([]);
  const [picked, setPicked] = useState([]);
  const [q, setQ] = useState('');
  const [error, setError] = useState(null);
  const linked = new Set(attachedIds.filter(Boolean));
  const source = photos || window.PHOTOS || [];
  const pool = source.filter(p => !p.archived && (!q || `${p.title || ''} ${p.name} ${p.location || ''}`.toLowerCase().includes(q.toLowerCase())));
  const toggle = (id) => { setPicked(prev => (prev.includes(id) ? prev.filter(x => x !== id) : [...prev, id])); if (error) setError(null); };

  const submit = () => {
    if (tab === 'upload') {
      if (!files.length) { setError('Add at least one photo to upload.'); return; }
      if (onUploadFiles) { onSubmit && onSubmit(onUploadFiles(files)); return; }
      const now = new Date().toISOString();
      onSubmit && onSubmit(files.map((f, i) => {
        const id = `pl-up-${Date.now()}-${i}`;
        const seed = apmSeed(id);
        if (window.PHOTOS) window.PHOTOS.unshift({ id, name: f.name, seed, date: now, createdAt: now, tagIds: [], personIds: [], w: 1, h: 1 });
        return { source: 'upload', photoId: id, name: f.name, seed, sizeBytes: f.sizeBytes };
      }));
      return;
    }
    if (!picked.length) { setError('Pick at least one photo.'); return; }
    const byId = Object.fromEntries(source.map(p => [p.id, p]));
    onSubmit && onSubmit(picked.map(id => ({ source: 'library', photoId: id, name: byId[id].name, seed: byId[id].seed, sizeBytes: null })));
  };

  const n = tab === 'upload' ? files.length : picked.length;
  const cta = uploadOnly ? (n > 1 ? `Upload ${n} photos` : 'Upload') : tab === 'upload' ? (n > 1 ? `Upload and add ${n}` : 'Upload and add') : (n > 1 ? `Add ${n} photos` : 'Add photo');

  return (
    <Modal title={uploadOnly ? 'Upload photos' : 'Add photos'} subtitle={subtitle} icon="add_photo_alternate" className="afm-dialog" onClose={onClose}
      footer={<React.Fragment>
        <Button variant="text" onClick={onClose}>Cancel</Button>
        <Button variant="filled" color="primary" icon={tab === 'upload' ? 'upload' : 'add'} disabled={tab === 'library' && n === 0} onClick={submit}>{cta}</Button>
      </React.Fragment>}>
      <div className="prop-doc-modal">
        {uploadOnly ? null : <SegmentedControl full ariaLabel="Where the photo comes from" value={tab} onChange={(v) => { setTab(v); setError(null); }} options={APM_TABS} />}
        {tab === 'upload' ? (
          <FileUpload accept="image/*" showKinds={false} files={files} error={error}
            hint={`JPEG, PNG, GIF, or WebP · up to ${((window.__odysseyImportLimits || {}).photo || (window.__odysseyImportLimits || {}).upload || 64)}\u00A0MB each · multiple at once`}
            onChange={(next) => { setFiles(next); if (error) setError(null); }} />
        ) : (
          <div className="prop-lib">
            <SearchField placeholder="Search photos by title, file name or place…" value={q} onChange={setQ} />
            <div className="odc-scroll" role="group" aria-label="Photos"
              style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(104px, 1fr))', gap: 8, maxHeight: 380, overflowY: 'auto', padding: 2 }}>
              {pool.length === 0 ? <EmptyLine>No photos match “{q}”.</EmptyLine> : pool.map(p => {
                const off = linked.has(p.id), on = picked.includes(p.id);
                return (
                  <button key={p.id} type="button" disabled={off} aria-pressed={on} title={p.title || p.name} onClick={() => toggle(p.id)}
                    style={{ position: 'relative', aspectRatio: '1', border: 0, padding: 0, borderRadius: 8, cursor: off ? 'not-allowed' : 'pointer', background: apmBg(p), opacity: off ? 0.45 : 1,
                      outline: on ? '2px solid var(--mud-palette-primary)' : 'none', outlineOffset: 2 }}>
                    <span style={{ position: 'absolute', top: 6, left: 6, display: 'grid', placeItems: 'center', width: 24, height: 24, borderRadius: 6, background: 'rgba(8,12,24,0.55)', color: '#fff' }}>
                      <MIcon name={off ? 'link' : on ? 'check_box' : 'check_box_outline_blank'} size={18} />
                    </span>
                    {off ? <span style={{ position: 'absolute', left: 6, right: 6, bottom: 6, padding: '3px 6px', borderRadius: 4, background: 'rgba(8,12,24,0.7)', color: '#fff', font: '500 11px/1.2 var(--font-sans)' }}>Already added</span> : null}
                  </button>
                );
              })}
            </div>
            {error ? <div className="helper aam-err" role="alert">{error}</div> : null}
          </div>
        )}
      </div>
    </Modal>
  );
};

Object.assign(window, { AttachPhotosModal, apmBg });
