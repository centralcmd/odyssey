/**
 * Odyssey DS — ProfilePictureField
 * ---------------------------------------------------------------------------
 * The identity control on a person's **own** account page: their picture, the
 * button that attaches or replaces it, and the button that removes it. It is
 * the only write surface for a user profile picture — there is no
 * administrator path, by design, so this field never takes a target user.
 *
 * Presence is driven by `version` (the read DTO's image-version token), never
 * by probing the byte endpoint: a non-null token means "renderable by this
 * caller", and it is also the cache key that re-keys `src` on a replace. With
 * no token the field renders the monogram and issues no request, and the button
 * reads **Add picture**; with one it reads **Change picture** and a **Remove
 * picture** button appears beside it.
 *
 * A load failure is silent. `Avatar.onError` swaps back to the monogram and
 * nothing is surfaced — an identity token that cannot load is not the user's
 * problem to act on. The failed state is per-`version`, so a replace clears it.
 *
 * `disabled` + `disabledReason` cover the one honest dead end: a session that
 * predates the read claim's deploy can upload successfully and see nothing, so
 * the control is disabled rather than accepting a write it cannot show. The
 * same disable covers **Remove**, which is a self-service erasure control — so
 * the reason must say how to clear it ("Sign out and back in…"), and it must
 * never be phrased as a failure the user caused.
 *
 * Size is `lg` (56 px), the same step the page header uses, and it is not
 * configurable: `Avatar` writes its size inline, so a page cannot enlarge it
 * without a new size step on the atom itself.
 *
 * Two variants. `row` (default) is the labelled control with text buttons —
 * the discoverable default for a settings page. `overlay` makes the mark
 * itself the control: the Change / Remove actions sit on the picture and
 * appear on hover — an edit pencil, plus a remove action where there is
 * something to remove — for a card that already shows the avatar and cannot
 * spare a second block. Hover is never the only affordance — the buttons are
 * always-rendered, focusable buttons (so `:focus-within` reveals them for
 * keyboard and touch) and a small pencil badge stays visible.
 *
 * The confirmation in front of removal belongs to the caller — the field raises
 * `onRemove` and nothing else, so the copy stays with the page that knows whose
 * picture it is. Announce the completed removal through a live region
 * (`LiveAnnouncer`); this field does not own one.
 */
export function ProfilePictureField({
  src = null,
  version = null,
  initials,
  alt = 'Your profile picture',
  tone = 'tide',
  label = 'Profile picture',
  hint,
  busy = false,
  removing = false,
  disabled = false,
  disabledReason,
  variant = 'row',
  onAdd,
  onRemove,
  className = '',
}) {
  const NS = (typeof window !== 'undefined' && window.OdysseyDesignSystem_d5aa51) || {};
  const { Avatar, Button, MIcon } = NS;
  const [failed, setFailed] = React.useState(false);
  React.useEffect(() => { setFailed(false); }, [version, src]);
  if (!Avatar || !Button) return null;

  const has = !!(version && src && !failed);
  const showImage = !!(version && src) && !failed;
  const mark = showImage
    ? <Avatar size="lg" src={src} alt={alt} onError={() => setFailed(true)} />
    : <Avatar size="lg" initials={initials} tone={tone} alt={alt} />;

  /* variant="overlay" — the mark IS the control: the actions sit on top of the
     picture and appear on hover. Hover alone is never an affordance here, so
     two things are always true: the buttons are real, always-rendered buttons
     (so Tab reaches them and `:focus-within` reveals the overlay, which is
     also what makes them work on touch), and a small camera badge stays
     visible so the control is discoverable without hovering at all. */
  if (variant === 'overlay') {
    return (
      <div className={`odc-picedit${disabled ? ' disabled' : ''}${className ? ' ' + className : ''}`}>
        {mark}
        {!disabled ? (
          <>
            <span className="odc-picedit-badge" aria-hidden="true">
              <span className="material-icons">edit</span>
            </span>
            <span className="odc-picedit-actions">
              <button type="button" className="odc-picedit-btn" onClick={onAdd} disabled={busy || removing}
                aria-label={has ? 'Change profile picture' : 'Add profile picture'}
                title={has ? 'Change profile picture' : 'Add profile picture'}>
                <span className="material-icons" aria-hidden="true">edit</span>
              </button>
              {has ? (
                <button type="button" className="odc-picedit-btn danger" onClick={onRemove} disabled={busy || removing}
                  aria-label="Remove profile picture" title="Remove profile picture">
                  <span className="material-icons" aria-hidden="true">delete_outline</span>
                </button>
              ) : null}
            </span>
          </>
        ) : null}
      </div>
    );
  }

  return (
    <div className={`odc-picfield${disabled ? ' disabled' : ''}${className ? ' ' + className : ''}`}>
      {mark}
      <div className="odc-picfield-body">
        <div className="odc-picfield-label">{label}</div>
        <p className="odc-picfield-hint">{hint || (has
          ? 'Shown wherever you appear in this workspace. Remove it at any time and your initials come back.'
          : 'Your initials are shown until you add one.')}</p>
        <div className="odc-picfield-actions">
          <Button variant="outlined" icon="photo_camera" disabled={disabled || busy || removing} onClick={onAdd}>
            {has ? 'Change picture' : 'Add picture'}
          </Button>
          {has ? (
            <Button variant="text" icon="delete_outline" loading={removing}
              disabled={disabled || busy || removing} onClick={onRemove}>Remove picture</Button>
          ) : null}
        </div>
        {disabled && disabledReason ? (
          <p className="odc-picfield-note">
            {MIcon ? <MIcon name="info" size={15} /> : null}
            <span>{disabledReason}</span>
          </p>
        ) : null}
      </div>
    </div>
  );
}
