/**
 * Odyssey DS — FileUpload
 * A drag-and-drop upload field: a dropzone (click or drop) plus a ready-file
 * list where each row can be renamed, retyped (file-kind picker), and removed.
 * Promoted from the web kit's AfmUpload (used by Add file / Add transaction).
 *
 * Works controlled (pass `files` + `onChange`) or uncontrolled (`defaultFiles`).
 * Each file is { uid, name, kind, sizeBytes }. `onChange` fires with the full
 * next array on every add / rename / retype / remove. Set `showKinds={false}`
 * for a plain list without the inline kind picker. Pass `guessKind(name)` to
 * override the default extension→kind guess with a domain vocabulary (tax,
 * insurance, contract…). Pass `renderFileExtra(file, patch)` to render an extra
 * editor beneath each file row (e.g. validity dates) — `patch(partial)` merges
 * fields into that file. Styled by .odc-upload-*.
 *
 * ## Never write a size limit into `hint` as a literal
 * Pass `maxMegabytes` and the size clause is composed from it. The limit is a
 * runtime setting an administrator can change, so a number typed into the hint
 * is a claim that goes stale silently — and it is the *user-visible* half of the
 * same mistake as a hard-coded pre-check: the field says one number while the
 * server enforces another.
 *
 * Where a surface has its own tighter product limit, `maxMegabytes` is the
 * **minimum** of that constant and the effective server cap, never the constant
 * alone. A surface may tighten the global cap; it must never override a lowered
 * one. `FileUpload.overMaxError(name, bytes, max)` composes the matching
 * rejection message from the same number.
 */

/* File-kind registry — label + Material glyph + accent color. */
const ODC_FILE_KINDS = [
  { key: 'Statement', label: 'Statement', icon: 'description',       color: 'oklch(0.79 0.115 188)', soft: 'oklch(0.79 0.115 188 / 0.16)' },
  { key: 'Document',  label: 'Document',  icon: 'insert_drive_file', color: 'oklch(0.74 0.02 250)',  soft: 'oklch(0.74 0.02 250 / 0.16)' },
  { key: 'Receipt',   label: 'Receipt',   icon: 'receipt_long',      color: 'oklch(0.80 0.15 150)',  soft: 'oklch(0.80 0.15 150 / 0.16)' },
  { key: 'Tax',       label: 'Tax',       icon: 'request_quote',     color: 'oklch(0.75 0.16 330)',  soft: 'oklch(0.75 0.16 330 / 0.16)' },
];

function odcKindMap(kinds) {
  return Object.fromEntries((kinds || ODC_FILE_KINDS).map((k) => [k.key, k]));
}

function odcFmtFileSize(bytes) {
  if (bytes == null) return '—';
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) {
    const kb = bytes / 1024;
    return `${kb < 10 ? kb.toFixed(1) : Math.round(kb)} KB`;
  }
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

function odcGuessKind(name) {
  const ext = (name.split('.').pop() || '').toLowerCase();
  if (['jpg', 'jpeg', 'png', 'heic', 'webp', 'gif', 'tiff'].includes(ext)) return 'Receipt';
  if (ext === 'pdf') return 'Statement';
  return 'Document';
}

let odcUploadUid = 0;
function odcFilesFromList(fileList) {
  return Array.from(fileList || []).map((f) => ({
    uid: `odc-up-${++odcUploadUid}`,
    name: f.name,
    kind: odcGuessKind(f.name),
    sizeBytes: f.size,
  }));
}

/* Inline file-kind picker — delegates to the DS TypeSelect engine (the same
   control behind AccountTypeSelect and the file-type pickers), so positioning,
   z-index-above-modal, scrolling and keyboard/selection behaviour all match the
   rest of the app instead of being re-implemented here. Rendered compact via the
   `.odc-upload-kind` wrapper. */
function OdcKindPicker({ value, kinds, onChange }) {
  const list = kinds || ODC_FILE_KINDS;
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const TypeSelect = NS.TypeSelect;
  if (!TypeSelect) return null;
  return (
    <div className="odc-upload-kind">
      <TypeSelect
        value={value}
        types={list}
        placeholder="Type"
        aria-label="File type"
        onChange={(k) => onChange(k)}
      />
    </div>
  );
}

/* One editable file row. */
function OdcFileRow({ file, kinds, showKinds, renderFileExtra, onChange, onRemove }) {
  const byKey = odcKindMap(kinds);
  const kind = byKey[file.kind] || (kinds || ODC_FILE_KINDS)[0];
  const extra = renderFileExtra ? renderFileExtra(file, (patch) => onChange({ ...file, ...patch })) : null;
  return (
    <div className={`odc-upload-file${extra ? ' has-meta' : ''}`}>
      {showKinds ? (
        <span className="odc-upload-file-ic" style={{ background: kind.soft, color: kind.color }}>
          <span className="material-icons" aria-hidden="true" style={{ fontSize: 20 }}>{kind.icon}</span>
        </span>
      ) : (
        <span className="odc-upload-file-ic" style={{ background: 'var(--mud-palette-action-default-hover)', color: 'var(--mud-palette-text-secondary)' }}>
          <span className="material-icons" aria-hidden="true" style={{ fontSize: 20 }}>insert_drive_file</span>
        </span>
      )}
      <div className="odc-upload-file-main">
        <input
          className="odc-upload-file-name"
          value={file.name}
          spellCheck={false}
          aria-label="File name"
          onChange={(e) => onChange({ ...file, name: e.target.value })}
        />
        <div className="odc-upload-file-sub">
          {showKinds ? (
            <OdcKindPicker value={file.kind} kinds={kinds} onChange={(k) => onChange({ ...file, kind: k })} />
          ) : null}
          <span className="odc-upload-file-size">{odcFmtFileSize(file.sizeBytes)}</span>
        </div>
        {extra ? <div className="odc-upload-file-extra">{extra}</div> : null}
      </div>
      <button type="button" className="odc-upload-file-x" aria-label="Remove file" onClick={onRemove}>
        <span className="material-icons" aria-hidden="true" style={{ fontSize: 18 }}>close</span>
      </button>
    </div>
  );
}

