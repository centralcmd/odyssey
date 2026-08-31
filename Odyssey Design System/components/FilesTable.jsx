/**
 * Odyssey DS — FilesTable
 * THE files surface — the attachments table rendered by the Accounts detail,
 * the Transactions detail/edit panels, and the Files page. A PRESET of
 * RecordTable: it inherits the record-row lifecycle (sortable headers, Saved
 * flash, overflow menu) and only fixes the file-specific parts — columns and
 * the Edit-file dialog. Rows don't expand: every field is a column. Columns: kind avatar · Name · Type · Size · Uploaded ·
 * actions.
 *
 * Data-prop driven — rows are plain objects, nothing global:
 *   { id, name, kind, size, uploaded,
 *     validFrom?, validTo?, issuedAt?, issuedBy?, statusBadge? }
 *   • `size` is a preformatted display string ("1.2 MB"); pass `formatSize`
 *     to render something else
 *   • `uploaded` is an ISO date; `formatDate` overrides the default long form
 *   • the four optional validity fields (ISO dates, plus an `issuedBy`
 *     contact id) describe document validity — a policy/warranty period,
 *     the issue date, and the issuing institution. They surface in the detail
 *     well and the inline editor only when present / when `issuers` is given.
 *   • `statusBadge` ({ text, tone?, icon?, ariaLabel? }) is an optional
 *     additive indicator rendered as an OdsChip next to the file name — e.g. a
 *     "Review pending · 12" hint for a file with an open, resumable analysis
 *     review. Meaning lives in the text; absent rows render exactly as before.
 *
 * `typeFor(file)` resolves each row's file-kind visuals — `{ icon, color,
 * soft }` (the registry shape of `OdysseyData.fileTypeByKey` /
 * ACCOUNT_FILE_TYPES) — so the kind reads identically here, in the upload
 * picker and in the account detail. Unknown kinds fall back to a neutral
 * document glyph.
 *
 * Row lifecycle (see the RecordTable anatomy card):
 *   • Edit (menu) → the standard DS Modal for name + document type (plus the
 *     validity dates + issuing contact when `issuers` is supplied); Save
 *     commits via `onSave(id, patch)` and flashes "Saved" on the row. Give
 *     `onSave` to enable; omit it for a read-only surface. `kinds` feeds the
 *     type picker (default: the canonical ACCOUNT_FILE_TYPES registry).
 *     `issuerFor(file)` resolves an `issuedBy` id to a display name.
 *   • `actions(file)` supplies the file-specific menu items — Preview /
 *     Download / Analyze / Copy ID — slotted between Edit and Delete per the
 *     menu convention. "Preview" opens the document (FileViewerModal).
 *     Host any modals OUTSIDE the table.
 *   • `onDelete(file)` appends the danger Delete item after a divider.
 *
 * Sorting defaults to Uploaded, newest first — uncontrolled unless the host
 * binds `sort` ({key,dir}) + `onSortChange` (forwarded to RecordTable), which
 * keeps the headers in sync with a toolbar SortSelect. Column sortTypes:
 * name→text · kind→status · size→number · uploaded→date. `validityColumns`
 * appends the read-only document-validity set (Valid from · Valid to · Issued ·
 * Issued by, resolved through `issuerFor`) on surfaces that track it. No
 * pagination — the MVP renders the filtered list whole. Styled by the kit's
 * `.ua-tbl` classes — identical to every RecordTable page.
 *
 * Bundle components can't import each other, so this reads RecordTable and
 * the panel atoms off the DS namespace at render time (the same way the kit
 * consumes every atom).
 */

const FT_FALLBACK = { icon: 'insert_drive_file', color: 'oklch(0.74 0.02 250)', soft: 'oklch(0.74 0.02 250 / 0.16)' };

const ftDate = (iso) => {
  const d = new Date(`${iso}T00:00:00`);
  return isNaN(d) ? iso : d.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
};

/* Compact form for the validity columns — four extra date columns can't each
   carry a long-form date without overflowing the row. */
const ftShortDate = (iso) => {
  if (!iso) return '—';
  const d = new Date(`${iso}T00:00:00`);
  return isNaN(d) ? iso : d.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: '2-digit' });
};

const ftKindChip = (f, fi) => (
  <span className="odc-chip" style={{ background: fi.soft, color: fi.color }}>{fi.label || f.kind}</span>
);

/* ---- A labeled DatePicker, matching the kit's `.field` shape (the DS
   DatePicker itself carries no label). Used by the file-validity editor. ---- */
function FTDateField({ label, value, onChange, min, max }) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const DatePicker = NS.DatePicker;
  if (!DatePicker) return null;
  return (
    <div className="field">
      <div className="label">{label}</div>
      <DatePicker value={value || null} onChange={onChange} min={min} max={max} full />
    </div>
  );
}

