/**
 * Odyssey DS — FilesTable
 * THE files surface — the attachments table rendered by the Accounts detail,
 * the Transactions detail/edit panels, and the Files page. A PRESET of
 * RecordTable: it inherits the full record-row lifecycle (sortable headers,
 * click-to-expand detail, inline Edit panel, Saved flash, overflow menu) and
 * only fixes the file-specific parts — columns, the detail grid, and the
 * Edit-file panel. Columns: kind avatar · Name · Type · Size · Uploaded ·
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
 *   • click a row (or "View details") → read-only MetaTile detail
 *     (File name · Document type · Size · Uploaded — plus a Valid from / Valid
 *     to / Issued / Issued by well when the file carries validity metadata)
 *   • Edit (menu) → inline edit panel for name + document type (and, when
 *     `issuers` is supplied, the validity dates + issuing contact); Save
 *     commits via `onSave(id, patch)` and flashes "Saved". Give `onSave` to
 *     enable; omit it for a read-only surface. `kinds` feeds the type picker
 *     (default: the canonical ACCOUNT_FILE_TYPES registry). `issuerFor(file)`
 *     resolves an `issuedBy` id to a display name for the detail well.
 *   • `actions(file)` supplies the file-specific menu items — Preview /
 *     Download / Analyze / Copy ID — slotted between Edit and Delete per the
 *     menu convention. "Preview" opens the document (FileViewerModal);
 *     "View details" expands the record. Host any modals OUTSIDE the table.
 *   • `onDelete(file)` appends the danger Delete item after a divider.
 *
 * Sorting defaults to Uploaded, newest first — uncontrolled unless the host
 * binds `sort` ({key,dir}) + `onSortChange` (forwarded to RecordTable), which
 * keeps the headers in sync with a toolbar SortSelect. Column sortTypes:
 * name→text · kind→status · size→number · uploaded→date. No pagination —
 * the MVP renders the filtered list whole. Styled by the kit's `.ua-tbl` /
 * `.acct-detail` / `.meta-grid` classes — identical to every RecordTable page.
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

const ftKindChip = (f, fi) => (
  <span className="odc-chip" style={{ background: fi.soft, color: fi.color }}>{fi.label || f.kind}</span>
);

/* ---- Expanded detail (view mode): the file record as a MetaTile grid ---- */
function FTDetail({ f, fi, sizeText, dateText, issuerFor, formatDate }) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const MetaTile = NS.MetaTile;
  if (!MetaTile) return null;
  const fmt = (iso) => (iso ? (formatDate ? formatDate(iso) : iso) : '—');
  // The validity well only appears when the file actually carries any of the
  // optional join-entity metadata (ValidFrom / ValidTo / IssuedAt / IssuedBy).
  const hasValidity = f.validFrom || f.validTo || f.issuedAt || f.issuedBy;
  const issuerName = f.issuedBy && issuerFor ? issuerFor(f) : null;
  return (
    <div className="acct-detail">
      <div className="meta-grid">
        <MetaTile label="File name" value={f.name} />
        <MetaTile label="Document type" value={ftKindChip(f, fi)} />
        <MetaTile label="Size" value={sizeText} mono />
        <MetaTile label="Uploaded" value={dateText} />
      </div>
      {hasValidity && (
        <div className="meta-grid" style={{ marginTop: 12 }}>
          <MetaTile label="Valid from" value={fmt(f.validFrom)} />
          <MetaTile label="Valid to" value={fmt(f.validTo)} />
          <MetaTile label="Issued" value={fmt(f.issuedAt)} />
          <MetaTile label="Issued by" value={issuerName || '—'} />
        </div>
      )}
    </div>
  );
}

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

/* ---- Inline edit panel: name + document type, plus the optional document
   validity metadata (valid-from / valid-to period, issue date, and the issuing
   contact). File bytes are immutable — you replace a file by re-uploading.
   The validity row appears only when `issuers` (the contact options) is
   supplied, i.e. on account-file surfaces. ---- */
