/* FileViewerModal — opened from the "View" action on any AccountFile row
   (Accounts → account detail → Files, and the flat Files page).

   Built on the shared .aam-* dialog shell, widened into a document viewer
   (.fvm-*). Three branches, chosen from the file extension:
     • image  → zoomable / rotatable raster (mock: a monospace receipt paper)
     • pdf    → paged document with page nav + zoom (mock: a statement page)
     • other  → graceful "can't preview, download instead" empty state

   MUDBLAZOR NOTE — what maps to native components vs. not:
     The shell (MudDialog), header, toolbar (MudIconButton / MudButtonGroup),
     and the image branch (MudImage) are all native MudBlazor. MudBlazor has
     NO dedicated PDF component — in the real app the pdf branch is the
     browser's built-in viewer embedded via <object data="blob:…/pdf">
     (or an <iframe>). The white "page" + toolbar here is the chrome we wrap
     around that embed so it matches the rest of Odyssey. */

const FV_IMAGE_EXT = ['jpg', 'jpeg', 'png', 'gif', 'webp', 'heic', 'heif', 'tiff', 'bmp', 'svg', 'avif'];

const fvExt = (name) => (name.split('.').pop() || '').toLowerCase();
const fvKindOf = (name) => {
  const ext = fvExt(name);
  if (FV_IMAGE_EXT.includes(ext)) return 'image';
  if (ext === 'pdf') return 'pdf';
  return 'other';
};

const FV_ZOOM_MIN = 0.5;
const FV_ZOOM_MAX = 3;
const FV_ZOOM_STEP = 0.25;
const fvClampZoom = (z) => Math.min(FV_ZOOM_MAX, Math.max(FV_ZOOM_MIN, Math.round(z / FV_ZOOM_STEP) * FV_ZOOM_STEP));

/* ---- Mock document content ----------------------------------------------
   Stand-ins for the real bytes the viewer would render. Kept simple and
   typographic (no illustration) so the chrome — zoom, rotate, paging — is
   what's on display. White paper + dark ink in BOTH themes: this is file
   content, not app surface. */

const FvStatementPage = ({ file, account, page, total }) => (
  <div className="fvm-page fvm-doc">
    <div className="fvm-doc-band">
      <div>
        <div className="fvm-doc-bank">{account ? account.name : 'Odyssey Bank'}</div>
        <div className="fvm-doc-acct">{account && account.number ? account.number : '•••• 0000'}</div>
      </div>
      <div className="fvm-doc-band-r">
        <div className="fvm-doc-kind">Account statement</div>
        <div className="fvm-doc-period">Jan 1 – Jan 31, 2026</div>
      </div>
    </div>

    {page === 1 ? (
      <React.Fragment>
        <div className="fvm-doc-summary">
          <div className="fvm-doc-tile">
            <span className="fvm-doc-tlab">Opening balance</span>
            <span className="fvm-doc-tval">$12,480.10</span>
          </div>
          <div className="fvm-doc-tile">
            <span className="fvm-doc-tlab">Total in</span>
            <span className="fvm-doc-tval pos">+$6,210.00</span>
          </div>
          <div className="fvm-doc-tile">
            <span className="fvm-doc-tlab">Total out</span>
            <span className="fvm-doc-tval neg">−$4,038.74</span>
          </div>
          <div className="fvm-doc-tile">
            <span className="fvm-doc-tlab">Closing balance</span>
            <span className="fvm-doc-tval">$14,651.36</span>
          </div>
        </div>
        <div className="fvm-doc-h">Activity</div>
      </React.Fragment>
    ) : (
      <div className="fvm-doc-h">Activity (continued)</div>
    )}

    <table className="fvm-doc-tbl">
      <thead>
        <tr><th scope="col">Date</th><th scope="col">Description</th><th scope="col" className="r">Amount</th><th scope="col" className="r">Balance</th></tr>
      </thead>
      <tbody>
        {[
          ['Jan 0' + (page * 2), 'Direct deposit — Aurora Labs', '+2,940.00', '15,420.10'],
          ['Jan 0' + (page * 2 + 1), 'Wholefoods Market #221', '−86.42', '15,333.68'],
          ['Jan 1' + page, 'Transfer to Brokerage', '−1,200.00', '14,133.68'],
          ['Jan 1' + (page + 3), 'Pacific Gas & Electric', '−142.18', '13,991.50'],
          ['Jan 2' + page, 'Refund — Delta Air Lines', '+318.60', '14,310.10'],
          ['Jan 2' + (page + 4), 'Card payment — Amex', '−620.00', '13,690.10'],
        ].map((r, i) => (
          <tr key={i}>
            <td className="fvm-doc-mono">{r[0]}</td>
            <td>{r[1]}</td>
            <td className={'r fvm-doc-mono ' + (r[2][0] === '+' ? 'pos' : 'neg')}>{r[2]}</td>
            <td className="r fvm-doc-mono">{r[3]}</td>
          </tr>
        ))}
      </tbody>
    </table>

    <div className="fvm-doc-foot">
      <span>{file.name}</span>
      <span>Page {page} of {total}</span>
    </div>
  </div>
);

