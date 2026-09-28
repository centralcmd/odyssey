# Components — File viewer

> Part of the [Odyssey Design System](../README.md) docs. Foundations, tokens and the component catalog live in the README; this file is the per-feature detail.


The **File viewer** (`FileViewerModal`) opens from the **Preview** action in the file row action menu — on the per-account Files list (Accounts → account detail) and the flat Files page. It previews a stored `AccountFile` in place without leaving the app. Specimen: `preview/29-components-file-viewer.html`; reference build: `ui_kits/web/FileViewerModal.jsx`.

**Anatomy.** A 12px-radius dialog (the modal radius) on the standard `rgba(8, 12, 24, 0.6)` scrim, in four bands:

1. *Header* — file-type icon chip, filename, and a context meta row (type chip · account ·last4 · size · upload date). Close affordance top-right.
2. *Toolbar* — controls adapt to the file type (see below). Page nav left, zoom centered, secondary actions (rotate, open-in-new) right.
3. *Stage* — a recessed neutral surface (`--ink-950` on dark, `--ink-200` on light) that holds the document. The file content itself is a **white page in both modes** — a receipt or statement is a document, not app chrome, so it never inverts.
4. *Footer* — a "Read-only preview" lock note, with Close (text) and Download (filled primary).

**Branches**, keyed off the file extension:

| Type | Match | Controls | Renders |
|---|---|---|---|
| Image | `jpg png gif webp svg heic tiff bmp avif…` | zoom, **rotate** | the raster, centered in the stage |
| PDF | `pdf` | **page nav**, zoom | the document, one page at a time |
| Other | everything else | — | a "Preview not available" empty state with a Download CTA |

Zoom runs 50–300% in 25% steps; the percent label resets zoom + rotation to fit. `←/→` page a PDF, `Esc` closes.

> **Stack reality check.** The shell (`MudDialog`), header, toolbar (`MudIconButton` / `MudButtonGroup`), and the **image** branch (`MudImage`) are all native MudBlazor. MudBlazor has **no dedicated PDF component** — render the PDF branch with the browser's built-in viewer embedded via `<object data="blob:…" type="application/pdf">` (or an `<iframe>`), and keep the toolbar/page chrome above as the Odyssey wrapper so it matches the rest of the app. The statement/receipt drawn in the specimen are stand-ins for real file bytes; the design work is the chrome around the embed.
