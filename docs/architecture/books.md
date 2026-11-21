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
