# Recipes

Part of the architecture rules split out of the root [`CLAUDE.md`](../../CLAUDE.md). Read this before changing code in this area; the rules here are binding in the same way.

**Recipes are shared household data that link library photos and Files-store files by id** (issue #525; `Odyssey.Core/Recipes/`,
`Odyssey.Dtos/Recipes/`). Rules that are easy to get backwards:

- **Claim before existence.** Adding a photo link needs `photos.read`, a file link `files.read` and a tag link
  `recipes.tags.read`, each checked before any lookup, so a missing claim and a missing id give the same `403`. `JournalEntryService` validates
  existence first; don't copy that order here.
- **Photos are absent, not hidden.** Without `photos.read` the service skips every photo lookup and returns
  `photos`/`coverPhotoId` as `null`; `hasPhotos` is a `403`. On `PUT`, `photoIds: null` leaves the links alone and
  any array from such a caller is a `403`, because it cannot see what it would replace.
- **Files follow the same rule under `files.read`** (Draft v6): `attachments`/`attachmentCount` are `null` without
  it, `hasAttachments` is a `403`, and `attachmentFileIds: null` on `PUT` leaves the links alone. A linked file must be
  visible in the Files store (`FileReferenceCounter.VisibleInFilesStore`), so a photo's own original cannot be linked
  under `files.read`. Links are unordered and come back by file name. Recipe uploads go to the shared **Library** area
  (`FileUploadArea.Library`, `library-files/`), which books and music will reuse — placement only, never access.
- **Ingredient and step positions are not unique; photo, file and tag pairs are.** Children are replaced wholesale in one
  save, which a unique position index would break on MariaDB (EF does not order a delete before an insert); links
  are diffed in place so their unique pairs never see a delete and re-insert. Only `Odyssey.IntegrationTests` shows
  this — InMemory enforces no unique index.
- **`Version` is the concurrency token**, required on `PUT`; a stale one is `409` with code `recipe-version-conflict`.
  A delete that loses a race to an edit is the same `409`; one that loses to another delete is a `404`.
- **Scaling is read-only** (`GET …?servings=`, `RecipeServingsScaler`): it never writes, never changes a unit, and
  leaves `Pinch`/`ToTaste` alone. `IngredientUnit` ordinals are a wire contract.
- **Every bound is a `RecipeLimits` constant**, named by the entity, the DTO annotations and the client alike.
- **Author ids never reach the wire**: the controller resolves them to names (`NameForOptional`), because Guest holds
  `profile-images.read`. **Finance must not reference `Odyssey.Core.Recipes`** (`RecipeModuleSourceLintTests`).