export function FileUpload({
  files,
  defaultFiles = [],
  onChange,
  accept,
  multiple = true,
  maxMegabytes,
  hint,
  error,
  compact = false,
  showKinds = true,
  kinds = ODC_FILE_KINDS,
  guessKind,
  renderFileExtra,
  maxHeight,
  id,
  ...rest
}) {
  const controlled = Array.isArray(files);
  const [internal, setInternal] = React.useState(defaultFiles);
  const list = controlled ? files : internal;
  const [dragging, setDragging] = React.useState(false);
  const inputRef = React.useRef(null);

  const commit = (next) => {
    if (!controlled) setInternal(next);
    if (onChange) onChange(next);
  };

  const addFiles = (fileList) => {
    let incoming = odcFilesFromList(fileList);
    if (guessKind) incoming = incoming.map((f) => ({ ...f, kind: guessKind(f.name) }));
    if (!incoming.length) return;
    // multiple=false is a genuine single-file field: a new pick/drop REPLACES the
    // current file rather than accumulating (the accumulation logic ignored the
    // flag before — a single-file picker would silently pile files up).
    if (!multiple) { commit([incoming[0]]); return; }
    commit([...list, ...incoming]);
  };
  const updateFile = (uid, next) => commit(list.map((f) => (f.uid === uid ? next : f)));
  const removeFile = (uid) => commit(list.filter((f) => f.uid !== uid));

  const onDrop = (e) => { e.preventDefault(); setDragging(false); addFiles(e.dataTransfer.files); };
  const totalBytes = list.reduce((s, f) => s + (f.sizeBytes || 0), 0);
  const errId = error && id ? `${id}-err` : undefined;
  // The size clause is composed, never typed: see the note above.
  const shownHint = hint != null ? hint : (
    maxMegabytes != null
      ? `PDF, JPG, PNG \u00b7 up to ${maxMegabytes}\u00a0MB each \u00b7 multiple at once`
      : 'PDF, JPG, PNG \u00b7 multiple at once'
  );

  return (
    <div className="odc-upload" id={id} {...rest}>
      <input
        ref={inputRef}
        type="file"
        accept={accept}
        multiple={multiple}
        hidden
        onChange={(e) => { addFiles(e.target.files); e.target.value = ''; }}
      />
      <div
        className={`odc-upload-drop${compact ? ' compact' : ''}${dragging ? ' drag' : ''}${error ? ' has-error' : ''}`}
        role="button"
        tabIndex={0}
        aria-describedby={errId}
        onClick={() => inputRef.current && inputRef.current.click()}
        onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); inputRef.current && inputRef.current.click(); } }}
        onDragOver={(e) => { e.preventDefault(); setDragging(true); }}
        onDragLeave={(e) => { e.preventDefault(); setDragging(false); }}
        onDrop={onDrop}
      >
        <span className="odc-upload-drop-ic">
          <span className="material-icons" aria-hidden="true" style={{ fontSize: compact ? 22 : 26 }}>cloud_upload</span>
        </span>
        <div className="odc-upload-drop-text">
          <strong>Drop files here</strong> or <span className="odc-upload-drop-link">browse</span>
        </div>
        <div className="odc-upload-drop-hint">{shownHint}</div>
      </div>
      {error ? <div className="odc-upload-err" id={errId}>{error}</div> : null}
      {list.length > 0 ? (
        <div className="odc-upload-list">
          <div className="odc-upload-list-head">
            <span>{list.length} file{list.length > 1 ? 's' : ''} ready</span>
            <span className="odc-upload-file-size">{odcFmtFileSize(totalBytes)}</span>
          </div>
          <div className="odc-upload-list-scroll" style={maxHeight ? { maxHeight } : undefined}>
            {list.map((f) => (
              <OdcFileRow
                key={f.uid}
                file={f}
                kinds={kinds}
                showKinds={showKinds}
                renderFileExtra={renderFileExtra}
                onChange={(next) => updateFile(f.uid, next)}
                onRemove={() => removeFile(f.uid)}
              />
            ))}
          </div>
        </div>
      ) : null}
    </div>
  );
}

FileUpload.KINDS = ODC_FILE_KINDS;
FileUpload.fmtSize = odcFmtFileSize;
FileUpload.guessKind = odcGuessKind;
FileUpload.filesFromList = odcFilesFromList;
/* The rejection message for a file over the effective cap. Composed from the
   same number as the hint, so the two can never disagree. */
FileUpload.overMaxError = (name, bytes, maxMegabytes) =>
  `${name} is ${odcFmtFileSize(bytes)} \u2014 over the ${maxMegabytes}\u00a0MB limit for this workspace. Nothing was uploaded.`;