/* ---- Edit dialog: file name + document type, plus the document validity
   metadata (valid-from / valid-to period, issue date, issuing contact) on
   surfaces that track it — i.e. when `issuers` is supplied. File bytes are
   immutable; you replace a file by re-uploading. Uses the standard DS Modal so
   the surface matches every other create/edit dialog in the kit. ---- */
function FTEditModal({ f, kinds, issuers, onSave, onClose }) {
  const { useState } = React;
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { Field, Button, Modal, TypeSelect, AccountFileTypeSelect, Select } = NS;
  const [name, setName] = useState(f.name);
  const [kind, setKind] = useState(f.kind);
  const [validFrom, setValidFrom] = useState(f.validFrom || null);
  const [validTo, setValidTo] = useState(f.validTo || null);
  const [issuedAt, setIssuedAt] = useState(f.issuedAt || null);
  const [issuedBy, setIssuedBy] = useState(f.issuedBy || '');
  const [touched, setTouched] = useState(false);
  if (!Field || !Button || !Modal) return null;
  const valid = name.trim().length > 0;
  const rangeBad = !!(validFrom && validTo && validTo < validFrom);
  const showValidity = Array.isArray(issuers);
  const submit = () => {
    setTouched(true);
    if (!valid || rangeBad) return;
    onSave(showValidity
      ? { name: name.trim(), kind,
          validFrom: validFrom || null, validTo: validTo || null,
          issuedAt: issuedAt || null, issuedBy: issuedBy || null }
      : { name: name.trim(), kind });
  };
  return (
    <Modal
      title="Edit file"
      subtitle={f.name}
      icon="edit"
      onClose={onClose}
      footer={
        <React.Fragment>
          <Button variant="text" onClick={onClose}>Cancel</Button>
          <Button variant="filled" color="primary" icon="check" onClick={submit}>Save changes</Button>
        </React.Fragment>
      }>
      <Field label="File name" value={name} required autoFocus
        onChange={(v) => { setName(v); setTouched(true); }}
        error={touched && !valid ? 'File name is required.' : undefined} />
      {/* Vocabulary-driven: renders whatever `kinds` registry the surface
          supplies (transaction / account / policy / tax file types) through
          the same TypeSelect engine the upload picker uses, so the control is
          identical everywhere. Falls back to the account-typed wrapper only if
          no kinds were provided. */}
      {TypeSelect && kinds ? (
        <TypeSelect label="Document type" value={kind} types={kinds}
          placeholder="Select type…" onChange={(k) => setKind(k)} />
      ) : AccountFileTypeSelect ? (
        <AccountFileTypeSelect label="Document type" value={kind} types={kinds}
          onChange={(k) => setKind(k)} />
      ) : null}
      {showValidity && (
        <React.Fragment>
          <FTDateField label="Valid from" value={validFrom} onChange={setValidFrom} max={validTo || undefined} />
          <FTDateField label="Valid to" value={validTo} onChange={setValidTo} min={validFrom || undefined} />
          {rangeBad && (
            <div className="helper" style={{ color: 'var(--mud-palette-error)' }}>
              “Valid to” can’t be before “Valid from”.
            </div>
          )}
          <FTDateField label="Issued" value={issuedAt} onChange={setIssuedAt} />
          {Select && (
            <Select label="Issued by" value={issuedBy} onChange={setIssuedBy}
              placeholder="Select issuer…" options={issuers} />
          )}
        </React.Fragment>
      )}
    </Modal>
  );
}

