/* =============================================================
   ProfilePicture.jsx — the User Profile Picture feature, kit side.

   One picture per user, owned by the user. Three surfaces:
     • /account  — the control in the profile card (ProfilePictureControl)
                   + the page-header mark (Account.jsx renders it)
     • /users    — the admin list row mark and the detail-panel mark
                   (Users.jsx, via kitUserProfileImage)

   What this file is faithful to:
   - **The ImageVersion token drives every state**, never a probe of the byte
     endpoint. `version == null` means "render the monogram and issue no
     request"; a non-null token means "renderable by this caller" and is the
     cache key that re-keys the image URL on a replace.
   - The token belongs to the PAGE, not to this control: Account.jsx holds it
     and feeds both the control and the header, so an upload updates both
     without a reload. A section-local token would satisfy the upload and leave
     the header stale.
   - **Storage is separate from the domain file store.** Nothing here goes near
     the Files surfaces; the read URL is its own resource,
     `/api/profile-images/{userId}?v={version}`, and the writes are self-scoped
     with no id at all (`POST` / `DELETE /api/profile/image`).
   - Every number comes from PROFILE_IMAGE_LIMITS (the kit's stand-in for
     `UserProfileImageLimits`) tightened against the live instance cap. No
     literal cap at a call site.
   - The crop dialog is the shared DS `ImageCropDialog`, the same component the
     contact image mounts — not a second cropper.
   - An administratively **disabled** account reads as absent: the projection
     nulls the token, so no <img> is emitted and nothing flickers. A transient
     lockout does NOT hide a picture (that would leak a failed-login signal).
   ============================================================= */

/* ---- UserProfileImageLimits (Odyssey.Dtos.Application) ---- */
const PROFILE_IMAGE_LIMITS = {
  // Stored — the server is authoritative. Effective cap is
  // min(instance upload cap, maxImageBytes): a surface tightens, never widens.
  maxImageBytes: 2 * 1024 * 1024,
  maxImageMegabytes: 2,          // the whole-megabyte form TightenTo() takes
  maxImageDimension: 1024,
  // Source — browser-side only, what the crop dialog will open.
  maxSourceBytes: 20 * 1024 * 1024,
  maxSourceDimension: 8192,
  // What the crop writes.
  outputDimension: 512,
  jpegQuality: 0.85,
  types: ['image/png', 'image/jpeg', 'image/webp'],
  typeLabel: 'PNG, JPEG or WebP',
};

/* The read URL — composed in one place, exactly as the typed API client does
   (`ProfileImagesApiClient.ImageUrl(userId, version)`). `v` is a cache key
   only: the action does not bind it, so it can never affect the response. */
const profileImageUrl = (userId, version) => `/api/profile-images/${userId}?v=${version}`;

/* Deterministic stand-in imagery — a striped placeholder with a monospace
   caption, so nothing pretends to be a photograph the kit does not have. */
const PROFILE_IMAGE_CACHE = {};
const profileImagePlaceholder = (hue = 150, label = 'PORTRAIT') => {
  const key = `${hue}:${label}`;
  if (PROFILE_IMAGE_CACHE[key]) return PROFILE_IMAGE_CACHE[key];
  const S = 512, c = document.createElement('canvas');
  c.width = S; c.height = S;
  const g = c.getContext('2d');
  g.fillStyle = `oklch(0.32 0.05 ${hue})`;
  g.fillRect(0, 0, S, S);
  g.strokeStyle = `oklch(0.80 0.14 ${hue} / 0.42)`;
  g.lineWidth = 6;
  for (let i = -S; i < S * 2; i += 26) { g.beginPath(); g.moveTo(i, 0); g.lineTo(i + S, S); g.stroke(); }
  g.fillStyle = 'oklch(0.94 0.02 200)';
  g.font = `500 ${Math.round(S * 0.085)}px ui-monospace, monospace`;
  g.textAlign = 'center'; g.textBaseline = 'middle';
  g.fillText(label, S / 2, S / 2);
  PROFILE_IMAGE_CACHE[key] = c.toDataURL('image/png');
  return PROFILE_IMAGE_CACHE[key];
};

/* `ExistingUser.ProfileImageVersion`, per admin row — the projection every
   /users surface reads. Two rules live here, both load-bearing:
     • the token is nulled for an administratively DISABLED subject, because
       the read path 404s one; nulling it in the projection is what stops a
       row rendering an <img>, taking the 404 and flickering to a monogram.
     • it is NOT derived from `enabled` in general: a transient lockout leaves
       the picture alone. Deriving one from the other would turn the read into
       a password-spray confirmation channel.
   Whether a seeded user has a picture at all is deterministic from the id. */
const kitUserProfileImage = (u) => {
  if (!u || !u.id) return { version: null, src: null };
  const n = parseInt(u.id.slice(0, 4), 16);
  const has = n % 3 !== 0;
  if (!has || u.enabled === false) return { version: null, src: null };
  const version = u.id.slice(0, 8);
  return {
    version,
    src: profileImagePlaceholder(90 + (n % 5) * 55),
    url: profileImageUrl(u.id, version),
  };
};