function FTEdit({ f, kinds, issuers, onSave, onCancel }) {
  const { useState } = React;
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { Field, Button, TypeSelect, AccountFileTypeSelect, Select } = NS;
  const [name, setName] = useState(f.name);
  const [kind, setKind] = useState(f.kind);
  const [validFrom, setValidFrom] = useState(f.validFrom || null);
  const [validTo, setValidTo] = useState(f.validTo || null);
  const [issuedAt, setIssuedAt] = useState(f.issuedAt || null);
  const [issuedBy, setIssuedBy] = useState(f.issuedBy || '');
  if (!Field || !Button) return null;
  const valid = name.trim().length > 0;
  const rangeBad = validFrom && validTo && validTo < validFrom;
  const showValidity = Array.isArray(issuers);
  return (
    <div className="acct-detail acct-edit">
      <div className="acct-edit-head"><span className="material-icons" aria-hidden="true">edit</span><span>Edit file — {f.name}</span></div>
      <div className="edit-grid">
        <div className="edit-wide">
          <Field label="File name" value={name} required
            onChange={setName}
            error={valid ? undefined : 'File name is required.'} />
        </div>
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
      </div>
      {showValidity && (
        <div className="edit-grid" style={{ marginTop: 12 }}>
          <FTDateField label="Valid from" value={validFrom} onChange={setValidFrom} max={validTo || undefined} />
          <FTDateField label="Valid to" value={validTo} onChange={setValidTo} min={validFrom || undefined} />
          <FTDateField label="Issued" value={issuedAt} onChange={setIssuedAt} />
          {Select && (
            <Select label="Issued by" value={issuedBy} onChange={setIssuedBy}
              placeholder="Select issuer…" options={issuers} />
          )}
          {rangeBad && (
            <div className="edit-wide helper" style={{ color: 'var(--mud-palette-error)' }}>
              “Valid to” can’t be before “Valid from”.
            </div>
          )}
        </div>
      )}
      <div className="acct-edit-actions">
        <Button variant="text" onClick={onCancel}>Cancel</Button>
        <Button variant="filled" color="primary" icon="check" disabled={!valid || rangeBad}
          onClick={() => valid && !rangeBad && onSave({
            name: name.trim(), kind,
            validFrom: validFrom || null, validTo: validTo || null,
            issuedAt: issuedAt || null, issuedBy: issuedBy || null,
          })}>Save changes</Button>
      </div>
    </div>
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
  defaultSort = { key: 'uploaded', dir: 'desc' },
  sort,
  onSortChange,
  empty,
  ariaLabel,
  className = '',
}) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { RecordTable, Chip } = NS;
  if (!RecordTable) return null;

  const kindOf = (f) => (typeFor && typeFor(f)) || FT_FALLBACK;
  const sizeText = (f) => (formatSize ? formatSize(f) : f.size);
  const byId = {};
  files.forEach((f) => { byId[f.id] = f; });

  const defaultExtra = (f) => [
    { icon: 'fingerprint', label: 'Copy ID', trailingIcon: 'content_copy', onClick: () => { if (navigator.clipboard) navigator.clipboard.writeText(f.id); } },
  ];

  return (
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
                {ctx.justSaved && Chip && <Chip tone="income" dot>Saved</Chip>}
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
      ]}
      actions={(f, ctx) => [
        ...(ctx.editing ? [] : [{ icon: ctx.expanded ? 'close' : 'expand_more', label: ctx.expanded ? 'Collapse' : 'View details', onClick: ctx.toggle }]),
        ...(onSave ? [{ icon: 'edit', label: 'Edit', onClick: ctx.startEdit }] : []),
        ...((actions || defaultExtra)(f)),
        ...(onDelete ? [{ divider: true }, { icon: 'delete', label: 'Delete', danger: true, onClick: ctx.remove }] : []),
      ]}
      renderDetail={(f) => <FTDetail f={f} fi={kindOf(f)} sizeText={sizeText(f)} dateText={formatDate(f.uploaded)} issuerFor={issuerFor} formatDate={formatDate} />}
      renderEdit={onSave ? (f, { save, cancel }) => <FTEdit f={f} kinds={kinds} issuers={issuers} onSave={save} onCancel={cancel} /> : undefined}
      onSave={onSave}
      onDelete={onDelete ? (id) => onDelete(byId[id] || id) : undefined}
      empty={empty ? <div className="muted" style={{ textAlign: 'center', padding: 48 }}>{empty}</div> : undefined}
    />
  );
}
