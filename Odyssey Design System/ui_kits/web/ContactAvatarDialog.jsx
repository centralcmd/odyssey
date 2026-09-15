/* ContactAvatarDialog — the client-side crop + downscale dialog behind
   “Change picture” / “Change logo” (contact-avatar spec §3).

   What this file is faithful to:
   - ONE image per contact, stored in the existing Files store. The dialog
     normalises what gets uploaded: a square 512 px cover crop for a Person, a
     contained 512 px canvas for an Organization (a wordmark is never cut).
   - Every number comes from CONTACT_AVATAR_LIMITS (the kit's stand-in for
     `ContactAvatarLimits` in Odyssey.Dtos) combined with the live server cap
     read from the upload-limits lookup. No literal cap at a call site.
   - The controls are native <input type="range"> on the DS `.odc-range` style — Zoom, Horizontal, Vertical —
     each labelled, each arrow/Home/End operable by construction. Pointer drag
     on the canvas is an ADDITIONAL affordance, never the only one.
   - The <canvas> is aria-hidden and the preview state is available as text
     beside it; a canvas exposes no accessible structure and the three ranges
     already carry the state.
   - One validation-failure rule: a failure attributable to a control renders on
     that control (aria-invalid + aria-describedby + focus, no role="alert", so
     it is announced once); a server / network failure goes to a role="alert"
     region, carries the ProblemDetails message verbatim, and does not move
     focus. */

const CONTACT_AVATAR_LIMITS = {
  // Stored avatar — the server is authoritative. The effective byte cap is
  // min(global upload cap, maxAvatarBytes): TightenTo is `min`, never `max`.
  maxAvatarBytes: 2 * 1024 * 1024,
  maxAvatarDimension: 1024,
  // Source — what the crop dialog will open, in the browser only.
  maxSourceBytes: 20 * 1024 * 1024,
  maxSourceDimension: 8192,
  // What the crop writes.
  outputDimension: 512,
  jpegQuality: 0.85,
  types: ['image/png', 'image/jpeg', 'image/webp'],
  typeLabel: 'PNG, JPEG or WebP',
};
const cavTightenTo = (globalCapBytes, surfaceCapBytes) => Math.min(globalCapBytes || Infinity, surfaceCapBytes);
const cavMb = (bytes) => `${Math.round((bytes / (1024 * 1024)) * 10) / 10} MB`;

/* Deterministic stand-in imagery. The product seeds two demo contacts from the
   existing DemoImages generator, which emits PNG — here it is a striped
   placeholder with a monospace caption, so nothing pretends to be a photograph
   the kit does not have. */
const cavPlaceholder = (kind, seed = 0) => {
  const S = 512;
  const c = document.createElement('canvas');
  c.width = S; c.height = kind === 'logo' ? Math.round(S * 0.42) : S;
  const g = c.getContext('2d');
  const hue = kind === 'logo' ? 295 : 150;
  g.fillStyle = kind === 'logo' ? 'oklch(0.28 0.03 295)' : 'oklch(0.34 0.05 150)';
  g.fillRect(0, 0, c.width, c.height);
  g.strokeStyle = kind === 'logo' ? 'oklch(0.72 0.16 295 / 0.5)' : 'oklch(0.80 0.15 150 / 0.4)';
  g.lineWidth = 6;
  for (let i = -c.height; i < c.width + c.height; i += 26 + (seed % 5)) {
    g.beginPath(); g.moveTo(i, 0); g.lineTo(i + c.height, c.height); g.stroke();
  }
  g.fillStyle = `oklch(0.94 0.02 ${hue})`;
  g.font = `500 ${Math.round(c.height * 0.1)}px ui-monospace, monospace`;
  g.textAlign = 'center'; g.textBaseline = 'middle';
  g.fillText(kind === 'logo' ? 'LOGO' : 'PORTRAIT', c.width / 2, c.height / 2);
  return c.toDataURL('image/png');
};

