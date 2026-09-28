# Components — Dialogs

> Part of the [Odyssey Design System](../README.md) docs. Foundations, tokens and the component catalog live in the README; this file is the per-feature detail.


Every modal flow shares one shell: the design system's **`Modal`** component (`components/Modal.jsx` — `.odc-scrim` / `.odc-modal` / `.odc-modal-head` (`.odc-modal-lead` + title column + close) / `.odc-modal-body` / `.odc-modal-foot`, `components.css`). It is a body-portaled scrim (click-out + Esc to close, with a subtle blur) under a 520px surface that rises in on open, hardened with a real a11y layer — focus trap (landing on the first body field), body scroll-lock, focus restore on close, `aria-labelledby` wiring. Per-dialog `className` widens it (`atm-` transaction 560px, `afm-` upload 560px, `fan-` analyze + `wide` 1240px). The **head is a tinted band** (the same subtle lift + hairline as the footer, so the body surface is bracketed top and bottom) carrying an **optional leading `icon` tile** (brand-tide by default, `iconTone="warning"`/`"error"` for destructive/confirm dialogs) and a title column that **fills the width up to the ×**, so the subtitle wraps only as it nears the close button (≤58ch measure cap). Every dialog sets a lead icon tied to its entity — New account `account_balance_wallet`, New transaction `receipt_long`, New budget `pie_chart`, Upload `cloud_upload`, New contact `store`, New currency `attach_money`, New tag `local_offer`, New exchange rate `currency_exchange`, Analyze file `document_scanner`, New term `percent`, edit dialogs `edit`. (The kit's legacy `.aam-*` classes remain in `kit.css`, mirrored 1:1, for the hand-rolled `FileViewerModal` surface and the static anatomy card.) Anatomy + the wording rules: `preview/37-dialog-anatomy.html`.

**Wording follows the create/new convention** (see Content fundamentals): a creation **trigger** reads *New X*, the **dialog title** reads *New X*, and the **primary button** reads *Create X*. Edit dialogs are titled *Edit X* with a *Save changes* primary (one component often does both, keyed on whether a record was passed). Upload keeps upload verbs (*Upload file* / *Upload files* / *Upload*). One-off process dialogs share a verb across title and primary (*Analyze file*, *Set rate*, *Reconnect*).

Each dialog has a live specimen card in the **Dialogs** group:

| Dialog | Component | Kind | Card |
|---|---|---|---|
| New / Edit account | `AddAccountModal` | create + edit | `preview/38` |
| New / Edit transaction | `AddTransactionModal` | create + edit | `preview/39` |
| New / Edit budget | `AddBudgetModal` | create + edit | `preview/40` |
| New / Edit budget item | `AddBudgetItemModal` | create + edit — **three** fields (transaction tag · category · planned amount) | `preview/41` |
| Upload files | `AddFileModal` | upload | `preview/42` |
| New contact | `AddContactModal` | create | `preview/43` |
| New / Edit alias | `ContactAliases`' own dialog | create + edit | `preview/62` |
| New / Edit currency | `AddCurrencyModal` | create + edit | `preview/44` |
| New / Edit tag | `AddTagModal` | create + edit | `preview/45` |
| New / Edit exchange rate | `RecordRateModal` | create + edit | `preview/46` |
| New / Edit term | `AddTermModal` | create + edit | `preview/48` |
| File viewer | `FileViewerModal` | viewer | `preview/47` |
| Analyze file | `AnalyzeFileModal` | process (consent-gated · resumable · **AI-matched**, with a Matching step + match-degraded fallback) | `preview/28` |

> **Convergence note — resolved.** Every create/edit/process dialog in the table above is now built ON the consumable `Modal` (it owns the scrim, head, scrollable body, footer, Esc/click-out, focus trap and restore); each dialog supplies only its body fields, footer verbs, and lead icon. The one deliberate exception is `FileViewerModal` — a full document-viewer chrome (header · toolbar · stage · footer) that composes only the scrim/surface primitives.