const FvReceiptPaper = ({ file }) => (
  <div className="fvm-page fvm-receipt">
    <div className="fvm-rc-store">COSTCO WHOLESALE</div>
    <div className="fvm-rc-sub">#1123 · San Carlos, CA</div>
    <div className="fvm-rc-rule" />
    {[
      ['ORGANIC BANANAS', '1.99'],
      ['ROTISSERIE CHICKEN', '4.99'],
      ['KIRKLAND OLIVE OIL', '17.49'],
      ['PAPER TOWELS 12CT', '21.99'],
      ['GROUND COFFEE 3LB', '14.99'],
      ['MIXED NUTS 2.5LB', '15.99'],
    ].map((r, i) => (
      <div className="fvm-rc-line" key={i}>
        <span>{r[0]}</span><span>{r[1]}</span>
      </div>
    ))}
    <div className="fvm-rc-rule" />
    <div className="fvm-rc-line"><span>SUBTOTAL</span><span>77.44</span></div>
    <div className="fvm-rc-line"><span>TAX 8.75%</span><span>6.78</span></div>
    <div className="fvm-rc-line fvm-rc-total"><span>TOTAL</span><span>84.22</span></div>
    <div className="fvm-rc-rule" />
    <div className="fvm-rc-meta">VISA ····6021   APPROVED</div>
    <div className="fvm-rc-meta">05/18/2026  14:22</div>
    <div className="fvm-rc-thanks">★ THANK YOU ★</div>
  </div>
);

const FvUnsupported = ({ file, onDownload }) => (
  <div className="fvm-empty">
    <span className="fvm-empty-ic"><MIcon name="visibility_off" size={34} /></span>
    <div className="fvm-empty-ttl">Preview not available</div>
    <div className="fvm-empty-sub">
      <span className="mono">.{fvExt(file.name)}</span> files can't be shown in the browser. Download the file to open it in another app.
    </div>
    <Button variant="outlined" color="primary" icon="download" onClick={onDownload}>Download file</Button>
  </div>
);

/* ---- Toolbar zoom cluster ---- */
const FvZoom = ({ zoom, onZoom, onFit }) => (
  <div className="fvm-zoom">
    <button className="fvm-tbtn" aria-label="Zoom out" disabled={zoom <= FV_ZOOM_MIN}
      onClick={() => onZoom(fvClampZoom(zoom - FV_ZOOM_STEP))}>
      <MIcon name="remove" size={18} />
    </button>
    <button className="fvm-zlabel" onClick={onFit} title="Reset to fit">{Math.round(zoom * 100)}%</button>
    <button className="fvm-tbtn" aria-label="Zoom in" disabled={zoom >= FV_ZOOM_MAX}
      onClick={() => onZoom(fvClampZoom(zoom + FV_ZOOM_STEP))}>
      <MIcon name="add" size={18} />
    </button>
  </div>
);