const ContactAvatarDialog = ({ contact, currentSrc, onCancel, onSaved, outcome = 'ok', globalCapBytes = 64 * 1024 * 1024 }) => {
  const { useState, useRef, useEffect, useCallback } = React;
  const isPerson = (contact && contact.type) !== 'Organization';
  const L = CONTACT_AVATAR_LIMITS;
  const effectiveCap = cavTightenTo(globalCapBytes, L.maxAvatarBytes);

  const [src, setSrc] = useState(currentSrc || null);
  const [img, setImg] = useState(null);
  const [zoom, setZoom] = useState(100);
  const [px, setPx] = useState(0);
  const [py, setPy] = useState(0);
  const [busy, setBusy] = useState(false);
  const [fileError, setFileError] = useState(null);   // attributable to the dropzone
  const [rows, setRows] = useState([]);               // the FileUpload field's own row model
  const [serverError, setServerError] = useState(null); // ProblemDetails text
  const [live, setLive] = useState('');
  const canvasRef = useRef(null);
  const dropRef = useRef(null);

  const noun = isPerson ? 'picture' : 'logo';

  useEffect(() => {
    if (!src) { setImg(null); return; }
    const i = new Image();
    i.onload = () => setImg(i);
    i.onerror = () => { setImg(null); setFileError('Unable to read that image. Choose a PNG, JPEG or WebP file.'); };
    i.src = src;
  }, [src]);

  const draw = useCallback(() => {
    const cv = canvasRef.current;
    if (!cv) return;
    const S = L.outputDimension;
    cv.width = S; cv.height = S;
    const g = cv.getContext('2d');
    g.clearRect(0, 0, S, S);
    if (!img) return;
    const base = isPerson
      ? Math.max(S / img.naturalWidth, S / img.naturalHeight)   // cover — square crop
      : Math.min(S / img.naturalWidth, S / img.naturalHeight);  // contain — letterboxed
    const scale = base * (zoom / 100);
    const dw = img.naturalWidth * scale, dh = img.naturalHeight * scale;
    const slackX = Math.max(0, (dw - S) / 2), slackY = Math.max(0, (dh - S) / 2);
    g.drawImage(img, (S - dw) / 2 + (px / 100) * slackX, (S - dh) / 2 + (py / 100) * slackY, dw, dh);
  }, [img, zoom, px, py, isPerson, L.outputDimension]);

  useEffect(() => { draw(); }, [draw]);

  const pick = (file) => {
    setServerError(null);
    if (!file) return;
    if (L.types.indexOf(file.type) < 0) {
      rejectFile(`That file is a ${file.type || 'unknown type'}. Choose a ${L.typeLabel} image.`);
      return;
    }
    if (file.size > L.maxSourceBytes) {
      rejectFile(`That file is ${cavMb(file.size)}. Choose an image of ${cavMb(L.maxSourceBytes)} or less.`);
      return;
    }
    setFileError(null);
    setRows(window.FileUpload ? window.FileUpload.filesFromList([file]) : []);
    setZoom(100); setPx(0); setPy(0);
    setSrc(URL.createObjectURL(file));
  };

  const rejectFile = (msg) => {
    setFileError(msg);
    setRows([]);
  };

  // Attributable failures move focus to the control they belong to — here the
  // shared FileUpload field's own dropzone.
  useEffect(() => {
    if (!fileError) return;
    const zone = dropRef.current && dropRef.current.querySelector('.odc-upload-drop');
    if (zone) zone.focus();
  }, [fileError]);

  const reset = () => { setZoom(100); setPx(0); setPy(0); };

  const save = () => {
    if (!img) { setFileError(`Choose an image to use as this contact's ${noun}.`); return; }
    setServerError(null);
    setBusy(true);
    setLive(`Uploading ${noun}…`);
    const encoded = canvasRef.current.toDataURL(isPerson ? 'image/jpeg' : 'image/png', L.jpegQuality);
    window.setTimeout(() => {
      if (outcome === 'too-large') {
        setBusy(false);
        setLive('');
        // The server's own ProblemDetails message — it names the actual limit.
        setServerError(`Unable to save the ${noun}. The image must be ${cavMb(effectiveCap)} or smaller. Crop a smaller area, or choose a different file.`);
        return;
      }
      if (outcome === 'network') {
        setBusy(false);
        setLive('');
        setServerError(`Unable to save the ${noun}. The server could not be reached. Check your connection and try again.`);
        return;
      }
      setBusy(false);
      setLive(isPerson ? 'Picture saved' : 'Logo saved');
      onSaved && onSaved(encoded);
    }, 650);
  };

  /* Pointer drag — an addition to the ranges, not a replacement. */
  const dragRef = useRef(null);
  const onDown = (e) => { if (!img) return; dragRef.current = { x: e.clientX, y: e.clientY, px, py }; };
  const onMove = (e) => {
    const d = dragRef.current;
    if (!d) return;
    const k = 0.55;
    setPx(Math.max(-100, Math.min(100, d.px - (e.clientX - d.x) * k)));
    setPy(Math.max(-100, Math.min(100, d.py - (e.clientY - d.y) * k)));
  };
  const endDrag = () => { dragRef.current = null; };

  const pctText = (v) => `${v} %`;
  const offsetText = (v) => (v === 0 ? 'centred' : `${Math.abs(v)} % ${v < 0 ? 'left of centre' : 'right of centre'}`);
  const offsetTextV = (v) => (v === 0 ? 'centred' : `${Math.abs(v)} % ${v < 0 ? 'above centre' : 'below centre'}`);
  const stateText = img
    ? `Showing ${px === 0 && py === 0 ? 'the centre' : 'an off-centre area'} of your ${noun} at ${zoom} %.`
    : `No image chosen yet. The ${noun} is ${isPerson ? 'cropped to a square and shown as a circle' : 'contained, never cropped, on a neutral ground'}.`;

  return (
    <Modal
      title={isPerson ? 'Crop profile picture' : 'Crop logo'}
      subtitle={`${L.typeLabel} · up to ${cavMb(L.maxSourceBytes)} · stored at ${L.outputDimension} × ${L.outputDimension}`}
      icon="crop"
      className="cav-dialog"
      onClose={onCancel}
      footer={<React.Fragment>
        <Button variant="text" onClick={onCancel}>Cancel</Button>
        <Button variant="filled" icon="check" loading={busy} onClick={save}>Save</Button>
      </React.Fragment>}>
      <div className="cav-body" aria-busy={busy || undefined}>
        {serverError ? (
          <div className="cav-alert" role="alert">
            <MIcon name="error_outline" size={18} />
            <span>{serverError}</span>
          </div>
        ) : null}

        {/* The shared DS upload field, single-file and kind-less: one image per
            contact, and the file's KIND is not the user's to set here (the
            stored filename and description are generated). Its onFiles hands us
            the actual bytes to crop. */}
        <FieldShell label={img ? 'Source image' : `Choose ${isPerson ? 'a picture' : 'a logo'}`}>
          <div ref={dropRef}>
            <FileUpload
              id="cav-file"
              compact
              multiple={false}
              showKinds={false}
              accept={L.types.join(',')}
              files={rows}
              onChange={(next) => { setRows(next); if (!next.length) { setSrc(null); setFileError(null); } }}
              onFiles={(files) => pick(files[0])}
              error={fileError || undefined}
              hint={`${L.typeLabel} · up to ${cavMb(L.maxSourceBytes)} · ${L.maxSourceDimension} × ${L.maxSourceDimension} px · one image`} />
          </div>
        </FieldShell>

        <div className="cav-stage">
          <div className={`cav-frame${isPerson ? ' round' : ' ground'}`}
            onPointerDown={onDown} onPointerMove={onMove} onPointerUp={endDrag} onPointerLeave={endDrag}>
            <canvas ref={canvasRef} aria-hidden="true" />
            {!img ? <span className="cav-frame-empty" aria-hidden="true"><MIcon name={isPerson ? 'person' : 'corporate_fare'} size={30} /></span> : null}
          </div>

          <div className="cav-controls">
            <p className="cav-state">{stateText}</p>
            <label className="cav-ctl" htmlFor="cav-zoom">
              <span className="cav-ctl-l">Zoom<b>{pctText(zoom)}</b></span>
              <input id="cav-zoom" type="range" className="odc-range" min={100} max={300} step={5} value={zoom} disabled={!img}
                aria-valuetext={pctText(zoom)} onChange={(e) => setZoom(Number(e.target.value))} />
            </label>
            <label className="cav-ctl" htmlFor="cav-x">
              <span className="cav-ctl-l">Horizontal<b>{offsetText(px)}</b></span>
              <input id="cav-x" type="range" className="odc-range" min={-100} max={100} step={2} value={px} disabled={!img}
                aria-valuetext={offsetText(px)} onChange={(e) => setPx(Number(e.target.value))} />
            </label>
            <label className="cav-ctl" htmlFor="cav-y">
              <span className="cav-ctl-l">Vertical<b>{offsetTextV(py)}</b></span>
              <input id="cav-y" type="range" className="odc-range" min={-100} max={100} step={2} value={py} disabled={!img}
                aria-valuetext={offsetTextV(py)} onChange={(e) => setPy(Number(e.target.value))} />
            </label>
            <div className="cav-reset">
              <Button variant="text" icon="restart_alt" onClick={reset}>Reset</Button>
            </div>
          </div>
        </div>

        <p className="cav-note">
          {isPerson
            ? 'Saved as a square JPEG and shown as a circle. Location, capture time and device details are removed from the stored file.'
            : 'Saved as a PNG with its transparency and shown contained on a neutral ground — a wordmark is never cut. Metadata is removed from the stored file.'}
        </p>
        <span className="cav-live" role="status" aria-live="polite">{live}</span>
      </div>
    </Modal>
  );
};

Object.assign(window, { ContactAvatarDialog, CONTACT_AVATAR_LIMITS, cavPlaceholder, cavMb, cavTightenTo });
