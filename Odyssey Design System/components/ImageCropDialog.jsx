/**
 * Odyssey DS — ImageCropDialog
 * ---------------------------------------------------------------------------
 * The one client-side crop + downscale dialog behind every "Add / Change
 * picture" control in the product. It was extracted from the contact-avatar
 * dialog when the **user profile picture** feature arrived, because two copies
 * of a cropper on an untrusted-input path drift — and the copy with fewer eyes
 * on it is the one that drifts first. Both surfaces now mount this component:
 *
 *   • a contact's image  — `subject="picture" | "logo"`, `fit="cover" | "contain"`
 *   • the signed-in user's own profile picture — `subject="picture"`, `fit="cover"`
 *
 * What the dialog owns, and what the caller owns:
 *   • It owns the source caps, the crop stage, the re-encode and the upload
 *     *call*; `onUpload(dataUrl)` is the caller's delegate and must resolve
 *     `{ ok: true }` or `{ ok: false, message }` — the message is the server's
 *     own ProblemDetails text, rendered verbatim.
 *   • It resolves the effective byte cap itself, as `min(globalCapBytes,
 *     surfaceMegabytes)` — never `max`. A surface may tighten an instance-wide
 *     cap; it must never override one an administrator has lowered. Pass the
 *     live instance cap in `globalCapBytes`; pass the surface's own constant in
 *     `surfaceMegabytes` (from the feature's limits class — never a literal).
 *   • The caller owns the confirmation in front of *removal*; this dialog only
 *     attaches and replaces.
 *
 * Accessibility, all inherited or explicit:
 *   • The shell is `Modal`, so role, focus trap, focus return and Escape come
 *     for free. No new interactive widget is introduced.
 *   • Every control id is generated **per instance** (`React.useId`). Hardcoded
 *     ids were safe in a single-mount dialog and are not safe here: two mounts
 *     would break `<label for>` and `aria-describedby` on both.
 *   • Zoom / Horizontal / Vertical are three labelled `<input type="range">`
 *     controls, each arrow/Home/End operable by construction. Pointer drag on
 *     the canvas is an **additional** affordance, never the only one.
 *   • The `<canvas>` is `aria-hidden`; a canvas exposes no accessible structure,
 *     so the preview state is written out as text beside it.
 *   • One validation rule: a failure attributable to a control renders on that
 *     control (`aria-invalid` + `aria-describedby` + focus, no `role="alert"`,
 *     so it is announced once). A server / network failure goes to the
 *     `role="alert"` region, carries the message verbatim, and does not move
 *     focus — the crop is never lost.
 */

/** Source + stored caps shared by every crop surface. A feature's own limits
 *  class (e.g. `UserProfileImageLimits`) supplies the stored megabyte cap; the
 *  rest of the shape is identical everywhere and lives here once. */
export const IMAGE_CROP_LIMITS = {
  maxSourceBytes: 20 * 1024 * 1024,
  maxSourceDimension: 8192,
  outputDimension: 512,
  jpegQuality: 0.85,
  types: ['image/png', 'image/jpeg', 'image/webp'],
  typeLabel: 'PNG, JPEG or WebP',
};

const odcCropMb = (bytes) => `${Math.round((bytes / (1024 * 1024)) * 10) / 10} MB`;

