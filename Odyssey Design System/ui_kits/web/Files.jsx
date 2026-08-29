/* Files — flat view of every AccountFile across all accounts.
   Mirrors the Transactions page layout (page-head + filter card + table).
   AccountFiles are keyed by accountId in data.js; we flatten them here and
   join each file back to its owning account for context. The table itself is
   the SHARED DS FilesTable (via the Accounts.jsx bridge) — the same surface
   the Accounts detail and the Transactions panels render. No account filter
   here by design — search + type cover the MVP.

   Files export (ZIP) lives in the page-header overflow menu — "Export all"
   archives every stored file; "Export filtered" archives only the current
   search/type set, mirroring the Contacts page's scoped-export pattern. */

// Toast/progress atoms aren't bridged to the kit's globals — read them off the DS namespace.
const { Toast: DSToast, ToastStack: DSToastStack, ProgressBar: DSProgressBar, Spinner: DSSpinner } = window.OdysseyDesignSystem_d5aa51 || {};

// odyssey-files-export-YYYYMMDD-HHMMSSZ.zip — UTC, deterministic.
const flUtcStamp = (d) => {
  const p = (n) => String(n).padStart(2, '0');
  return `${d.getUTCFullYear()}${p(d.getUTCMonth() + 1)}${p(d.getUTCDate())}-`
       + `${p(d.getUTCHours())}${p(d.getUTCMinutes())}${p(d.getUTCSeconds())}Z`;
};
const flParseSize = (s) => {
  const m = /([\d.]+)\s*(B|KB|MB|GB)/i.exec(String(s || ''));
  if (!m) return 0;
  const mult = { b: 1, kb: 1024, mb: 1048576, gb: 1073741824 }[m[2].toLowerCase()];
  return Math.round(parseFloat(m[1]) * mult);
};
const flFormatSize = (bytes) => {
  if (bytes >= 1073741824) return `~${(bytes / 1073741824).toFixed(1)} GB`;
  if (bytes >= 1048576) return `~${(bytes / 1048576).toFixed(1)} MB`;
  if (bytes >= 1024) return `~${Math.round(bytes / 1024)} KB`;
  return `~${bytes} B`;
};

