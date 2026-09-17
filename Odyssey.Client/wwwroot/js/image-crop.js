// Client-side crop + downscale for the product's ONE image-crop dialog (issue #86 §5.5, issue #94 §5),
// following the existing interop-module pattern (cal-timegrid.js, overlay-focus.js). Two surfaces mount
// it: a contact's image/logo, and the signed-in user's own profile picture.
//
// WHAT THIS IS FOR. The dialog normalises what gets uploaded: a square cover crop for a Person, a
// contained canvas for an Organization (a wordmark is never cut). That keeps the stored bytes small
// and square, and the canvas re-encode also applies EXIF Orientation and drops metadata on the normal
// path — the SERVER strip is still the guarantee, because a direct API caller never runs any of this.
//
// WHAT IT RETURNS. The encoded crop comes back as a Blob behind an IJSStreamReference, never as a
// byte[]: a byte[] crossing JS interop is base64-marshalled, inflating a 2 MB payload by a third.
//
// Every number is passed in from the surface's limits class on the .NET side. Nothing here holds a cap.

const state = new Map();

/** The decoded source for one dialog instance, plus the object URL that has to be revoked. */
function slot(handle) {
    let entry = state.get(handle);
    if (!entry) {
        entry = { image: null, objectUrl: null };
        state.set(handle, entry);
    }
    return entry;
}

/**
 * Decodes a picked file against the SOURCE pixel cap, which is the browser's alone — the server has
 * its own, much tighter, stored caps and does not trust any of this.
 *
 * The bytes arrive as a DotNetStreamReference rather than a byte[]: a byte[] crossing interop is
 * base64-marshalled, which would inflate a 20 MB source by a third in each direction. The type and
 * byte caps are checked on the .NET side, where IBrowserFile already knows both without reading a
 * single byte.
 *
 * Returns a plain object rather than throwing, so the caller can render the rejection on the control
 * it belongs to.
 */
export async function load(handle, stream, contentType, maxSourceDimension) {
    const entry = slot(handle);
    release(handle);

    let objectUrl;
    try {
        const buffer = await stream.arrayBuffer();
        objectUrl = URL.createObjectURL(new Blob([buffer], { type: contentType }));
    } catch {
        return { ok: false, reason: 'undecodable' };
    }

    let image;
    try {
        image = await decode(objectUrl);
    } catch {
        URL.revokeObjectURL(objectUrl);
        return { ok: false, reason: 'undecodable' };
    }

    if (image.naturalWidth > maxSourceDimension || image.naturalHeight > maxSourceDimension) {
        URL.revokeObjectURL(objectUrl);
        return { ok: false, reason: 'dimensions', width: image.naturalWidth, height: image.naturalHeight };
    }

    entry.image = image;
    entry.objectUrl = objectUrl;
    return { ok: true, width: image.naturalWidth, height: image.naturalHeight };
}

function decode(src) {
    return new Promise((resolve, reject) => {
        const image = new Image();
        image.onload = () => resolve(image);
        image.onerror = () => reject(new Error('decode'));
        image.src = src;
    });
}

/**
 * Paints the current crop into the preview canvas.
 *
 * `cover` scales so the SHORTER side fills the frame and the overflow is cropped — a photograph.
 * `contain` scales so the LONGER side fits and the rest is transparent ground — a logo, which is
 * rarely square and must never be cut.
 *
 * `zoom` is a percentage over that base scale; `offsetX`/`offsetY` are percentages of the available
 * slack, so the three range inputs cover exactly the reachable area and no more.
 */
export function draw(handle, canvas, size, cover, zoom, offsetX, offsetY) {
    if (!canvas) {
        return;
    }

    canvas.width = size;
    canvas.height = size;
    const context = canvas.getContext('2d');
    context.clearRect(0, 0, size, size);

    const entry = state.get(handle);
    const image = entry && entry.image;
    if (!image) {
        return;
    }

    const base = cover
        ? Math.max(size / image.naturalWidth, size / image.naturalHeight)
        : Math.min(size / image.naturalWidth, size / image.naturalHeight);
    const scale = base * (zoom / 100);
    const width = image.naturalWidth * scale;
    const height = image.naturalHeight * scale;
    const slackX = Math.max(0, (width - size) / 2);
    const slackY = Math.max(0, (height - size) / 2);

    context.drawImage(
        image,
        (size - width) / 2 + (offsetX / 100) * slackX,
        (size - height) / 2 + (offsetY / 100) * slackY,
        width,
        height);
}

/**
 * Encodes the canvas and hands back a Blob for IJSStreamReference. JPEG for a person's photograph,
 * PNG for a logo — PNG keeps the transparency the contained frame relies on, and a wordmark
 * re-encoded as JPEG picks up ringing around its edges.
 */
export function encode(canvas, contentType, quality) {
    return new Promise((resolve, reject) => {
        if (!canvas) {
            reject(new Error('no canvas'));
            return;
        }

        canvas.toBlob(
            blob => (blob ? resolve(blob) : reject(new Error('encode failed'))),
            contentType,
            quality);
    });
}

/** Frees the decoded source and its object URL. Called on dispose and before each new pick. */
export function release(handle) {
    const entry = state.get(handle);
    if (!entry) {
        return;
    }

    if (entry.objectUrl) {
        URL.revokeObjectURL(entry.objectUrl);
    }

    entry.image = null;
    entry.objectUrl = null;
}

/** Drops the instance entirely, so a long-lived page does not accumulate one entry per dialog open. */
export function dispose(handle) {
    release(handle);
    state.delete(handle);
}

/**
 * Moves focus to a control inside a host element. Used for the one validation-failure rule: a failure
 * attributable to a control moves focus to that control, and the shared upload field's dropzone is
 * inside a component whose internals the caller holds no ElementReference to.
 */
export function focusWithin(host, selector) {
    const target = host && host.querySelector(selector);
    if (target && typeof target.focus === 'function') {
        try { target.focus(); } catch { /* removed between render and focus; ignore */ }
    }
}
