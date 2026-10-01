# Transaction tag icons — frontend

Frontend counterpart to *Transaction Tag Icons — Backend (Draft v1)*. Four surfaces change — the tag table, the tag dialog, transaction rows, and every transaction-tag picker / filter. Everything else that shows a tag (read-only tag chips, counts, empty states, the Tags nav item, the tag dialog's header) keeps the generic `local_offer`.

## Pieces

- **`TagIconPicker`** (`components/TagIconPicker.jsx`) — closed grid of the catalogue. First cell is **Default** (dashed outline, `local_offer` glyph, value `null`). Selected cell: tag-violet fill + 2px inset ring + check badge, so selection never rests on colour. Caption row under the grid names the selection ("Groceries", or "Default · the tag icon") and previews the hovered / focused cell's label.
- **`TransactionTagIcons`** — the client mirror of `Odyssey.Dtos/Finance` `TransactionTagIcons`: `All` (32 `{key,label}`), `Default`, `isKnown` (ordinal, case-sensitive; the default key is not a member), `normalize`, `glyph`, `labelFor`, `order`, `resolve`.

## 1 · Tag table

The leading avatar **is** the icon column (no new header): `Avatar icon={glyph(tag.icon)}` in the tag tone. `null` and unknown keys draw `local_offer`. Not sortable or filterable by icon (non-goal §2.5).

## 2 · New / Edit tag dialog

An **Icon** field below Description, wrapped in `FieldShell`:

- Helper: "Shown for this tag and on every transaction carrying it. Default keeps the tag icon."
- New tag starts on Default. Edit opens on the stored icon, normalised (an unknown key opens as Default).
- Submit always sends `icon` — `PUT` is full replacement, so omitting it would reset the tag. Default sends `null`, never `"local_offer"`.
- A `400` with `errors.Icon` renders at the field as "Not an available icon." The picker can't produce one; it only occurs if the client catalogue is stale.
- Journal, task and photo tag pages use the same factory with `icons` off — no icon field, no avatar change.

## 3 · Transaction rows

`TxnTable` draws `t.displayIcon` (server-resolved, always non-null) in the leading avatar. The avatar **tone** still encodes direction (mint income / coral expense); only the glyph comes from the tags. The avatar stays decorative (`aria-hidden`) — the Tag column names the tags in text.

Tag chips render in the API's order (name, case-insensitive), so the first chip is the tag whose icon is most likely showing. Applies everywhere `TxnTable` renders: Transactions, Accounts, Budgets, Dashboard.

## 4 · Tag pickers and filters

Every control that lists transaction tags shows each tag's icon before its name (`null` / unknown → `local_offer`). One kit helper builds the option — `OdysseyData.tagOption(tag)` → `{ value, label, icon }` — and one DS rule draws it (`TransactionTagIcons.glyph`).

| Surface | Control | Notes |
|---|---|---|
| New / Edit transaction — **Tags** | `TagMultiSelect` | Icon on each option and selected chip. The helper becomes a live **Row icon** preview: the glyph `resolve` gives for the current selection, and the tag it came from ("from Dining"), or "default — none of these tags has an icon". Empty selection keeps the original hint. |
| Budget item — **Transaction tag** | `TransactionTagPicker` | Option icon was a fixed `local_offer`; now the tag's icon. "in use" / "· Archived" stay in words. |
| Analyze file — **Category** cell | `TagMultiSelect` | Same options as the transaction form. |
| Transactions header — **Tag** filter | `MultiSelect` | Icon per option. |
| Account / Contract / Property **smart tags** | add picker | Icon per option. |

Tax-statement derivation tags are keyed by name from their own catalogue and are unchanged.

## Resolution rule (display only)

Order by name (ordinal, case-insensitive; ties by id) → first tag with a known icon → else `local_offer`. Archived tags count. Render the server's `displayIcon`; call `TransactionTagIcons.resolve` only for a local preview (e.g. while editing a transaction's tags before save).

## Not in v1

Icon colour, user-chosen primary tag, icons on journal/task/photo tags, sorting or filtering by icon.