// Minimal store-method ZIP builder (no compression) so the export downloads a
// real, valid .zip demonstrating the spec's archive layout: file-map.json at
// the root + every file under files/{name}.
const flCrcTable = (() => {
  const t = new Uint32Array(256);
  for (let n = 0; n < 256; n++) { let c = n; for (let k = 0; k < 8; k++) c = (c & 1) ? (0xEDB88320 ^ (c >>> 1)) : (c >>> 1); t[n] = c >>> 0; }
  return t;
})();
const flCrc32 = (bytes) => {
  let c = 0xFFFFFFFF;
  for (let i = 0; i < bytes.length; i++) c = flCrcTable[(c ^ bytes[i]) & 0xFF] ^ (c >>> 8);
  return (c ^ 0xFFFFFFFF) >>> 0;
};
const flZip = (entries) => {
  const enc = new TextEncoder();
  const u16 = (n) => [n & 0xFF, (n >>> 8) & 0xFF];
  const u32 = (n) => [n & 0xFF, (n >>> 8) & 0xFF, (n >>> 16) & 0xFF, (n >>> 24) & 0xFF];
  const chunks = []; const central = []; let offset = 0;
  for (const e of entries) {
    const nameB = enc.encode(e.name);
    const data = e.data instanceof Uint8Array ? e.data : enc.encode(e.data);
    const crc = flCrc32(data);
    const local = [].concat(u32(0x04034b50), u16(20), u16(0), u16(0), u16(0), u16(0), u32(crc), u32(data.length), u32(data.length), u16(nameB.length), u16(0));
    chunks.push(new Uint8Array(local), nameB, data);
    central.push({ crc, size: data.length, nameB, offset });
    offset += local.length + nameB.length + data.length;
  }
  const cdStart = offset; let cdSize = 0;
  for (const c of central) {
    const head = [].concat(u32(0x02014b50), u16(20), u16(20), u16(0), u16(0), u16(0), u16(0), u32(c.crc), u32(c.size), u32(c.size), u16(c.nameB.length), u16(0), u16(0), u16(0), u16(0), u32(0), u32(c.offset));
    const buf = new Uint8Array(head.length + c.nameB.length);
    buf.set(head, 0); buf.set(c.nameB, head.length);
    chunks.push(buf); cdSize += buf.length;
  }
  chunks.push(new Uint8Array([].concat(u32(0x06054b50), u16(0), u16(0), u16(central.length), u16(central.length), u32(cdSize), u32(cdStart), u16(0))));
  return new Blob(chunks, { type: 'application/zip' });
};
// Sanitize a stored filename for a safe ZIP entry (spec §6.4): strip path
// separators + control chars, neutralise `..`, fall back to file-{id}.
const flSanitizeName = (name, fileId) => {
  let n = String(name || '').replace(/[\\/]/g, '_').replace(/[\x00-\x1f]/g, '').replace(/\.\.+/g, '.').trim();
  return n || `file-${fileId}`;
};
// Build the archive from a given file set: file-map.json + every file under
// files/{safeName}. Duplicate names get a deterministic ` (n)` suffix.
const flBuildFilesArchive = (fileSet) => {
  const seen = new Map();
  const files = fileSet.map((f) => {
    let name = flSanitizeName(f.fileName, f.fileId);
    if (seen.has(name)) {
      const n = seen.get(name) + 1; seen.set(name, n);
      const dot = name.lastIndexOf('.');
      const base = dot > 0 ? name.slice(0, dot) : name;
      const ext = dot > 0 ? name.slice(dot) : '';
      name = `${base} (${n})${ext}`;
    } else { seen.set(name, 1); }
    return { fileId: f.fileId, fileName: name };
  });
  const entries = [{ name: 'file-map.json', data: JSON.stringify({ files }, null, 2) }];
  for (const f of files) entries.push({ name: `files/${f.fileName}`, data: `Odyssey export placeholder for ${f.fileName} (FileId ${f.fileId}).\nThe production export streams the original file binary here.\n` });
  return flZip(entries);
};
const flDownloadFiles = (fileSet, name) => {
  const blob = flBuildFilesArchive(fileSet);
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url; a.download = name; document.body.appendChild(a); a.click();
  a.remove(); setTimeout(() => URL.revokeObjectURL(url), 1500);
};

const FL_CHIP = { display: 'inline-flex', alignItems: 'center', gap: 5, padding: '4px 10px', borderRadius: 999, font: '400 12px/1.3 var(--font-mono)', color: 'var(--mud-palette-text-secondary)', background: 'var(--mud-palette-action-default-hover)', border: '1px solid var(--mud-palette-divider)' };