export function FilesTable({
  files = [],
  typeFor,
  actions,
  onSave,
  kinds,
  issuerFor,
  issuers,
  onDelete,
  formatDate = ftDate,
  formatSize,
  validityColumns = false,
  defaultSort = { key: 'uploaded', dir: 'desc' },
  sort,
  onSortChange,
  empty,
  ariaLabel,
  className = '',
}) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { RecordTable, Chip } = NS;
  const { useState } = React;
  // Editing runs in the standard DS Modal (name + document type only), so the
  // table owns the open file and the post-save flash itself.
  const [editFile, setEditFile] = useState(null);
  const [savedId, setSavedId] = useState(null);
  if (!RecordTable) return null;

  const kindOf = (f) => (typeFor && typeFor(f)) || FT_FALLBACK;
  const sizeText = (f) => (formatSize ? formatSize(f) : f.size);
  const byId = {};
  files.forEach((f) => { byId[f.id] = f; });

  const defaultExtra = (f) => [
    { icon: 'fingerprint', label: 'Copy ID', trailingIcon: 'content_copy', onClick: () => { if (navigator.clipboard) navigator.clipboard.writeText(f.id); } },
  ];

  return (
    <React.Fragment>
    <RecordTable
      rows={files}
      rowKey={(f) => f.id}
      ariaLabel={ariaLabel}
      defaultSort={defaultSort}
      sort={sort}
      onSortChange={onSortChange}
      className={className}
      leading={(f) => {
        const fi = kindOf(f);
        // Decorative — the file kind is already in the Type column, so the
        // glyph is aria-hidden and the wrapper carries no img role.
        return (
          <span className="odc-avatar" style={{ background: fi.soft, color: fi.color }}>
            <span className="material-icons" aria-hidden="true">{fi.icon}</span>
          </span>
        );
      }}
      columns={[
        { key: 'name', header: 'Name', sortable: true, sortType: 'text', sortValue: (f) => (f.name || '').toLowerCase(),
          cell: (f, ctx) => {
            // Optional additive status indicator (e.g. "Review pending · 12").
            // Carried as text in an OdsChip — meaning never icon/colour alone;
            // any icon is decorative. Absent on rows without a badge, so the
            // table renders exactly as before.
            const sb = f.statusBadge;
            return (
              <span style={{ display: 'inline-flex', alignItems: 'center', gap: 8 }}>
                <span>{f.name}</span>
                {(ctx.justSaved || savedId === f.id) && Chip && <Chip tone="income" dot>Saved</Chip>}
                {sb && Chip && (
                  <Chip tone={sb.tone || 'pending'} icon={sb.icon} dot={!sb.icon}>
                    <span aria-label={sb.ariaLabel || undefined}>{sb.text}</span>
                  </Chip>
                )}
              </span>
            );
          } },
        { key: 'kind', header: 'Type', sortable: true, sortType: 'status', sortValue: (f) => (f.kind || '').toLowerCase(),
          cell: (f) => ftKindChip(f, kindOf(f)) },
        { key: 'size', header: 'Size', sortable: true, sortType: 'number', align: 'right', className: 'mono muted',
          sortValue: (f) => parseFloat(f.size) || 0, cell: (f) => sizeText(f) },
        { key: 'uploaded', header: 'Uploaded', sortable: true, sortType: 'date', align: 'right', className: 'muted',
          sortValue: (f) => f.uploaded || '', cell: (f) => formatDate(f.uploaded) },
        // Document-validity metadata (opt-in — only surfaces that track it,
        // e.g. account files). Read-only: recorded at upload, never edited here.
        ...(validityColumns ? [
          { key: 'validFrom', header: 'Valid from', sortable: true, sortType: 'date', align: 'right', className: 'muted',
            sortValue: (f) => f.validFrom || '', cell: (f) => ftShortDate(f.validFrom) },
          { key: 'validTo', header: 'Valid to', sortable: true, sortType: 'date', align: 'right', className: 'muted',
            sortValue: (f) => f.validTo || '', cell: (f) => ftShortDate(f.validTo) },
          { key: 'issuedAt', header: 'Issued', sortable: true, sortType: 'date', align: 'right', className: 'muted',
            sortValue: (f) => f.issuedAt || '', cell: (f) => ftShortDate(f.issuedAt) },
          { key: 'issuedBy', header: 'Issued by', sortable: true, sortType: 'text', className: 'muted',
            sortValue: (f) => ((f.issuedBy && issuerFor && issuerFor(f)) || '').toLowerCase(),
            cell: (f) => (f.issuedBy && issuerFor && issuerFor(f)) || '—' },
        ] : []),
      ]}
      actions={(f, ctx) => [
        ...(onSave ? [{ icon: 'edit', label: 'Edit', onClick: () => setEditFile(f) }] : []),
        ...((actions || defaultExtra)(f)),
        ...(onDelete ? [{ divider: true }, { icon: 'delete', label: 'Delete', danger: true, onClick: ctx.remove }] : []),
      ]}
      onSave={onSave}
      onDelete={onDelete ? (id) => onDelete(byId[id] || id) : undefined}
      empty={empty ? <div className="muted" style={{ textAlign: 'center', padding: 48 }}>{empty}</div> : undefined}
    />
    {editFile && (
      <FTEditModal
        f={editFile}
        kinds={kinds}
        issuers={issuers}
        onClose={() => setEditFile(null)}
        onSave={(patch) => {
          const id = editFile.id;
          onSave && onSave(id, patch);
          setEditFile(null);
          setSavedId(id);
          setTimeout(() => setSavedId((curr) => (curr === id ? null : curr)), 2200);
        }} />
    )}
    </React.Fragment>
  );
}
