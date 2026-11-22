# File storage, photos and thumbnails

Part of the architecture rules split out of the root [`CLAUDE.md`](../../CLAUDE.md). Read this before changing code in this area; the rules here are binding in the same way.

**Files on disk go through the filesystem object store, `Odyssey.Core/FileStorage/`** (issue #346).
Deploy-time volumes (`FileStorage:Volumes:<name>:RootPath`) hold the bytes; `StoredObjects` rows say what is
where; code-declared *storage features* (`StorageFeature`, registered in DI, assembled into
`StorageFeatureCatalogue`) are bound to a volume at deploy time (`FileStorage:Bindings:<Feature>`). **The production
catalogue holds seven features, all `Graced`**: `PhotoOriginals` (#428), `PhotoThumbnails` (#362), the four file
areas `FinanceFiles`, `JournalFiles`, `ContactAvatars` (#448) and `LibraryFiles` (#525), and `NewsArticles` (#470).
Every host with volumes must bind all seven (check B1); `FileStorageMaintenanceTests` fails on an `Immediate` production feature. The three
`TestShared/FileStorage` features exercise the store's own tests.
Every file and photo has its bytes on the store: the legacy database copy (`FileBlob`) is gone. Rules a consumer
inherits, and that are easy to get backwards:

- **A `Committed` row always has its file.** `IObjectStore.PutAsync` reserves a `Pending` row through its own
  `IDbContextFactory<OdysseyContext>` context (it commits whatever the caller's transaction does), writes the
  file, then attaches the row to the CALLER's context as `Committed`, unsaved: the caller saves it with its
  referencing row in one `SaveChangesAsync`. `State` is the concurrency token, so a reservation the abandoned
  -write sweep already removed fails the caller's save instead of committing a reference to a deleted file.
  That sweep removes the row **before** the files for the same reason.
- **A referencing table declares a `RESTRICT` key, belongs to exactly one feature (its
  `StorageFeatureReference`), and releases through `StageRelease` in the same save that removes or nulls the
  reference** — never by removing the `StoredObject` row or deleting a file. A cascade-deleted consumer row is
  caught by the hourly unreferenced sweep. `FileStorageMaintenanceTests` reflects over the model and fails on
  a key to `StoredObjects` that breaks this.
- **No caller ever supplies a path, and no endpoint takes a stored-object id.** Bytes are served only by the
  consuming feature's own endpoint, under its own claim (`nosniff`, the database-held content type, range
  processing, the hash as a strong ETag). `OpenReadAsync` 404s another feature's object. A generic
  `GET /api/stored-objects/{id}` would be a cross-claim read primitive.
- **Every filesystem operation re-runs the structural check** (`StorageNames.IsConformingPath`, or the
  catalogue-free variant for a feature this build no longer has), **the containment check and the symlink
  check** — a row may have been edited by hand. A prefix test alone would let a path reach the marker,
  `.tmp`, `.quarantine` or `lost+found`.
- **File names are personal data.** The default naming keeps the sanitized original stem
  (`StoredFileNames.SanitizeStem`, the third and strictest sanitizer); the extension always comes from the
  validated content type. Never log a `RelativePath` or a file name — name an object by its id.
- **`Degraded` is per process.** The API's view is what `GET /api/file-storage/volumes` reports; a request
  re-probes a degraded volume at most every 15 seconds (`FileStorageVolumeRegistry.RecheckInterval`).
- **Startup verification stops the host** on any misconfiguration (`FileStorageVerifier`, checks
  C1–C5/B1–B3/V1–V8; B4, a live row of an unknown feature, only logs a Critical). Only the API writes a
  marker, adopts or initialises. The no-op rule: an EF InMemory host with no volumes, or a relational host
  with no volumes, no features and nothing stored, verifies nothing — which is what keeps every ordinary
  `WebApplicationFactory` in `Odyssey.Api.Tests` unaffected. `FileStorageApiFactory` is the store's own.
- **The deletion grace is mapped by the sweeper, not the resolver.** Anything but a healthy read of
  `FileStorageDeletionGraceHours` resolves to 168 h; `Immediate` features and `immediate: true` releases
  ignore it.

Operator documentation — volumes, bindings, every startup check's remedy, backup order, quarantine and the
breaker — is `docs/deployment.md` → *File storage*.

**Photo thumbnails are rendered in `Odyssey.Worker` with ImageMagick, and nowhere else** (issues #362, #365).
`PhotoThumbnailJob` (`Odyssey.Core/Journal/Thumbnails/`) drives a durable queue (`PhotoThumbnailJobs`, one row
per photo) and stores a Small (200 px) and a Medium (400 px) WebP per photo through the `PhotoThumbnails`
storage feature; `GET /api/photos/{id}/thumbnail?size=` serves them under `photos.read`. Rules that are easy
to get backwards:

- **Magick.NET is referenced by `Odyssey.Worker` alone, and only `Odyssey.Worker/Thumbnails/` may use it.**
  The renderer sits behind `IPhotoThumbnailRenderer` in Core; `ThumbnailSourceLintTests` fails on any other
  reference. Never move decoding into the API. Its patch clock (72 h / 14 days) is in `docs/deployment.md`.
- **`MagickRuntime.EnsureConfigured()` runs before anything else touches ImageMagick.** Reading even
  `MagickNET.Version` first initialises the default policy and the coder allow-list is then silently
  ignored. The startup self-test (`MagickDecoderSelfTest`) is what proves the policy is live.
- **The format is pinned from the stored content type, never sniffed**, and the pre-strip walk runs before
  native code: a still JPEG/PNG/WebP reaches ImageMagick already stripped and size-gated. GIF and animated
  input go in unstripped — an accepted residual, documented.
- **`Generation` is the job row's only concurrency token, and every write bumps it — a rename included.** A
  worker acts only on a `Running` row carrying its own `RunId`. A photo rename (`PUT /api/photos/{id}`) re-renders
  through `PhotoThumbnailService.StageRenameForPhotoAsync`, staged in the update's own save. A Files-store rename
  re-renders nothing: no photo wraps a Files-store file any more, so the former `IFileRenameObserver` seam and its
  `409 rename-busy` went with the legacy photo shape.
- **Every replica runs the job; state is per process.** The breaker (`PhotoThumbnailBreaker`, 3 crashes or
  hangs an hour) counts `PhotoThumbnailIncidents` rows, which carry no photo id so a delete cannot reset it.
  A hung decode cannot be cancelled: the worker pauses, then exits (code 70) for its container to restart —
  which only a supervisor such as Compose does.
- **`ThumbnailsReady` is "both renditions exist", and on the journal DTO it is also "the caller holds
  `photos.read"`** (`JournalEntriesController.ApplyThumbnailRule`). The client decides sources through one rule,
  `PhotoImageSources.For`, and draws every photo below viewer size through `OdsPhotoImage` (thumbnail → original
  → placeholder). The viewer, lightbox and edit banner always show the original; `PhotoThumbnailSurfaceTests`
  keeps a surface from building a `ContentUrl` or a CSS `background-image`.
- **The five thumbnail tables are derived data**: everything in them can be rebuilt from the original by
  re-queuing the photo, so they never need restoring on their own.

**A photo owns its original on the object store, not in the Files store** (issue #428). `Photo.StoredObjectId`
(RESTRICT, feature `PhotoOriginals`, laid out `<upload year>/<bucket>`) carries the bytes and the photo carries its own
`FileName`/`ContentType`/`SizeBytes`/`Sha256Hash` — all five `NOT NULL`. **The legacy shape is gone**: a photo once
wrapped a Files-store file through `Photo.FileId`; every production system finished the `PhotoOriginalBackfill` move, and
`RemoveLegacyFileBlobStorage` dropped the column, the job and its tables. Rules that are easy to get backwards:

- **Read through `PhotoOriginalSource`**, never `FileMetadata`. The original is served by
  `GET /api/photos/{id}/content` under `photos.read` alone; `files.read` reaches no photo, and no photo appears on the
  Files list.
- **`Photo.LegacyFileId` stays, on purpose.** It is the file id a photo wrapped before the move, with no key (the file
  may be gone), and it is what resolves an `odyssey-photo:<fileId>` reference in a pre-move `.ics` export
  (`IPhotoLookup.ResolveReferencesAsync`). A former file the move kept because something else used it is still a Files-
  store row: `FileService` reads through `FileReferenceCounter.VisibleInFilesStore`, which hides a file a photo names
  as its `LegacyFileId` unless another referencing column also names it — an attachment's own claim already reaches
  that one. `FileReferenceCounter.Discover` is derived from the EF model (every key to `FileMetadata`), so a new
  referencing table is counted without a code change — never replace it with a hand-kept list.
- **Upload is one multipart `POST /api/photos`** (`PhotoService.UploadAsync`): validated, hashed, duplicate-checked
  (`409 photo-duplicate`, existing id only for a `photos.read` holder), reserve-checked (`507`), written, then one save.
  There is no "photo from an existing file id" path; a JSON body with `fileId` is a `415`.
- **A rename changes `Photo.FileName` only**, never the file on disk. Journal entries link photos by `PhotoIds`. A delete
  releases the original and both thumbnails in its own save; it never touches a Files-store row.
- **`JobType` 5 (`PhotoOriginalBackfill`) and 7 (`FileBlobBackfill`), and `JobHeldReason` 6 (`BackingOff`), are
  retired — permanent holes, never reused.** `RemoveLegacyFileBlobStorage` deleted their run and heartbeat rows (TPH has
  no branch left to materialise them) and the CHECKs refuse them.
- **`OdysseyApiFactory` binds every production feature (both photo features, the four file areas and `NewsArticles`) to one
  per-factory temp volume** (`FileVolumeRoot`, `FileVolumeSpace`). A test that needs a host with no
  volume (the settings tests that read an unseeded InMemory store) sets `OdysseyApiFactory.NoFileVolumeKey`; on EF
  InMemory a host with volumes runs `EnsureCreated` before verifying, so `HasData` rows exist. A test file or photo is
  staged through `TestShared/FileStorage/StoredFileFixtures` — the bytes on the store, as production writes them.

**Every other file lives on the object store too, by AREA** (issue #448). `FileMetadata` has one nullable `RESTRICT`
column per feature — `FinanceStoredObjectId` (`FinanceFiles`, `finance-files/`), `JournalStoredObjectId` (`JournalFiles`,
`journal-files/`), `ContactAvatarStoredObjectId` (`ContactAvatars`, `contact-avatars/`) and `LibraryStoredObjectId`
(`LibraryFiles`, `library-files/`, issue #525: the Library module's area, shared by recipes, books and music) — and
`CK_FileMetadata_ExactlyOneSource` keeps exactly one of the four set. `FileUploadArea.Library` is `3` and
`StoredFileArea.Library` is `4`; the two are joined only through `StoredFileAreas.For`. **The legacy database copy is gone**: every
production system finished the `FileBlobBackfill` move, and `RemoveLegacyFileBlobStorage` dropped `FileBlob`,
`FileMetadata.FileBlobId`, the job and its tables (its guard refuses to run while any legacy row is left). All of it
lives in the module-neutral `Odyssey.Core/FileStorage/Files/`. Rules that are easy to get backwards:

- **The area is chosen at upload and never moves.** `POST /api/files` takes a required `area`
  (`FileUploadArea`, `Odyssey.Dtos` root); each client host passes its own (`AttachDocumentsDialog.Area` is
  `EditorRequired`). The avatar path always writes `ContactAvatars`, and no other writer may (source-lint).
  Placement is not access: the claim of the referencing record still decides who reads a file.
- **Read bytes through `StoredFileContentSource`, remove a row through `StoredFileRemoval`.** A `FileMetadata.Remove`
  outside the allow-list, or any reference to the removed `FileBlob`/`FileBlobId`, fails `StoredFilesSourceLintTests`.
  Removal stages the store release in the same save.
- **Every upload surface shares `UploadFailureText`** (`Odyssey.Client/Services/`): a `507`/`503`/`429` stops
  the batch, anything else is the file's own; inside a dialog the alert line is the only failure channel.
- **Finance must not gain a `using Odyssey.Core.Journal`.** Three files that predate the rule are allow-listed
  in the lint; the list may only shrink.
