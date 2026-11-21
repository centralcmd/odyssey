# Contact avatars and user profile pictures

Part of the architecture rules split out of the root [`CLAUDE.md`](../../CLAUDE.md). Read this before changing code in this area; the rules here are binding in the same way.

**A contact carries ONE image, and what bounds it is what licenses the claim gate** (issue #86).
`Contact.AvatarFileId` points at a `FileMetadata` row in the `ContactAvatars` area of the object store
(see [file-storage.md](file-storage.md)) — no avatar-specific table. Three
endpoints (`GET`/`POST`/`DELETE /api/contacts/{id}/avatar`) are gated on `contacts.read` and
`contacts.update` **alone**, deliberately not also `files.read`/`.create`/`.delete`. The read side is
contact data and takes no file id, so the claim buys that contact's image and nothing else. The write
side rests on **boundedness**: what `contacts.update` confers here is ≤ 2 MB, one of three allow-listed
still image types, magic-byte-checked, ≤ 1024², non-animated, metadata-stripped and re-validated, with a
server-generated filename and description, at one row per contact. **That is an invariant, not a
one-time judgement** — a change that widens any of it re-opens the claim-gating question rather than
inheriting this answer.

Rules around it that are easy to get backwards:

- **The endpoint takes file BYTES, never a `fileId`.** A caller-supplied id would let a
  `contacts.update` holder point a contact at any row in the Files store and read its bytes back
  through the `contacts.read`-gated download — an arbitrary-file-read primitive assembled from two
  innocuous claims. "Pick an existing file" is a non-goal for that reason, not for want of convenience.
- **THREE paths release an avatar file, and all three call one rule** (`ContactAvatarRelease`): the
  explicit `DELETE`, the contact-delete cascade, and **the replace half of `POST`**. An earlier draft
  guarded the first two and missed the third, so a contact mis-pointed at a PDF would have had that PDF
  destroyed by the next upload — the likeliest of the three to be reached, since replacing an image is
  ordinary and deleting one is not. A file outside the avatar allow-list is **detached and logged,
  never deleted**.
- **Metadata removal is an allow-list walk, and the guarantee is a property of the OUTPUT.** The walk
  (`ImageContainerWalk`) produces the stripped bytes, the dimensions and the animation determination in
  one pass, so no raster library and no full EXIF parser sit on the avatar and profile-image path (photo
  thumbnails are the one deliberate exception, and only in the worker — see [file-storage.md](file-storage.md)) —
  `MetadataExtractor` appears only in **tests**, where it is the independent oracle that makes
  strip-then-verify meaningful. The runtime re-validation is a second pass of the same walk and on its
  own is only a self-check; **keep both**. Every `APPn` is dropped, `APP0` included: JFIF carries a
  thumbnail and `JFXX` embeds its own, so the one marker a keep-list is tempted to retain is the one
  that can still smuggle a second image.
- **Animated containers are rejected, never flattened**, and an unreadable one is rejected rather than
  stored — for this slot an unparseable container is a defect, not a degraded read.
- **The read path is `no-cache` with a strong `ETag`, resolved from `FileMetadata` alone.** That makes
  revalidation the hot path, which is what buys immediate revocation and erasure — so opening the
  stored file to answer a conditional request would make every return visit pay full materialisation for
  a body it never sends. The response carries `Cross-Origin-Resource-Policy: **same-site**`, not
  `same-origin`: CORP is enforced on no-cors subresource loads (exactly how an `<img src>` loads) and
  compares scheme, host **and port**, so `same-origin` would block every avatar outside Docker — and
  silently, since a load failure degrades to the type glyph.

**Every image action lives in the record row's `⋯` menu — there is no identity tile and no image
field in the create/edit dialog.** The design system (commit 5234c6e) is newer than issue #86's §3 and
was chosen over it deliberately, so a later reader finding the spec's tile unimplemented is looking at
a decision, not an omission. What that buys is not layout: the spec's create-dialog field forces a
two-step write that can half-fail — contact created, image rejected — and AC 23's
`CreatedWithoutImage` partial-success state with its Retry affordance exists only to paper over that.
Attaching from the row menu means the contact always exists first, so **that state has nothing to
represent and is not implemented**. AC 36's "identity tile alt text" has no surface for the same
reason; the list-card image is decorative (`alt=""`), which §3 requires anyway. Reinstating the tile
re-opens the partial-success state along with it.

**A user's profile picture is stored SEPARATELY from the domain file store, and that separation is a
security boundary** (issue #94). `UserProfileImage` / `UserProfileImageBlob` sit beside `UserProfile`
in `Odyssey.Context` with **zero relationships in either direction** to `FileMetadata` or the object store. A
holder of `files.read` cannot read a person's face, `files.delete` cannot destroy it, and
`AdminFileExportService` — which enumerates `FileMetadata` and pulls the matching files wholesale — cannot
sweep one into an admin file export. The cost is a standing tax: every storage-wide concern
(encryption at rest, quota accounting, orphan sweeps, backup tooling) now has two places to be applied.
An integration test asserts the zero-FK property in both directions, because one convenience key added
later would quietly undo all of it.

The duplication is accepted for **storage, limits and the read path**, and explicitly **not** for the
**validation pipeline**: two copies of a parser on an untrusted-input path will diverge, and the copy
with fewer eyes on it is the one that will. So `Odyssey.Core/Imaging/` holds the solution's **one**
container walk, metadata strip, magic-byte table and parameterised validator (`StillImageValidator` +
`StillImagePolicy`); `ContactAvatarValidator` and `UserProfileImageValidator` are thin callers that
supply their own caps. A source-lint asserts exactly one walk exists and that its scan set is non-empty
— the lints are path-scoped, and `ContactAvatarService`/`ContactAvatarRelease` stayed behind in
`Journal/Avatar`, so a lint left pointing at the old directory would have kept **passing** while the
files it protects had silently left its scope.

Six rules around it are easy to get backwards:

- **The blob is the DEPENDENT, deliberately** — the inverse of the Files store's former `FileMetadata`/`FileBlob`
  pair (removed with the legacy file storage), where the *blob* was the principal and the cascade ran blob →
  metadata, never the reverse. Copying that shape would leave every deleted user's
  facial image bytes a permanent orphan. Making the blob the *dependent* gives an unbroken
  `AspNetUsers → UserProfileImage → UserProfileImageBlob` chain, so GDPR Art. 17 erasure holds through
  a bare `userManager.DeleteAsync` inside the existing delete transaction, which carries no purge code.
- **A replace UPDATES the row in place; it never deletes and re-inserts.** Three things break together
  otherwise: EF does not guarantee DELETE-before-INSERT ordering in one `SaveChangesAsync`, so the
  *ordinary* replace path raises a duplicate-key error against the unique index on `UserId` —
  self-inflicted, not a race; the concurrency token becomes dead weight (EF emits no
  `WHERE ImageVersion = @original` on an `INSERT`); and `UserProfileImageId` changes, which is the
  blob's PK *and* FK, so the old blob cascades away and "new blob content" becomes inexpressible.
- **`ImageVersion` is the concurrency token, and that is what makes the `409` reachable at all.** Under
  update-in-place the unique index can only fire for two concurrent *first* uploads; the token catches
  two concurrent *replaces*, where both `UPDATE`s affect one row and the later would silently win. The
  two races are separate integration tests for that reason.
- **The blob write shares the metadata write's `SaveChangesAsync`.** Split across two saves, the loser
  of a replace race commits its *bytes* and is refused on the *metadata*, leaving its image stored under
  the winner's `Sha256Hash` — not a lost update but a silent integrity break, on a read path whose whole
  design rests on revalidation being correct.
- **The read path `404`s an ADMINISTRATIVELY DISABLED subject, and only that.**
  `AccountLockout.IsAdministrativelyDisabled` tests the sentinel; `AccountLockout.IsEnabled` answers
  *sign-in eligibility*, which a **transient** lockout also affects. Substituting one for the other is a
  security defect, not a tidy-up: a deliberate removal never reverts while a lockout reverts in about
  five minutes, so any `profile-images.read` holder polling a known id would observe `200 → 404 → 200`
  and learn that account is under a failed-login lockout — a password-spray oracle outside the login
  endpoint and its rate limiter. The helper is for **materialised entities only**; the read path is a
  join onto `AspNetUsers`, and EF translates no arbitrary static method call, so it compares against
  `AccountLockout.DisabledLockoutEnd` **inline** (still naming the constant, never a literal).
- **`ExistingUser.ProfileImageVersion` means "renderable by this caller", not "a row exists"**, and the
  nulling lives at `UserAdministrationService.MapUserAsync` — the single point every `ExistingUser` is
  projected through, including the row the admin update that *performs* a disable returns. **Do not
  derive it from `ExistingUser.Enabled`**: the two come from different predicates, so a `/users` row can
  legitimately show `Enabled == false` beside a rendering picture. The tempting consistency fix
  reintroduces the transient-lockout oracle quietly.

There is **no administrator write path** — the write endpoints take no user id in route or body at all,
so the IDOR mitigation is structural rather than a check. The remedies for an abusive upload are
disable (which now also stops the read) or delete (which cascades the bytes away), never editing
another person's identity. One operational consequence: **disabling puts the image beyond every
endpoint, the administrator's included**, so evidence must be captured before disabling. Adding a
remove-only admin path later is *the trigger* to add an attribution column, since from that point
uploader and subject can differ.

`profile-images.read` is granted to **every** role, Guest included, so its effective reach equals "any
authenticated caller". What it buys is a **revocation lever that exists before release** — claim values
are baked into the auth cookie at sign-in, so retrofitting one later de-authorizes live sessions. The
premise that would license claim-free access ("a caller can only fetch a picture for someone they
already see *named*") is **false**: `ExistingTransactionFile.AttachedByUserId`,
`ExistingAccountFile.AttachedByUserId` and the nested `ExistingFileMetadata.UploadedByUserId` return raw
user ids, and Guest holds `transactions.read`, `accounts.read` and `files.read`. Issue #106 added the
matching `AttachedByName`/`UploadedByName`, but those are resolved per caller and can be `null`, so the
raw id still reaches callers who cannot see the name.

There is deliberately **no whole-database JSON export** any more — the `data.export` claim, its
endpoint and its Settings row were removed. Reinstating one re-opens this question: such a document
would carry a face to a holder who is never the data subject.