const Files = ({ onNavigate }) => {
  const d = window.OdysseyData;
  const [q, setQ] = React.useState('');
  const [kindFilter, setKindFilter] = React.useState([]);

  // Local copy of accountFiles so newly-uploaded files appear immediately.
  const [filesByAccount, setFilesByAccount] = React.useState(d.accountFiles);
  // Shared sort (§6.11): Uploaded (default) · File name · Size · Type; one
  // {key,dir} synced with the FilesTable headers.
  const [sort, setSort] = React.useState({ key: 'uploaded', dir: 'desc' });
  // Server pagination: page (1-based) + rows-per-page (footer Pager owns it,
  // toolbar PageSizeSelect mirrors it).
  const [pageSize, setPageSize] = React.useState(25);
  const [page, setPage] = React.useState(1);

  // Export: scope ('all' | 'filtered') opens the confirm dialog; a job then
  // drives the progress widget; toastName announces the finished download.
  const [exportScope, setExportScope] = React.useState(null);
  const [filesJob, setFilesJob] = React.useState(null);   // null | {status:'preparing'} | {status:'running',done,total}
  const [toastName, setToastName] = React.useState(null);

  // Flatten accountFiles { accountId: [...] } into one list, tagged with its account.
  const allFiles = Object.entries(filesByAccount).flatMap(([acctId, list]) =>
    list.map(f => ({ ...f, account: acctId })));

  const rows = React.useMemo(() => allFiles.filter(f => {
    if (kindFilter.length && !kindFilter.includes(f.kind)) return false;
    if (q && !f.name.toLowerCase().includes(q.toLowerCase())) return false;
    return true;
  }), [allFiles, kindFilter, q]);

  // Any search / filter / sort / size change returns to page 1 (server contract).
  React.useEffect(() => { setPage(1); }, [q, kindFilter, sort, pageSize]);
  const totalCount = rows.length;
  const paged = React.useMemo(() => {
    if (pageSize === 'all') return rows;
    const start = (page - 1) * pageSize;
    return rows.slice(start, start + pageSize);
  }, [rows, page, pageSize]);

  // Account-file types present in the data, in canonical registry order — drives the filter.
  const presentTypes = (window.OdysseyData.accountFileTypes || [])
    .filter(t => allFiles.some(f => f.kind === t.key));

  // The file set the confirm dialog + export operate on (per chosen scope).
  const exportSet = React.useMemo(() => {
    const src = exportScope === 'filtered' ? rows : allFiles;
    return src.map(f => ({ fileId: f.id, fileName: f.name, bytes: flParseSize(f.size) }));
  }, [exportScope, rows, allFiles]);
  const exportBytes = flFormatSize(exportSet.reduce((a, f) => a + f.bytes, 0));

  // Confirm → async job (preparing → archiving N/total) → real .zip download
  // → success toast. The dialog is replaced by the progress widget while
  // running, so duplicate starts are impossible.
  const startExport = () => {
    const set = exportSet, scope = exportScope;
    setExportScope(null);
    setFilesJob({ status: 'preparing' });
    setTimeout(() => {
      const total = set.length;
      let done = 0;
      setFilesJob({ status: 'running', done, total });
      const step = Math.max(1, Math.round(total / 28));
      const iv = setInterval(() => {
        done = Math.min(total, done + step);
        setFilesJob({ status: 'running', done, total });
        if (done >= total) {
          clearInterval(iv);
          setTimeout(() => {
            const suffix = scope === 'filtered' ? '-filtered' : '';
            const name = `odyssey-files-export${suffix}-${flUtcStamp(new Date())}.zip`;
            flDownloadFiles(set, name);
            setFilesJob(null);
            setToastName(name);
          }, 450);
        }
      }, 110);
    }, 750);
  };

  return (
    <div className="col gap-6">
      <PageHeader
        title="Files"
        icon="folder"
        sub={`${rows.length} files across ${Object.keys(filesByAccount).length} accounts`}
        overview={(
          <div className="odc-summary-grid">
            <BreakdownTile label="By type" empty="No files."
              rows={odcTypeRows(allFiles, window.OdysseyData.accountFileTypes, (f) => f.kind)} />
          </div>
        )}
        overviewDefaultOpen
        searchDefaultOpen
        search={(
          <div className="row gap-3" style={{ flexWrap: 'wrap' }}>
            <div style={{ minWidth: 280, flex: 1 }}>
              <SearchField placeholder="Search file name…" value={q} onChange={setQ} />
            </div>
            <div style={{ minWidth: 160 }}>
              <AccountFileTypeMultiSelect value={kindFilter} onChange={setKindFilter} types={presentTypes} />
            </div>
            <SortSelect sort={sort} onSort={setSort}
              fields={[
                { key: 'uploaded', label: 'Uploaded', type: 'date' },
                { key: 'name', label: 'File name', type: 'text' },
                { key: 'size', label: 'Size', type: 'number' },
                { key: 'kind', label: 'Type', type: 'status' },
              ]} />
            <PageSizeSelect value={pageSize} onChange={setPageSize} />
          </div>
        )}
        menu={filesJob ? undefined : [
          { icon: 'folder_zip', label: 'Export all files as ZIP', onClick: () => setExportScope('all') },
          { icon: 'filter_list', label: `Export filtered (${totalCount}) as ZIP`, onClick: () => setExportScope('filtered') },
        ]}
      />

      <Card>
        <CardBody style={{ padding: 0 }}>
          {/* The shared files surface — same component as the Accounts detail
              and the Transactions panels; sorting lives inside it. */}
          <FilesTable
            files={paged}
            ariaLabel="Files"
            sort={sort}
            onSortChange={setSort}
            accountFor={(f) => d.accountById[f.account]}
            onNavigate={onNavigate}
            onDelete={(f) => setFilesByAccount(prev => ({
              ...prev,
              [f.account]: (prev[f.account] || []).filter(x => x.id !== f.id),
            }))}
            empty="No files match your filters."
          />
          {totalCount > 0 && (
            <Pager page={page} pageSize={pageSize} totalCount={totalCount}
              onPageChange={setPage} onPageSizeChange={setPageSize} />
          )}
        </CardBody>
      </Card>

      {/* Preparing / archiving progress — a small fixed status card while a job runs. */}
      {filesJob && (
        <div role="status" aria-live="polite"
          style={{ position: 'fixed', right: 24, bottom: 24, zIndex: 60, minWidth: 264, padding: '14px 16px', borderRadius: 10, background: 'var(--mud-palette-surface)', border: '1px solid var(--mud-palette-divider)', boxShadow: 'var(--mud-elevation-8, 0 8px 30px rgba(0,0,0,.35))' }}>
          {filesJob.status === 'preparing' ? (
            <div className="row gap-2" style={{ alignItems: 'center' }}>
              {DSSpinner ? <DSSpinner size="sm" /> : null}
              <span style={{ font: '500 13px/1 var(--font-sans)', color: 'var(--mud-palette-text-primary)' }}>Preparing export…</span>
            </div>
          ) : (
            <>
              <div className="row" style={{ justifyContent: 'space-between', marginBottom: 8, gap: 10, whiteSpace: 'nowrap' }}>
                <span style={{ font: '500 12.5px/1 var(--font-sans)', color: 'var(--mud-palette-text-primary)' }}>Archiving files…</span>
                <span style={{ font: '500 11.5px/1 var(--font-mono)', color: 'var(--mud-palette-text-secondary)' }}>{filesJob.done} / {filesJob.total}</span>
              </div>
              {DSProgressBar ? <DSProgressBar value={Math.round((filesJob.done / (filesJob.total || 1)) * 100)} /> : null}
            </>
          )}
        </div>
      )}

      {toastName && DSToast && (
        <DSToastStack>
          <DSToast key="files" severity="success" icon="folder_zip" duration={5200} onClose={() => setToastName(null)}
            message={(
              <div>
                <div>Files export ready</div>
                <div style={{ fontFamily: 'var(--font-mono)', fontSize: 11.5, color: 'var(--mud-palette-text-secondary)', marginTop: 2, wordBreak: 'break-all' }}>{toastName}</div>
              </div>
            )} />
        </DSToastStack>
      )}

      {exportScope && (
        <Modal
          title={exportScope === 'filtered' ? 'Export filtered files?' : 'Export all files?'}
          icon="folder_zip"
          iconTone="warning"
          onClose={() => setExportScope(null)}
          footer={(
            <>
              <Button variant="text" onClick={() => setExportScope(null)}>Cancel</Button>
              <Button variant="filled" icon="download" onClick={startExport}>Export</Button>
            </>
          )}
        >
          <div style={{ font: '400 14px/1.65 var(--font-sans)', color: 'var(--mud-palette-text-primary)' }}>
            This downloads a ZIP containing <b>{exportScope === 'filtered' ? 'the files matching your current filters' : 'every stored file’s contents'}</b> plus a <code style={{ fontFamily: 'var(--font-mono)', fontSize: 12.5, background: 'var(--mud-palette-action-default-hover)', padding: '1px 5px', borderRadius: 4 }}>file-map.json</code> that maps each file ID to its archived filename. No other metadata is included.
          </div>
          <div className="row" style={{ flexWrap: 'wrap', gap: 8, marginTop: 14 }}>
            <span style={FL_CHIP}><MIcon name="description" size={13} />{exportSet.length} files</span>
            <span style={FL_CHIP}><MIcon name="folder_zip" size={13} />{exportBytes}</span>
            <span style={FL_CHIP}><MIcon name="schedule" size={13} />may take several minutes</span>
          </div>
          <div style={{ display: 'flex', gap: 9, marginTop: 16, padding: '11px 13px', borderRadius: 8, background: 'var(--finance-pending-soft)', border: '1px solid var(--finance-pending-border)' }}>
            <span style={{ color: 'var(--amber-500)', flex: 'none', marginTop: 1, display: 'inline-flex' }}><MIcon name="lock" size={17} /></span>
            <div style={{ font: '400 12.5px/1.5 var(--font-sans)', color: 'var(--mud-palette-text-secondary)' }}>
              Treat the archive as sensitive — it contains uploaded file contents. The download requires your admin permission.
            </div>
          </div>
        </Modal>
      )}
    </div>
  );
};

Object.assign(window, { Files });
