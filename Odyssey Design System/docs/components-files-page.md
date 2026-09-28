# Components — Files page

> Part of the [Odyssey Design System](../README.md) docs. Foundations, tokens and the component catalog live in the README; this file is the per-feature detail.


The **Files page** (`Files.jsx`) is the flat screen at `/files` — every stored file across every account in one searchable, sortable table. It mirrors the **Transactions page** layout (page header + filter card + table) but it *manages* files rather than creating them. Specimen: `templates/files` (the whole page, static), with `preview/29-components-file-viewer.html` for the View modal a row opens; reference build: `ui_kits/web/Files.jsx`.

**A join, not a collection.** Files belong to accounts — there is no flat files endpoint. `data.js` keys `accountFiles` by `accountId`; the page flattens them into one list and joins each file back to its owning account for filter context. The header sub counts the set (`5 files across 2 accounts`), there is **no header primary** (files are added per-account via Add file on Accounts or the upload field), and Search holds a name query plus a document-type `MultiSelect` — **no account filter by design**; search + type cover the MVP, and a file's owning account shows in its View modal.

**The table.** The consumable DS **`FilesTable`** (`components/FilesTable.jsx` — the same surface as the per-account Files list and the Transactions panels, now a **preset of `RecordTable`**). Four sortable columns — Name, Type, Size, Uploaded (the default sort, newest first) — plus the actions cell. File rows follow the standard **expand → detail → inline edit** lifecycle: click a row (or **View details**) for the read-only MetaTile detail, **Edit** swaps in the inline panel (name + document type, the only mutable fields), Save flashes the row's **Saved** chip. There is **no Account column**. Each row leads with a **kind avatar** — a document-type icon tile whose glyph + oklch hue match the upload modal's kind picker (resolved via `typeFor`).

**Document types.** Four kinds, each one icon + categorical hue: **Statement** (teal `description`) · **Document** (slate `insert_drive_file`) · **Receipt** (green `receipt_long`) · **Tax** (magenta `request_quote`). Unknown extensions fall back to the slate document glyph. Only **Statements** can be analyzed.

**The overflow menu** is the file action menu, identical on every files surface and following the record-table menu convention (View details · Edit · file-specific items · — · Delete):

| Action | Opens / does |
|---|---|
| View details | Expands the row into its read-only detail — the record, not the document. In-place, no modal. |
| Edit | The inline edit panel in the expanded row — rename + change document type (its only mutable fields). |
| Preview | `FileViewerModal` — the document itself: PDFs paged, images zoomable, else a download prompt. |
| Download | Native browser save with the stored filename — no preview, no navigation. |
| Resume review | `AnalyzeFileModal` opened on the saved job — **only when the file has an open, resumable analysis** (extraction done, candidates still pending). Reopens straight into the candidate list; no new transfer. The row also shows a **“Review pending · N”** chip. |
| Analyze | `AnalyzeFileModal` — the candidate-review flow. **Statements only**; other kinds are blocked up-front. With a resumable job present it opens on the **reanalyze-confirm** fork (resume vs. analyze again) rather than silently creating a duplicate. |
| Delete | Divided off, coral, through the confirm dialog. |

> **Stack reality check.** The flat page is `Files.razor` (`/files`) standing in for a MudBlazor `MudTable`. An `AccountFile` carries name, document type (`AccountFileType` — `Message` / `Statement` / `Contract` / `Tax` / `Other`), size and upload date, and belongs to an account — so the list is assembled from each account's `GET /api/accounts/{id}/files`, not a flat endpoint. Edit is `PATCH /api/files/{id}` (name + type only; the bytes are immutable), Delete `DELETE /api/files/{id}`, Analyze `POST /api/file-analysis`. The prototype flattens `data.js` `accountFiles` in `Files.jsx`; new files are uploaded per-account, never from this view.
