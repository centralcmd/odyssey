/* Journal — /journal
   ----------------------------------------------------------------------------
   The shared, searchable narrative journal. Sibling of Subscriptions / Contracts
   on the expandable record-card scaffold: PageHeader + Overview + Search, an
   InfiniteList of entry cards (reverse-chron by EntryDate), expand-to-detail
   (full content, location, author + last-editor, tags, linked contacts,
   a JournalPhotoGallery, and an attachment list), inline edit, and a create
   dialog.

   Entries link contacts + files by id only; the client hydrates names.
   A dangling / no-access contact renders a text-labelled "Unavailable"
   chip (spec §11). Content is plain text, rendered escaped (React default).
   Seed + helpers from journal-data.js. */

const J_H = window.OdysseyHelpers;
const J_D = window.OdysseyData;

// Active journal tags → option list for the tag pickers/filters.
const JOURNAL_TAG_OPTIONS = () => J_D.journalTags.filter((t) => !t.archived).map((t) => ({ value: t.id, label: t.name }));
const JOURNAL_CP_OPTIONS = () => J_D.contacts.filter((c) => !c.archived).map((c) => ({ value: c.id, label: c.name }));

/* Atoms not bridged to the kit globals — read straight off the DS namespace. */
const { Menu: JMenu, Toast: JToast, ToastStack: JToastStack } = window.OdysseyDesignSystem_d5aa51 || {};

/* ================= iCalendar VJOURNAL (RFC 5545 §3.6.3) export + import sim (spec §5/§9) =================
   Export is real — each entry serializes to a VJOURNAL inside a VCALENDAR
   envelope with §3.1 line folding + text escaping, downloaded as text/calendar.
   Import is a simulated parse (the DS FileUpload abstracts the raw bytes, as in
   the ICS / VTODO / vCard precedents) that creates/updates by UID and returns a
   JournalEntryIcsImportResult the page applies + surfaces.

   X-ODYSSEY-CONTACT is emitted only when the caller holds contacts.read
   (spec §9/§10 item 2); X-ODYSSEY-LOCATION carries Location (not a valid VJOURNAL
   property under RFC 5545). */
const jExternalUid = (e) => e.externalUid || `urn:uuid:${e.id}`;
const icsEscape = (s) => String(s == null ? '' : s).replace(/\\/g, '\\\\').replace(/\n/g, '\\n').replace(/,/g, '\\,').replace(/;/g, '\\;');
const icsFoldLine = (line) => {
  if (line.length <= 75) return line;
  let out = line.slice(0, 75), rest = line.slice(75);
  while (rest.length) { out += '\r\n ' + rest.slice(0, 74); rest = rest.slice(74); }
  return out;
};
const icsUtcStamp = (iso) => { try { return new Date(iso).toISOString().replace(/[-:]/g, '').replace(/\.\d+/, ''); } catch (x) { return ''; } };

