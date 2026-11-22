# Books (Library)

Part of the architecture rules split out of the root [`CLAUDE.md`](../../CLAUDE.md). Read this before changing code in this area; the rules here are binding in the same way.

**Books are the first Library module, and they link only to contacts, library photos, files and their own tags**
(issue #527). `Book` carries a required main author (`MainAuthorContactId`) and ordered `BookContributors`; images are
`BookImages` over Photo Library photos (one `IsCover`, no order), attachments are `BookAttachments` over Files-store rows
uploaded to the `Library` area, and `BookTags` are their own table. Rules that are easy to get backwards:

- **A credit blocks its contact's delete, on both paths, with a `RESTRICT` key behind it.** `Books.MainAuthorContactId`
  and `BookContributors.ContactId` are `RESTRICT`; `IContactBookCredits` (interface in `Journal`, implementation in
  `Books`, so Journal never references Books) is checked before and inside the delete's transaction, and a `1451` from
  either Books table maps to the same `409`. The detach valve never removes a credit. The `409`'s `bookCredits` carries
  counts always and titles only with `books.read`, capped at `BookLimits.MaxCreditBooksListed`.
- **Claims before existence.** A link array, a link filter (`contactId`, `sortBy=Author`, `hasCover`, `hasAttachments`,
  `tagIds`) or a changed main author needs its read claim, checked before any lookup, so a refusal never says whether
  an id exists. On `PUT` an array of `null` keeps the set; any array replaces it and needs its claim.
- **`PUT` diffs children in place and checks `Version`** (`409 book-version-conflict`); never delete and re-insert a
  child, or the `(BookId, ContactId)`/`(BookId, PhotoId)` unique indexes fire on the ordinary path.
- **A new link to an archived contact or tag is a `422`; a kept one stays.** Year is −9999…9999 except `0` (BC is
  negative); an ISBN is stored normalised by `IsbnRules`, the one rule the client shares.
- **EPUB is accepted in every upload area** (stored `mimetype` first, `FileHeaderSignatures.HeaderLength` 64) and is
  download-only; parsing one server-side re-opens the upload review.

**The client** (issue #571; design system `docs/components-books.md`) lives in `Odyssey.Client/Pages/Library/`:

- **Every link change is the book's own `PUT` with one array set** (`BookRecord.Unchanged(book) with { … }`): the
  record ⋯ menu adds contributors, images and files, the tiles move, detach or re-cover them, and the New / Edit book
  dialog never sends `contributors`, `photoIds`, `coverPhotoId` or `attachmentFileIds` (empty on `POST`, `null` on
  `PUT`). `tagIds` follows the `books.tags.read` snapshot at open. A `409 book-version-conflict` re-reads; nothing
  overwrites.
- **Format is the record's accent, status is a chip.** `OdsTypeRegistries.BookFormats` / `BookVisuals.Statuses` follow
  the server ordinals; an unknown ordinal reads *Unknown format* / *Unknown status*, never the trailing *Other*.
- **A count or section without its read claim is absent, not empty** — the server sends `ImageCount` /
  `AttachmentCount` as `null` without `photos.read` / `files.read`.
- **EPUB:** `ToApiUpload` sends an `.epub` the browser typed empty or `application/octet-stream` as
  `application/epub+zip` (the only content-type rewrite); `OdsTypeRegistries.FileKindOf` labels it *E-book* and never
  previews it.