const FileViewerModal = ({ file, account, onClose }) => {
  const { useState, useEffect } = React;
  const H = window.OdysseyHelpers;
  const type = fvKindOf(file.name);
  const kind = (window.AFM_KIND_BY_KEY && window.AFM_KIND_BY_KEY[file.kind]) || { icon: 'insert_drive_file', color: 'var(--mud-palette-text-secondary)', soft: 'var(--mud-palette-action-default-hover)' };

  const totalPages = type === 'pdf' ? 3 : 1;
  const [zoom, setZoom] = useState(1);
  const [rotation, setRotation] = useState(0);
  const [page, setPage] = useState(1);
  const dialogRef = React.useRef(null);

  // Focus management (mirrors the DS Modal contract): remember the opener,
  // move focus into the dialog, trap Tab inside it, restore focus on close.
  useEffect(() => {
    const prev = document.activeElement;
    const node = dialogRef.current;
    if (node) node.focus();
    const focusables = () => (node
      ? Array.from(node.querySelectorAll('a[href], button:not([disabled]), input:not([disabled]), [tabindex]:not([tabindex="-1"])')).filter((el) => el.offsetParent !== null)
      : []);
    const onTrap = (e) => {
      if (e.key !== 'Tab') return;
      const els = focusables();
      if (!els.length) { e.preventDefault(); if (node) node.focus(); return; }
      const first = els[0];
      const last = els[els.length - 1];
      const active = document.activeElement;
      if (e.shiftKey) {
        if (active === first || !node.contains(active)) { e.preventDefault(); last.focus(); }
      } else if (active === last || !node.contains(active)) { e.preventDefault(); first.focus(); }
    };
    document.addEventListener('keydown', onTrap);
    return () => {
      document.removeEventListener('keydown', onTrap);
      if (prev && prev.focus) prev.focus();
    };
  }, []);

  useEffect(() => {
    const onKey = (e) => {
      if (e.key === 'Escape') return onClose();
      if (type === 'pdf' && e.key === 'ArrowRight') setPage(p => Math.min(totalPages, p + 1));
      if (type === 'pdf' && e.key === 'ArrowLeft') setPage(p => Math.max(1, p - 1));
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [onClose, type, totalPages]);

  const fit = () => { setZoom(1); setRotation(0); };

  let body;
  if (type === 'pdf') body = <FvStatementPage file={file} account={account} page={page} total={totalPages} />;
  else if (type === 'image') body = <FvReceiptPaper file={file} />;
  else body = <FvUnsupported file={file} onDownload={() => H.downloadFile(file)} />;

  return ReactDOM.createPortal(
    <div className="aam-scrim fvm-scrim" onMouseDown={onClose}>
      <div className="aam-dialog fvm-dialog" role="dialog" aria-modal="true" ref={dialogRef} tabIndex={-1}
        aria-label={`Viewing ${file.name}`} onMouseDown={(e) => e.stopPropagation()}>

        {/* Header — file identity + context */}
        <div className="fvm-head">
          <span className="fvm-head-ic" style={{ background: kind.soft, color: kind.color }}>
            <MIcon name={kind.icon} size={20} />
          </span>
          <div className="fvm-head-main">
            <div className="fvm-head-name" title={file.name}>{file.name}</div>
            <div className="fvm-head-meta">
              <Chip tone="info">{file.kind}</Chip>
              {account && <span className="fvm-dot">·</span>}
              {account && <span className="muted">{account.name}{account.number ? ` ${account.number}` : ''}</span>}
              <span className="fvm-dot">·</span>
              <span className="muted mono">{file.size}</span>
              <span className="fvm-dot">·</span>
              <span className="muted">{H && H.dateLong ? H.dateLong(file.uploaded) : file.uploaded}</span>
            </div>
          </div>
          <button className="aam-x fvm-x" aria-label="Close" onClick={onClose}>
            <MIcon name="close" size={20} />
          </button>
        </div>

        {/* Toolbar — controls vary by type */}
        {type !== 'other' && (
          <div className="fvm-bar">
            <div className="fvm-bar-l">
              {type === 'pdf' && (
                <div className="fvm-pager">
                  <button className="fvm-tbtn" aria-label="Previous page" disabled={page <= 1}
                    onClick={() => setPage(p => Math.max(1, p - 1))}>
                    <MIcon name="chevron_left" size={20} />
                  </button>
                  <span className="fvm-pager-lab mono">{page} / {totalPages}</span>
                  <button className="fvm-tbtn" aria-label="Next page" disabled={page >= totalPages}
                    onClick={() => setPage(p => Math.min(totalPages, p + 1))}>
                    <MIcon name="chevron_right" size={20} />
                  </button>
                </div>
              )}
            </div>

            <div className="fvm-bar-c">
              <FvZoom zoom={zoom} onZoom={setZoom} onFit={fit} />
            </div>

            <div className="fvm-bar-r">
              {type === 'image' && (
                <button className="fvm-tbtn" aria-label="Rotate"
                  onClick={() => setRotation(r => (r + 90) % 360)}>
                  <MIcon name="rotate_right" size={18} />
                </button>
              )}
              <button className="fvm-tbtn" aria-label="Open in new tab" title="Open in new tab">
                <MIcon name="open_in_new" size={18} />
              </button>
            </div>
          </div>
        )}

        {/* Stage — recessed neutral; holds the page */}
        <div className={`fvm-stage ${type === 'other' ? 'is-empty' : ''}`}>
          {type === 'other' ? body : (
            <div className="fvm-canvas"
              style={{ transform: `scale(${zoom}) rotate(${rotation}deg)` }}>
              {body}
            </div>
          )}
        </div>

        {/* Footer */}
        <div className="fvm-foot">
          <span className="fvm-foot-note muted">
            <MIcon name="lock" size={14} /> Read-only preview
          </span>
          <div className="fvm-foot-actions">
            <Button variant="text" onClick={onClose}>Close</Button>
            <Button variant="filled" color="primary" icon="download" onClick={() => H.downloadFile(file)}>Download</Button>
          </div>
        </div>
      </div>
    </div>,
    document.body
  );
};

Object.assign(window, { FileViewerModal, fvKindOf, fvExt });