const buildVJournal = (e, opts = {}) => {
  const L = ['BEGIN:VJOURNAL'];
  L.push('UID:' + jExternalUid(e));
  L.push('DTSTAMP:' + icsUtcStamp(e.updatedAt || new Date().toISOString()));
  if (e.entryDate) L.push('DTSTART;VALUE=DATE:' + e.entryDate.slice(0, 10).replace(/-/g, ''));
  L.push('SUMMARY:' + icsEscape(e.title));
  if (e.content) L.push('DESCRIPTION:' + icsEscape(e.content));
  L.push('STATUS:' + (e.archived ? 'CANCELLED' : 'FINAL'));
  if (e.location) L.push('X-ODYSSEY-LOCATION:' + icsEscape(e.location));
  const tags = J_H.jEntryTags(e).map((t) => t.name);
  if (tags.length) L.push('CATEGORIES:' + tags.map(icsEscape).join(','));
  if (opts.includeContacts !== false) {
    J_H.jContacts(e).forEach((cp) => { const x = cp.externalUid || `urn:uuid:${cp.id}`; L.push('X-ODYSSEY-CONTACT:' + icsEscape(x)); });
  }
  (e.attachments || []).forEach((a) => { const id = a.id || a.fileId; if (id) L.push('ATTACH;VALUE=URI:odyssey-file:' + id); });
  (e.photos || []).forEach((p) => { const id = p.fileId || p.id; if (id) L.push('ATTACH;VALUE=URI:odyssey-photo:' + id); });
  L.push('END:VJOURNAL');
  return L.map(icsFoldLine).join('\r\n');
};
const buildJournalIcs = (list, opts) => {
  const head = ['BEGIN:VCALENDAR', 'VERSION:2.0', 'PRODID:-//Odyssey//Journal//EN', 'CALSCALE:GREGORIAN'];
  return [...head, ...list.map((e) => buildVJournal(e, opts)), 'END:VCALENDAR'].join('\r\n') + '\r\n';
};
const jStamp = () => { const d = new Date(); const p = (n) => String(n).padStart(2, '0'); return `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}-${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}Z`; };
const jDownloadIcs = (text, filename) => {
  const blob = new Blob([text], { type: 'text/calendar;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url; a.download = filename; document.body.appendChild(a); a.click();
  a.remove(); setTimeout(() => URL.revokeObjectURL(url), 1000);
};

/* ---- import simulation (see note above) ---- */
const VJ_TITLES = ['Site visit — north warehouse', 'Client kickoff notes', 'Quarterly review recap', 'Vendor dispute log', 'Renovation walkthrough', 'Board meeting minutes'];
let __vjSeq = 0;
const makeImportedEntries = (n) => {
  const now = new Date().toISOString();
  return Array.from({ length: n }, (_, i) => ({
    id: `je-imp-${Date.now()}-${i}`, externalUid: `urn:uuid:imported-${Date.now()}-${i}`,
    title: VJ_TITLES[(__vjSeq + i) % VJ_TITLES.length], content: 'Imported from an iCalendar VJOURNAL file.',
    entryDate: now.slice(0, 10) + 'T00:00:00Z', location: null, tagIds: [], contactIds: [],
    photos: [], attachments: [], createdBy: J_D.user.name, updatedBy: J_D.user.name, createdAt: now, updatedAt: now, archived: null,
  }));
};
const simulateJournalImport = (file, rows, outcome, canReadCp) => {
  if (outcome === 'rejected') return { rejected: 'This file has more than the 2,000-entry limit (MaxVJournals). Split it into smaller files and import each.' };
  if (/^odyssey-journal-entr/i.test(file.name || '')) {
    const ids = rows.map((r) => r.id);
    return { result: { importedCount: 0, updatedCount: ids.length, skipped: [], skippedTagLinkCount: 0, skippedContactLinkCount: 0, skippedAttachmentCount: 0, skippedPhotoCount: 0 }, createdRows: [], updatedIds: ids };
  }
  const created = makeImportedEntries(5); __vjSeq += 5;
  const updatedIds = rows.slice(0, 2).map((r) => r.id);
  const skipped = outcome === 'clean' ? [] : [
    { reason: 'Entry date (DTSTART) is required', count: 2, sampleTitles: ['(no date)', 'Untitled draft'] },
    { reason: 'Title (SUMMARY) is missing or over 200 characters', count: 1, sampleTitles: ['(no summary)'] },
    { reason: 'External ID already in use by another journal entry', count: 1, sampleTitles: ['Site visit — north warehouse'] },
  ];
  const soft = outcome === 'clean'
    ? { skippedTagLinkCount: 0, skippedContactLinkCount: 0, skippedAttachmentCount: 0, skippedPhotoCount: 0 }
    // Without contacts.read every X-ODYSSEY-CONTACT reference is skipped (spec §10 item 2).
    : { skippedTagLinkCount: 3, skippedContactLinkCount: canReadCp ? 1 : 4, skippedAttachmentCount: 1, skippedPhotoCount: 2 };
  return { result: { importedCount: created.length, updatedCount: updatedIds.length, skipped, ...soft }, createdRows: created, updatedIds };
};

// ---- FileUpload round-trip (shared impl on OdysseyHelpers) -----------------
const toUploadPhotos = (p) => J_H.toUploadPhotos(p);
const toUploadFiles = (a) => J_H.toUploadFiles(a);
const fromUploadPhotos = (f) => J_H.fromUploadPhotos(f);
const fromUploadFiles = (f, d) => J_H.fromUploadFiles(f, d, 'ja');

// Linked-contact chip — the shared DS ContactChip (hydrated name +
// type glyph, or a muted "Unavailable" chip for a since-deleted / no-access id).
// Late-bound from the DS namespace so it never depends on script load order.
const ContactChip = ({ cp }) => {
  const Cmp = (window.OdysseyDesignSystem_d5aa51 || {}).ContactChip;
  return Cmp ? <Cmp contact={cp} size="sm" /> : null;
};

// The attachment well — the card is always open, so the shared FilesTable has
// nowhere to live: a RecordTable's sortable headers and column rhythm don't
// survive at card width. The count in the footer IS the disclosure, and it
// unfolds every file in place (no InlinePager, by decision), each row carrying
// FilesTable's own field set. Kind visuals + the type vocabulary still come
// from the shared file registry, so a kind reads identically across surfaces.
// Decisions recorded in preview/explore-journal-card-a.html.
const JEC_FILE_FALLBACK = { icon: 'insert_drive_file', color: 'oklch(0.74 0.02 250)', soft: 'oklch(0.74 0.02 250 / 0.16)' };
const jecUploaded = (iso) => {
  if (!iso) return '';
  const d = new Date(`${iso}T00:00:00`);
  return isNaN(d) ? iso : d.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
};

const JournalFileRow = ({ f, onPreview, onRemove }) => {
  const fi = J_D.fileTypeByKey[f.kind] || JEC_FILE_FALLBACK;
  return (
    <div className="jec-file">
      <span className="odc-avatar" style={{ background: fi.soft, color: fi.color }}>
        <span className="material-icons" aria-hidden="true">{fi.icon}</span>
      </span>
      <span className="jec-file-name" title={f.name}>{f.name}</span>
      <span className="odc-chip" style={{ background: fi.soft, color: fi.color }}>{fi.label || f.kind}</span>
      <span className="jec-file-size">{f.size}</span>
      <span className="jec-file-up">{jecUploaded(f.uploaded)}</span>
      <span className="je-cardmenu">
        <ActionMenu items={[
          { icon: 'visibility', label: 'Preview', onClick: () => onPreview(f) },
          { icon: 'download', label: 'Download', onClick: () => J_H.downloadFile(f) },
          { icon: 'fingerprint', label: 'Copy ID', trailingIcon: 'content_copy', onClick: () => { if (navigator.clipboard) navigator.clipboard.writeText(f.id); } },
          ...(onRemove ? [{ divider: true }, { icon: 'link_off', label: 'Remove from entry', danger: true, onClick: () => onRemove(f) }] : []),
        ]} />
      </span>
    </div>
  );
};

/* ---------- Photo lightbox ----------
   Clicking a gallery tile opens a large view. The kit has no real bytes, so a
   photo renders as a big striped placeholder + filename; a real deployment would
   drop the image in `src`. Arrow keys / the chevrons page through the set. */
const PhotoLightbox = ({ photos, index, onClose, onIndex }) => {
  const { useEffect } = React;
  const p = photos[index];
  useEffect(() => {
    const onKey = (ev) => {
      if (ev.key === 'Escape') onClose();
      else if (ev.key === 'ArrowRight') onIndex((index + 1) % photos.length);
      else if (ev.key === 'ArrowLeft') onIndex((index - 1 + photos.length) % photos.length);
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [index, photos.length]);
  if (!p) return null;
  const many = photos.length > 1;
  return (
    <div className="je-lightbox" role="dialog" aria-modal="true" aria-label={`Photo ${p.name || p.id}`} onClick={onClose}>
      <button type="button" className="je-lightbox-close" aria-label="Close" onClick={onClose}>
        <MIcon name="close" size={22} />
      </button>
      {many && (
        <button type="button" className="je-lightbox-nav prev" aria-label="Previous photo"
          onClick={(ev) => { ev.stopPropagation(); onIndex((index - 1 + photos.length) % photos.length); }}>
          <MIcon name="chevron_left" size={28} />
        </button>
      )}
      <figure className="je-lightbox-stage" onClick={(ev) => ev.stopPropagation()}>
        {p.src ? (
          <img className="je-lightbox-img" src={p.src} alt={p.name || ''} />
        ) : (
          <span className="je-lightbox-ph" aria-hidden="true"><span className="mono">photo</span></span>
        )}
        <figcaption className="je-lightbox-cap">
          <span className="mono je-lightbox-name">{p.name || p.id}</span>
          {many && <span className="je-lightbox-count mono">{index + 1} / {photos.length}</span>}
        </figcaption>
      </figure>
      {many && (
        <button type="button" className="je-lightbox-nav next" aria-label="Next photo"
          onClick={(ev) => { ev.stopPropagation(); onIndex((index + 1) % photos.length); }}>
          <MIcon name="chevron_right" size={28} />
        </button>
      )}
    </div>
  );
};

/* ---------- The card's body ----------
   Was the expanded detail panel; A has no expand step, so this renders the
   card's content directly. Tag/contact unlink moved to the edit dialog with
   the tile grid it lived on. */
const JournalEntryBody = ({ e, onRemoveFile }) => {
  const { useState } = React;
  const tags = J_H.jEntryTags(e);
  const cps = J_H.jContacts(e);
  const galleryPhotos = (e.photos || []).map((p) => ({ id: p.id, photoId: p.photoId, fileId: p.fileId, name: p.name, src: p.src }));
  // Detail records for the shared Photos dialogs (window.PhotoDetailModal /
  // PhotoEditDialog). The kit has no real bytes, so a photo renders as a
  // deterministic gradient scene keyed by a stable seed hashed from its id
  // (same look as the library). Kept in local state so favourite / archive /
  // edit made from the journal are live for the session — exactly the Photos UX.
  const jPhotoSeed = (id) => { let h = 0; for (let i = 0; i < id.length; i++) h = (h * 31 + id.charCodeAt(i)) >>> 0; return h; };
  const author = (e.createdBy && (e.createdBy.name || e.createdBy)) || '\u2014';
  const [photoRecs, setPhotoRecs] = useState(() => galleryPhotos.map((p) => ({
    id: p.id, name: p.name, title: null, src: p.src, seed: jPhotoSeed(p.id),
    date: e.entryDate, location: e.location || null, lat: null, lng: null,
    caption: null, fav: false, archived: false, tagIds: [], personIds: [], albums: [],
    w: 1, h: 1, pxW: 640, pxH: 640, createdBy: author, updatedBy: null,
  })));
  const [viewIdx, setViewIdx] = useState(null);
  const [editId, setEditId] = useState(null);
  const PhotoView = window.PhotoDetailModal;
  const PhotoEdit = window.PhotoEditDialog;
  // Minimal library facade implementing just the methods the shared dialogs call.
  const photoLib = {
    albumList: [],
    toggleFav: (id) => setPhotoRecs((rs) => rs.map((r) => (r.id === id ? { ...r, fav: !r.fav } : r))),
    toggleArchive: (id) => setPhotoRecs((rs) => rs.map((r) => (r.id === id ? { ...r, archived: !r.archived } : r))),
    updatePhoto: (id, patch) => setPhotoRecs((rs) => rs.map((r) => (r.id === id ? { ...r, ...patch, updatedBy: author, updatedAt: new Date().toISOString() } : r))),
    addToAlbums: () => {}, removeFromAlbums: () => {},
  };
  const editRec = editId != null ? photoRecs.find((r) => r.id === editId) : null;
  const nPhotos = galleryPhotos.length;
  const nFiles = (e.attachments || []).length;
  const [filesOpen, setFilesOpen] = useState(false);
  const [previewFile, setPreviewFile] = useState(null);
  const FileViewer = window.FileViewerModal;
  const files = e.attachments || [];
  const listId = `jec-files-${e.id}`;
  return (
    <React.Fragment>
      {/* The author/location line, tucked under the title — the entry date is
          the rail, so it is not repeated here. */}
      <div className="jec-meta">
        <span><MIcon name="person" size={14} />{e.createdBy}</span>
        {e.location ? <span><MIcon name="place" size={14} />{e.location}</span> : null}
        {e.updatedAt && e.updatedAt !== e.createdAt ? (
          <span><MIcon name="history" size={14} />edited {J_H.jDateTime(e.updatedAt)}
            {e.updatedBy && e.updatedBy !== e.createdBy ? ` by ${e.updatedBy}` : ''}</span>
        ) : null}
        {e.archived ? (
          <span><span className="odc-chip sm outline archived">Archived</span>
            <span className="jec-archived-at">{J_H.jDateTime(e.archived)}</span></span>
        ) : null}
      </div>

      {/* The entry text in full — never clamped: this card IS the detail. */}
      {e.content ? <p className="jec-text">{e.content}</p> : null}

      {/* Photos: the shared JournalPhotoGallery, uncapped (P1). minTile={120}
          is the auto-fill grid the decision picked; the component keeps the
          `src` path, the filename caption and the list semantics, so a real
          deployment renders real images rather than placeholders. */}
      {nPhotos > 0 ? (
        <JournalPhotoGallery photos={galleryPhotos} title="" minTile={120}
          onOpen={(p) => setViewIdx(galleryPhotos.findIndex((x) => x.id === p.id))} />
      ) : null}

      {/* Footer: contacts and tags uncapped in the first track, the attachment
          count fixed in the second so a chip wrap can never displace it. */}
      {cps.length || tags.length || nFiles ? (
        <div className="jec-foot">
          <div className="jec-chips">
            {cps.map((cp) => <ContactChip key={cp.id} cp={cp} />)}
            {tags.map((t) => (
              <span className="odc-chip tag" key={t.id}><MIcon name="label" size={13} />{t.name}</span>
            ))}
          </div>
          {nFiles ? (
            <button type="button" className="jec-filecount" aria-expanded={filesOpen} aria-controls={listId}
              onClick={() => setFilesOpen((v) => !v)}>
              <MIcon name="attach_file" size={15} />
              <span className="jec-fcount">{nFiles}</span> {nFiles === 1 ? 'file' : 'files'}
              <MIcon name="expand_more" size={15} className="chev" />
            </button>
          ) : null}
        </div>
      ) : null}

      {filesOpen && nFiles ? (
        <div className="jec-files" id={listId}>
          {files.map((f) => (
            <JournalFileRow key={f.id} f={f} onPreview={setPreviewFile}
              onRemove={onRemoveFile ? (file) => onRemoveFile(file.id) : null} />
          ))}
        </div>
      ) : null}

      {previewFile && FileViewer && (
        <FileViewer file={previewFile} onClose={() => setPreviewFile(null)} />
      )}
      {viewIdx != null && PhotoView && (
        <PhotoView photos={photoRecs} index={viewIdx} lib={photoLib} contained={false}
          onIndex={setViewIdx} onClose={() => setViewIdx(null)}
          onEdit={(p) => { setViewIdx(null); setEditId(p.id); }} />
      )}
      {editRec && PhotoEdit && (
        <PhotoEdit photo={editRec} lib={photoLib} contained={false} onClose={() => setEditId(null)} />
      )}
    </React.Fragment>
  );
};

/* ---------- One entry card ----------
   Purpose-built, not the shared DS RecordCard: a journal entry has no headline
   figure and nothing worth collapsing, so the card is always open and IS the
   detail — no chevron, no open/onToggle, no counts row. The date is the card's
   anchor, carried as a calendar leaf down the left edge.
   Directions and decisions: preview/explore-journal-card-a.html. */
const JournalListItem = ({ row, highlight, onSave, onDelete, onExport }) => {
  const { useState, useRef, useEffect } = React;
  const [showEdit, setShowEdit] = useState(false);
  const cardRef = useRef(null);
  const e = row;

  const saveEdit = (patch) => { onSave(e.id, patch); setShowEdit(false); };
  const toggleArchive = () => onSave(e.id, { archived: e.archived ? null : new Date().toISOString() });
  const removeFile = (id) => onSave(e.id, { attachments: (e.attachments || []).filter((f) => f.id !== id) });

  // The date leaf's three lines, in the reader's local zone (entryDate is
  // stored UTC and date-only, so two entries on one date simply repeat it).
  const d = e.entryDate ? new Date(e.entryDate) : null;
  const leaf = d && !isNaN(d) ? {
    dow: d.toLocaleDateString('en-US', { weekday: 'short' }),
    day: d.toLocaleDateString('en-US', { day: 'numeric' }),
    mon: d.toLocaleDateString('en-US', { month: 'short', year: '2-digit' }),
  } : { dow: '', day: '—', mon: '' };

  useEffect(() => {
    if (!highlight || !cardRef.current) return;
    const el = cardRef.current;
    let scroller = el.parentElement;
    while (scroller && scroller !== document.body) {
      const oy = getComputedStyle(scroller).overflowY;
      if ((oy === 'auto' || oy === 'scroll') && scroller.scrollHeight > scroller.clientHeight) break;
      scroller = scroller.parentElement;
    }
    requestAnimationFrame(() => {
      if (scroller && scroller !== document.body) {
        const top = scroller.scrollTop + (el.getBoundingClientRect().top - scroller.getBoundingClientRect().top) - 24;
        scroller.scrollTo({ top, behavior: 'smooth' });
      }
    });
  }, [highlight]);

  return (
    <div ref={cardRef}>
      <article className={`jec-card${e.archived ? ' archived' : ''}${highlight ? ' highlight' : ''}`}>
        <div className="jec-leaf">
          <span className="jec-dow">{leaf.dow}</span>
          <span className="jec-day">{leaf.day}</span>
          <span className="jec-mon">{leaf.mon}</span>
        </div>
        <div className="jec-body">
          <div className="jec-head">
            <h3 className="jec-title">{e.title}</h3>
            <span className="je-cardmenu">
              <ActionMenu items={[
                { icon: 'edit', label: 'Edit entry', onClick: () => setShowEdit(true) },
                { icon: 'event_note', label: 'Export VJOURNAL', onClick: () => onExport && onExport(e) },
                { icon: 'fingerprint', label: 'Copy ID', trailingIcon: 'content_copy', onClick: () => { if (navigator.clipboard) navigator.clipboard.writeText(e.id); } },
                { divider: true },
                { icon: e.archived ? 'unarchive' : 'inventory_2', label: e.archived ? 'Unarchive' : 'Archive', onClick: toggleArchive },
                { icon: 'delete', label: 'Delete', danger: true, onClick: () => onDelete && onDelete(e.id) },
              ]} />
            </span>
          </div>
          <JournalEntryBody e={e} onRemoveFile={removeFile} />
        </div>
      </article>
      {showEdit && <AddJournalEntryModal entry={e} onClose={() => setShowEdit(false)} onSave={saveEdit} />}
    </div>
  );
};

/* ---------- Create / Edit dialog ---------- */
const AddJournalEntryModal = ({ onClose, onCreate, onSave, entry = null }) => {
  const { useState } = React;
  const editing = !!entry;
  const [draft, setDraft] = useState(editing ? {
    title: entry.title, content: entry.content, entryDate: (entry.entryDate || '').slice(0, 10),
    location: entry.location || '', tagIds: entry.tagIds || [], contactIds: entry.contactIds || [],
    photos: toUploadPhotos(entry.photos), attachments: toUploadFiles(entry.attachments),
  } : {
    title: '', content: '', entryDate: new Date().toISOString().slice(0, 10), location: '',
    tagIds: [], contactIds: [], photos: [], attachments: [],
  });
  const [errors, setErrors] = useState({});
  const set = (k) => (v) => { setDraft((d) => ({ ...d, [k]: v })); if (errors[k]) setErrors((x) => ({ ...x, [k]: undefined })); };

  const submit = () => {
    const next = {};
    if (!draft.title.trim()) next.title = 'Give the entry a title.';
    if (!draft.content.trim()) next.content = 'Write something for the entry.';
    if (!draft.entryDate) next.entryDate = 'Choose the entry date.';
    if (Object.keys(next).length) { setErrors(next); return; }
    const core = {
      title: draft.title.trim(), content: draft.content.trim(),
      entryDate: draft.entryDate + 'T00:00:00Z', location: draft.location.trim() || null,
      tagIds: draft.tagIds, contactIds: draft.contactIds,
      photos: fromUploadPhotos(draft.photos),
      attachments: fromUploadFiles(draft.attachments, draft.entryDate),
    };
    if (editing) {
      // Archived is managed from the row's action menu; parent merge preserves it.
      onSave && onSave({ ...core, updatedBy: J_D.user.name, updatedAt: new Date().toISOString() });
    } else {
      onCreate && onCreate(core);
    }
  };

  return (
    <Modal title={editing ? 'Edit entry' : 'New entry'} icon="menu_book" onClose={onClose} wide
      footer={<React.Fragment>
        <Button variant="text" onClick={onClose}>Cancel</Button>
        <Button variant="filled" color="primary" icon={editing ? 'check' : 'add'} onClick={submit}>
          {editing ? 'Save changes' : 'Create entry'}
        </Button>
      </React.Fragment>}>
      <div className="edit-grid je-create-grid">
        <div className="edit-wide"><Field label="Title" required value={draft.title} onChange={set('title')} error={errors.title} maxLength={200} autoFocus /></div>
        <DateField label="Entry date" required value={draft.entryDate} onChange={set('entryDate')} error={errors.entryDate} />
        <Field label="Location" value={draft.location} onChange={set('location')} placeholder="Where this happened" maxLength={300} />
        <TagMultiSelect label="Tags" value={draft.tagIds} onChange={set('tagIds')} options={JOURNAL_TAG_OPTIONS()} optional
          onCreate={(name) => J_D.createTag('journal', name)}
          createKinds={J_D.tagCreateKinds('journal')} />
        {/* Linked contacts create CONTACTS, so the rows are the contact types —
            the same two rows every contact picker offers. */}
        <TagMultiSelect label="Contacts" value={draft.contactIds} onChange={set('contactIds')} options={JOURNAL_CP_OPTIONS()} addLabel="Link contact" placeholder="No linked contacts" optional
          searchLabel="Search contacts" searchPlaceholder="Search or add a contact…" emptyText="No contacts match" noun="contact"
          createLabel="Add" onCreate={(name, kind) => J_D.contactOption(J_D.createContact(name, kind))}
          createKinds={(window.OdysseyDesignSystem_d5aa51 || {}).CONTACT_CREATE_KINDS} />
        <div className="edit-wide"><NoteField label="Content" required value={draft.content} onChange={set('content')} maxLength={4096} rows={6} error={errors.content} placeholder="What happened?" /></div>
        <FieldShell label="Photos" optional helper="JPEG, PNG, GIF, or WebP.">
          <FileUpload accept="image/*" showKinds={false} files={draft.photos} onChange={set('photos')} compact />
        </FieldShell>
        <FieldShell label="Attachments" optional helper="PDFs and documents.">
          <FileUpload files={draft.attachments} onChange={set('attachments')} compact />
        </FieldShell>
      </div>
    </Modal>
  );
};

/* ---------- Page ---------- */
const Journal = ({ tweaks = {}, onNavigate }) => {
  const { useState, useEffect, useMemo } = React;
  const DS = window.OdysseyDesignSystem_d5aa51 || {};

  const [q, setQ] = useState('');
  const [debouncedQ, setDebouncedQ] = useState('');
  const [tagFilter, setTagFilter] = useState([]);
  const [cpFilter, setCpFilter] = useState([]);
  const [mediaFilter, setMediaFilter] = useState([]); // 'photos' | 'files'
  const [dateRange, setDateRange] = useState({ from: null, to: null });
  const [statusFilter, setStatusFilter] = useState([]); // 'active' | 'archived'
  const [adding, setAdding] = useState(false);
  const [rows, setRows] = useState(J_D.journalEntries);
  const [sort, setSort] = useState({ key: 'entryDate', dir: 'desc' });
  const [batch, setBatch] = useState(25);
  const [importOpen, setImportOpen] = useState(false);
  const [exporting, setExporting] = useState(false);
  const [toast, setToast] = useState(null);
  const canImport = tweaks.jCanImport !== false;      // journal.create AND journal.update
  const canReadCp = tweaks.jCanReadCp !== false;      // contacts.read (§10 item 2)
  const pushToast = (severity, message) => setToast({ severity, message, k: Date.now() });

  useEffect(() => { const t = setTimeout(() => setDebouncedQ(q.trim()), 300); return () => clearTimeout(t); }, [q]);

  const createEntry = (dto) => {
    const now = new Date().toISOString();
    setRows((prev) => [{ id: `je-${Date.now()}`, createdBy: J_D.user.name, updatedBy: J_D.user.name, createdAt: now, updatedAt: now, archived: null, ...dto }, ...prev]);
    setAdding(false);
  };
  const onSave = (id, patch) => setRows((prev) => prev.map((e) => (e.id === id ? { ...e, ...patch } : e)));
  const onDelete = (id) => setRows((prev) => prev.filter((e) => e.id !== id));

  const sortFields = [
    { key: 'entryDate', label: 'Entry date', type: 'date', sortValue: (e) => e.entryDate || null },
    { key: 'title', label: 'Title', type: 'text', sortValue: (e) => (e.title || '').toLowerCase() },
    { key: 'createdAt', label: 'Created', type: 'date', sortValue: (e) => e.createdAt || null },
  ];

  const filtered = useMemo(() => rows.filter((e) => {
    const st = e.archived ? 'archived' : 'active';
    if (statusFilter.length && !statusFilter.includes(st)) return false;
    else if (!statusFilter.length && e.archived) return false; // hide archived by default
    if (tagFilter.length && !(e.tagIds || []).some((t) => tagFilter.includes(t))) return false;
    if (cpFilter.length && !(e.contactIds || []).some((c) => cpFilter.includes(c))) return false;
    // Media flags are AND-ed: picking both wants entries carrying both.
    if (mediaFilter.includes('photos') && !(e.photos || []).length) return false;
    if (mediaFilter.includes('files') && !(e.attachments || []).length) return false;
    // Entry-date range, inclusive on both ends (dates only, stored UTC).
    const day = (e.entryDate || '').slice(0, 10);
    if (dateRange.from && (!day || day < dateRange.from)) return false;
    if (dateRange.to && (!day || day > dateRange.to)) return false;
    if (debouncedQ) {
      const hay = `${e.title} ${e.content} ${e.location || ''}`.toLowerCase();
      if (!hay.includes(debouncedQ.toLowerCase())) return false;
    }
    return true;
  }), [rows, statusFilter, tagFilter, cpFilter, mediaFilter, dateRange, debouncedQ]);

  const sortedRows = DS.SortHelpers ? DS.SortHelpers.sortRows(filtered, sortFields, sort, (e) => e.id) : filtered;

  const active = rows.filter((e) => !e.archived);
  const photoCount = active.reduce((n, e) => n + (e.photos ? e.photos.length : 0), 0);
  const hasFilters = !!(debouncedQ || tagFilter.length || statusFilter.length || cpFilter.length || mediaFilter.length || dateRange.from || dateRange.to);
  const clearFilters = () => { setQ(''); setTagFilter([]); setStatusFilter([]); setCpFilter([]); setMediaFilter([]); setDateRange({ from: null, to: null }); };

  // Per-entry export (action menu, spec §3/§7.1) — a single-VJOURNAL .ics.
  // Filename is always the timestamp form, never the entry title (spec §3 #2).
  const exportEntry = (e) => {
    const fname = `odyssey-journal-entry-${jStamp()}.ics`;
    jDownloadIcs(buildJournalIcs([e], { includeContacts: canReadCp }), fname);
    pushToast('success', `Exported ${fname}`);
  };
  // Page-level export (spec §7.2): 'all' = every status incl. archived; 'filtered'
  // = the current search/tag/status set (the `filtered` memo already applies the
  // page's neither-selected → Active default, spec §30).
  const doExport = (scope) => {
    if (exporting) return;
    if (tweaks.jExportCap) { pushToast('error', 'Too many entries matched — narrow your filters and try again.'); return; }
    setExporting(true);
    setTimeout(() => {
      const set = scope === 'filtered' ? filtered : rows;
      const fname = scope === 'filtered' ? `odyssey-journal-entries-filtered-${jStamp()}.ics` : `odyssey-journal-entries-${jStamp()}.ics`;
      jDownloadIcs(buildJournalIcs(set, { includeContacts: canReadCp }), fname);
      setExporting(false);
      pushToast('success', `Exported ${set.length} ${set.length === 1 ? 'entry' : 'entries'}.`);
    }, 700);
  };
  // Import (spec §7.3): apply created rows + touch updated rows, hand the result
  // back to the dialog to render its summary.
  const runImport = (file) => {
    const sim = simulateJournalImport(file, rows, tweaks.jImportOutcome || 'skips', canReadCp);
    if (sim.rejected) return { rejected: sim.rejected };
    const now = new Date().toISOString();
    const upd = new Set(sim.updatedIds || []);
    setRows((prev) => [...(sim.createdRows || []), ...prev.map((e) => upd.has(e.id) ? { ...e, updatedAt: now } : e)]);
    return { result: sim.result };
  };

  // "By tag" breakdown over active entries.
  const tagRows = J_D.journalTags.filter((t) => !t.archived).map((t) => ({
    key: t.id, icon: 'label', iconColor: 'var(--tag-text)', label: t.name,
    count: active.filter((e) => (e.tagIds || []).includes(t.id)).length,
  })).filter((r) => r.count > 0);

  return (
    <div className="col gap-6">
      <PageHeader
        title="Journal"
        icon="menu_book"
        sub={`${active.length} ${active.length === 1 ? 'entry' : 'entries'} · ${photoCount} ${photoCount === 1 ? 'photo' : 'photos'}`}
        overview={(
          <div className="je-overview">
            <div className="je-stat-tiles">
              <InfoTile icon="edit_note" iconColor="var(--tag-text)" label="Entries" value={String(active.length)} foot="not archived" />
              <InfoTile icon="photo_library" iconColor="var(--sea-400)" label="Photos" value={String(photoCount)} foot="across all entries" />
            </div>
            <BreakdownTile label="By tag" empty="No tagged entries."
              rows={tagRows} />
          </div>
        )}
        overviewDefaultOpen
        searchDefaultOpen
        search={(
          <div className="row gap-3 acct-filter-bar" style={{ flexWrap: 'wrap' }}>
            <div style={{ minWidth: 280, flex: 1 }}>
              <SearchField placeholder="Search title, content, location…" value={q} onChange={setQ} />
            </div>
            <div style={{ minWidth: 170 }}>
              <MultiSelect allLabel="Any tag" value={tagFilter} onChange={setTagFilter}
                options={JOURNAL_TAG_OPTIONS()} />
            </div>
            <div style={{ minWidth: 180 }}>
              <MultiSelect allLabel="Any contact" value={cpFilter} onChange={setCpFilter}
                options={JOURNAL_CP_OPTIONS()} />
            </div>
            <div style={{ minWidth: 170 }}>
              <MultiSelect allLabel="Any attachment" value={mediaFilter} onChange={setMediaFilter}
                options={[{ value: 'photos', label: 'Has photos' }, { value: 'files', label: 'Has files' }]} />
            </div>
            <DateRangePicker label="Entry date" icon="event" ariaLabel="Filter by entry date"
              value={dateRange} onChange={(r) => setDateRange({ from: r.from || null, to: r.to || null })} />
            <div style={{ minWidth: 160 }}>
              <MultiSelect allLabel="Active" value={statusFilter} onChange={setStatusFilter}
                options={[{ value: 'active', label: 'Active' }, { value: 'archived', label: 'Archived' }]} />
            </div>
            <SortSelect sort={sort} onSort={setSort} fields={sortFields} />
            <PageSizeSelect prefix="Load" suffix="at a time" label="Entries per batch"
              value={batch} onChange={setBatch} options={[25, 50, 100]} />
          </div>
        )}
        primary={{ label: 'New entry', icon: 'add', onClick: () => setAdding(true) }}
        menu={[
          { icon: 'event_note', label: 'Export all as iCalendar', onClick: () => doExport('all') },
          { icon: 'filter_list', label: `Export filtered (${filtered.length}) as iCalendar`, onClick: () => doExport('filtered') },
          ...(canImport ? [{ divider: true }, { icon: 'upload_file', label: 'Import from iCalendar…', onClick: () => setImportOpen(true) }] : []),
        ]}
      />

      {importOpen && <ImportJournalEntriesModal onClose={() => setImportOpen(false)} onImport={runImport} />}

      {adding && <AddJournalEntryModal onClose={() => setAdding(false)} onCreate={createEntry} />}

      {rows.length === 0 ? (
        <EmptyState
          icon="menu_book"
          title="No entries yet"
          description="Keep a running, searchable record next to your finances — what happened, where, who it involved, with photos and files attached."
          action={<Button variant="filled" color="primary" icon="add" onClick={() => setAdding(true)}>Write your first entry</Button>}
        />
      ) : (
        <div className="acct-list">
          <InfiniteList
            items={sortedRows}
            batchSize={batch}
            itemKey={(e) => e.id}
            noun="entries"
            renderItem={(e) => (
              <JournalListItem row={e} onSave={onSave} onDelete={onDelete} onExport={exportEntry} />
            )}
            empty={(
              <div className="empty-line" style={{ textAlign: 'center', padding: 48 }}>
                {hasFilters
                  ? <React.Fragment>No entries match your filters. <button className="link-btn" onClick={clearFilters}>Clear filters</button></React.Fragment>
                  : 'No entries to show.'}
              </div>
            )}
            trailing={(
              <AddRow title="New entry" sub="Title, content, an entry date, and any photos, files, tags, or contacts."
                onClick={() => setAdding(true)} />
            )}
          />
        </div>
      )}
      {toast && JToast && JToastStack && (
        <JToastStack>
          <JToast key={toast.k} severity={toast.severity} duration={4200} onClose={() => setToast(null)} message={toast.message} />
        </JToastStack>
      )}
    </div>
  );
};

Object.assign(window, { Journal, JournalListItem, AddJournalEntryModal });