export function ImageCropDialog({
  open = true,
  title,
  subject = 'picture',
  fit = 'cover',
  emptyIcon = 'person',
  limits = IMAGE_CROP_LIMITS,
  surfaceMegabytes = 2,
  globalCapBytes,
  currentSrc = null,
  note,
  onUpload,
  onCancel,
  onSaved,
}) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { Modal, Button, MIcon, FieldShell, FileUpload } = NS;
  const { useState, useRef, useEffect, useCallback } = React;

  const L = limits;
  const contain = fit === 'contain';
  // min(), never max(): a surface tightens, it never widens.
  const effectiveCap = Math.min(globalCapBytes || Infinity, surfaceMegabytes * 1024 * 1024);

  const uid = React.useId().replace(/[^a-zA-Z0-9]/g, '');
  const ids = { file: `crop-file-${uid}`, zoom: `crop-zoom-${uid}`, x: `crop-x-${uid}`, y: `crop-y-${uid}` };

  const [src, setSrc] = useState(currentSrc);
  const [img, setImg] = useState(null);
  const [zoom, setZoom] = useState(100);
  const [px, setPx] = useState(0);
  const [py, setPy] = useState(0);
  const [busy, setBusy] = useState(false);
  const [fileError, setFileError] = useState(null);
  const [rows, setRows] = useState([]);
  const [serverError, setServerError] = useState(null);
  const [live, setLive] = useState('');
  const canvasRef = useRef(null);
  const dropRef = useRef(null);
  const dragRef = useRef(null);

  useEffect(() => {
    if (!src) { setImg(null); return; }
    const i = new Image();
    i.onload = () => setImg(i);
    i.onerror = () => { setImg(null); setFileError(`Unable to read that image. Choose a ${L.typeLabel} file.`); };
    i.src = src;
  }, [src, L.typeLabel]);

  const draw = useCallback(() => {
    const cv = canvasRef.current;
    if (!cv) return;
    const S = L.outputDimension;
    cv.width = S; cv.height = S;
    const g = cv.getContext('2d');
    g.clearRect(0, 0, S, S);
    if (!img) return;
    const base = contain
      ? Math.min(S / img.naturalWidth, S / img.naturalHeight)
      : Math.max(S / img.naturalWidth, S / img.naturalHeight);
    const scale = base * (zoom / 100);
    const dw = img.naturalWidth * scale, dh = img.naturalHeight * scale;
    const slackX = Math.max(0, (dw - S) / 2), slackY = Math.max(0, (dh - S) / 2);
    g.drawImage(img, (S - dw) / 2 + (px / 100) * slackX, (S - dh) / 2 + (py / 100) * slackY, dw, dh);
  }, [img, zoom, px, py, contain, L.outputDimension]);

  useEffect(() => { draw(); }, [draw]);

  // An attributable failure moves focus to the control it belongs to.
  useEffect(() => {
    if (!fileError) return;
    const zone = dropRef.current && dropRef.current.querySelector('.odc-upload-drop');
    if (zone) zone.focus();
  }, [fileError]);

  if (!Modal || !Button || !FileUpload) return null;

  const reject = (msg) => { setFileError(msg); setRows([]); };

  const pick = (file) => {
    setServerError(null);
    if (!file) return;
    if (L.types.indexOf(file.type) < 0) {
      reject(`That file is a ${file.type || 'unknown type'}. Choose a ${L.typeLabel} image.`);
      return;
    }
    if (file.size > L.maxSourceBytes) {
      reject(`That file is ${odcCropMb(file.size)}. Choose an image of ${odcCropMb(L.maxSourceBytes)} or less.`);
      return;
    }
    setFileError(null);
    setRows(FileUpload.filesFromList ? FileUpload.filesFromList([file]) : []);
    setZoom(100); setPx(0); setPy(0);
    setSrc(URL.createObjectURL(file));
  };

  const reset = () => { setZoom(100); setPx(0); setPy(0); };

  const save = async () => {
    if (!img) { setFileError(`Choose an image to use as this ${subject}.`); return; }
    setServerError(null);
    setBusy(true);
    setLive(`Uploading ${subject}…`);
    const encoded = canvasRef.current.toDataURL(contain ? 'image/png' : 'image/jpeg', L.jpegQuality);
    const result = onUpload ? await onUpload(encoded) : { ok: true };
    setBusy(false);
    if (result && result.ok === false) {
      setLive('');
      setServerError(result.message || `Unable to save the ${subject}. Try again.`);
      return;
    }
    setLive(`${subject.charAt(0).toUpperCase()}${subject.slice(1)} saved`);
    if (onSaved) onSaved(encoded, (result && result.version) || undefined);
  };

  const onDown = (e) => { if (!img) return; dragRef.current = { x: e.clientX, y: e.clientY, px, py }; };
  const onMove = (e) => {
    const d = dragRef.current;
    if (!d) return;
    const k = 0.55;
    setPx(Math.max(-100, Math.min(100, d.px - (e.clientX - d.x) * k)));
    setPy(Math.max(-100, Math.min(100, d.py - (e.clientY - d.y) * k)));
  };
  const endDrag = () => { dragRef.current = null; };

  const pct = (v) => `${v} %`;
  const offX = (v) => (v === 0 ? 'centred' : `${Math.abs(v)} % ${v < 0 ? 'left of centre' : 'right of centre'}`);
  const offY = (v) => (v === 0 ? 'centred' : `${Math.abs(v)} % ${v < 0 ? 'above centre' : 'below centre'}`);
  const stateText = img
    ? `Showing ${px === 0 && py === 0 ? 'the centre' : 'an off-centre area'} of your ${subject} at ${zoom} %.`
    : `No image chosen yet. The ${subject} is ${contain ? 'contained, never cropped, on a neutral ground' : 'cropped to a square and shown as a circle'}.`;

  return (
    <Modal
      open={open}
      title={title || `Crop ${subject}`}
      subtitle={`${L.typeLabel} · up to ${odcCropMb(L.maxSourceBytes)} · stored at ${L.outputDimension} × ${L.outputDimension} · max ${odcCropMb(effectiveCap)}`}
      icon="crop"
      className="odc-crop-dialog"
      onClose={onCancel}
      footer={<>
        <Button variant="text" onClick={onCancel}>Cancel</Button>
        <Button variant="filled" icon="check" loading={busy} onClick={save}>Save</Button>
      </>}>
      <div className="odc-crop-body" aria-busy={busy || undefined}>
        {serverError ? (
          <div className="odc-crop-alert" role="alert">
            {MIcon ? <MIcon name="error_outline" size={18} /> : null}
            <span>{serverError}</span>
          </div>
        ) : null}

        {FieldShell ? (
          <FieldShell label={img ? 'Source image' : `Choose a ${subject}`}>
            <div ref={dropRef}>
              <FileUpload
                id={ids.file}
                compact
                multiple={false}
                showKinds={false}
                accept={L.types.join(',')}
                files={rows}
                onChange={(next) => { setRows(next); if (!next.length) { setSrc(null); setFileError(null); } }}
                onFiles={(files) => pick(files[0])}
                error={fileError || undefined}
                hint={`${L.typeLabel} · up to ${odcCropMb(L.maxSourceBytes)} · ${L.maxSourceDimension} × ${L.maxSourceDimension} px · one image`} />
            </div>
          </FieldShell>
        ) : null}

        <div className="odc-crop-stage">
          <div className={`odc-crop-frame${contain ? ' ground' : ' round'}`}
            onPointerDown={onDown} onPointerMove={onMove} onPointerUp={endDrag} onPointerLeave={endDrag}>
            <canvas ref={canvasRef} aria-hidden="true" />
            {!img && MIcon ? <span className="odc-crop-frame-empty" aria-hidden="true"><MIcon name={emptyIcon} size={30} /></span> : null}
          </div>

          <div className="odc-crop-controls">
            <p className="odc-crop-state">{stateText}</p>
            <label className="odc-crop-ctl" htmlFor={ids.zoom}>
              <span className="odc-crop-ctl-l">Zoom<b>{pct(zoom)}</b></span>
              <input id={ids.zoom} type="range" className="odc-range" min={100} max={300} step={5} value={zoom} disabled={!img}
                aria-valuetext={pct(zoom)} onChange={(e) => setZoom(Number(e.target.value))} />
            </label>
            <label className="odc-crop-ctl" htmlFor={ids.x}>
              <span className="odc-crop-ctl-l">Horizontal<b>{offX(px)}</b></span>
              <input id={ids.x} type="range" className="odc-range" min={-100} max={100} step={2} value={px} disabled={!img}
                aria-valuetext={offX(px)} onChange={(e) => setPx(Number(e.target.value))} />
            </label>
            <label className="odc-crop-ctl" htmlFor={ids.y}>
              <span className="odc-crop-ctl-l">Vertical<b>{offY(py)}</b></span>
              <input id={ids.y} type="range" className="odc-range" min={-100} max={100} step={2} value={py} disabled={!img}
                aria-valuetext={offY(py)} onChange={(e) => setPy(Number(e.target.value))} />
            </label>
            <div className="odc-crop-reset">
              <Button variant="text" icon="restart_alt" onClick={reset}>Reset</Button>
            </div>
          </div>
        </div>

        <p className="odc-crop-note">{note || (contain
          ? 'Saved as a PNG with its transparency and shown contained on a neutral ground — a wordmark is never cut. Metadata is removed from the stored file.'
          : 'Saved as a square JPEG and shown as a circle. Location, capture time and device details are removed from the stored file.')}</p>
        <span className="odc-crop-live" role="status" aria-live="polite">{live}</span>
      </div>
    </Modal>
  );
}
