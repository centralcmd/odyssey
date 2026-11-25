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
  (`FileUploadArea.Library`, `library-files/`), shared with books (#527) — placement only, never access.
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
- **Demo data** (`RecipeGenerator`, issue #525 §11): six tags (one archived) and five recipes (one archived) covering
  sections, `ToTaste`, `Pinch`, a servings label and a recipe with no times. `DemoDataSeeder` writes the
  recipe rows; the worker's `DemoFileSeeder` writes the photos, the Library-area files and their links.

**AI recipe import drafts a recipe from text and files, and nothing is a recipe until a person creates it** (issues
#602, #604; `Odyssey.Core/Recipes/Import/`, `/recipe-imports`). An import (`RecipeImports`, with its ordered
`RecipeImportSources` and its `RecipeImportDrafts`) is household-shared under `recipes.import`; the worker's
`RecipeImportQueueJob` sends it and `RecipeImportRetentionJob` purges finished imports after `RecipeImportRetentionDays`.
Rules that are easy to get backwards:

- **The draft lives on the import, never in `Recipes`.** Accept (`POST …/drafts/{id}/accept`) creates the recipe through
  `RecipeService.StageCreateAsync` — the same validation as `POST /api/recipes` — and marks the draft in **one**
  transaction fenced by the draft's `Version` and the import's `Generation`, so a parallel accept, purge or delete loses
  cleanly. `RecipeService.CreateAsync` keeps its own behaviour.
- **Only Library-area files can be sources** (a Finance or Journal id is the same `404` as a missing one), and images go
  through `StillImageValidator`: the stripped bytes are what is stored and what is sent. The worker strips an existing
  image again before sending and fails `source_invalid` rather than send unstripped bytes.
- **Every pre-send check runs in the worker, from an uncached read, before `TransferStartedAt`**: the requester's claims
  (read from the database, not a cookie), the files, the destination origin, the disclosure version (per caller: it
  covers whether tag names are sent), the credential, then `RecipeImportEnabled` last. `DocumentSent` derives from the
  stamp alone. A claimed import is never re-sent; the recipe-import HTTP client retries once, only on 429/503/529.
- **Delete is a tombstone and never touches a recipe.** It purges the content, nulls the source file links and releases
  only import-owned files nothing else references (`RecipeImportContentPurge`); the row and its audit columns stay.
- **`tagIds: null` on a draft `PUT` keeps the stored suggestions** and needs no tag claim; reads return `null` to a caller
  without `recipes.tags.read`.
- **The provider's tool schema is a verbatim JSON element**, because the request serializer's snake_case policy would
  rename JSON Schema keywords such as `maxItems`.