/* =============================================================
   ProfilePictureControl — the /account write surface.

   It owns the crop dialog, the removal confirmation and its own polite
   announcement; it does NOT own the token (the page does, so the header
   re-renders with it). Removal is behind a confirmation because it destroys
   the bytes — there is no image history and no "previous pictures".
   ============================================================= */
const ProfilePictureControl = ({
  userId,
  initials,
  version,
  src,
  onChange,          // (version, src) — null, null clears
  claimStale = false, // the caller's session predates profile-images.read
  globalCapBytes = 64 * 1024 * 1024,
  uploadOutcome = 'ok',
  variant = 'row',
}) => {
  const { useState } = React;
  const DS = window.OdysseyDesignSystem_d5aa51 || {};
  const { ProfilePictureField, ImageCropDialog, IMAGE_CROP_LIMITS } = DS;
  const [cropping, setCropping] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const [removing, setRemoving] = useState(false);
  const [live, setLive] = useState('');

  const L = PROFILE_IMAGE_LIMITS;
  const cap = Math.min(globalCapBytes, L.maxImageBytes);
  const mb = (b) => `${Math.round((b / (1024 * 1024)) * 10) / 10} MB`;

  // The upload delegate. A rejection carries the server's own ProblemDetails
  // message, which names the actual limit — never a compiled-in literal.
  const upload = () => new Promise((res) => setTimeout(() => {
    if (uploadOutcome === 'too-large') {
      res({ ok: false, message: `Unable to save the picture. The image must be ${mb(cap)} or smaller. Crop a smaller area, or choose a different file.` });
    } else if (uploadOutcome === 'rate-limited') {
      res({ ok: false, message: 'Unable to save the picture. You have changed it too many times in the last few minutes. Try again in 4 minutes.' });
    } else if (uploadOutcome === 'conflict') {
      res({ ok: false, message: 'Unable to save the picture. It was changed in another tab a moment ago. Reload the page and try again.' });
    } else {
      // 200 with the NEW ImageVersion — not 204: the client needs the token to
      // re-key the image URL, or the browser never re-requests.
      res({ ok: true, version: Math.random().toString(16).slice(2, 10) });
    }
  }, 700));

  const remove = () => {
    setRemoving(true);
    setTimeout(() => {
      setRemoving(false);
      setConfirming(false);
      onChange(null, null);
      setLive('Profile picture removed');
    }, 600);
  };

  if (!ProfilePictureField) return null;

  return (
    <div className={`acc-picture${variant === 'overlay' ? ' overlay' : ''}`}>
      <ProfilePictureField
        variant={variant}
        version={version}
        src={src}
        initials={initials}
        alt="Your profile picture"
        disabled={claimStale}
        disabledReason={claimStale ? 'Sign out and back in to manage your profile picture.' : undefined}
        removing={removing}
        onAdd={() => setCropping(true)}
        onRemove={() => setConfirming(true)} />

      {variant === 'overlay' ? (
        claimStale ? <p className="acc-picture-meta">Sign out and back in to manage your profile picture.</p> : null
      ) : (
        <p className="acc-picture-meta">
          {claimStale
            ? 'Sign out and back in to manage your profile picture.'
            : version
              ? <>Stored at {L.outputDimension} × {L.outputDimension} · served from <code>{profileImageUrl(userId, version)}</code></>
              : <>{L.typeLabel} · up to {mb(cap)} · stored at {L.outputDimension} × {L.outputDimension}, square</>}
        </p>
      )}

      {cropping && ImageCropDialog ? (
        <ImageCropDialog
          title="Crop your profile picture"
          subject="picture"
          fit="cover"
          emptyIcon="person"
          limits={IMAGE_CROP_LIMITS || L}
          surfaceMegabytes={L.maxImageMegabytes}
          globalCapBytes={globalCapBytes}
          onUpload={upload}
          onSaved={(dataUrl, newVersion) => {
            setCropping(false);
            onChange(newVersion || Math.random().toString(16).slice(2, 10), dataUrl);
            setLive('Profile picture saved');
          }}
          onCancel={() => setCropping(false)} />
      ) : null}

      {confirming ? (
        <Modal
          title="Remove profile picture?"
          icon="delete_outline"
          iconTone="error"
          onClose={() => { if (!removing) setConfirming(false); }}
          footer={<>
            <Button variant="text" disabled={removing} onClick={() => setConfirming(false)}>Cancel</Button>
            <Button variant="filled" className="danger" color="" icon="delete_outline" loading={removing} onClick={remove}>Remove picture</Button>
          </>}>
          <div className="col gap-3">
            <div style={{ font: '400 14px/1.65 var(--font-sans)', color: 'var(--mud-palette-text-primary)' }}>
              The stored image is deleted, not hidden — your initials come back everywhere you appear,
              on this page and in the administrator's user list. There is no picture history, so this
              cannot be undone. You can add a new picture at any time.
            </div>
          </div>
        </Modal>
      ) : null}

      <span className="acc-picture-live" role="status" aria-live="polite">{live}</span>
    </div>
  );
};

Object.assign(window, {
  PROFILE_IMAGE_LIMITS, profileImageUrl, profileImagePlaceholder,
  kitUserProfileImage, ProfilePictureControl,
});
