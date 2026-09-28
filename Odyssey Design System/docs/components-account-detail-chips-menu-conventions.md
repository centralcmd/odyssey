# Components — Account detail chips & menu conventions

> Part of the [Odyssey Design System](../README.md) docs. Foundations, tokens and the component catalog live in the README; this file is the per-feature detail.


The expanded account record's metadata grid reads as **one coherent chip family** — type, custodian, and status all render as the same pill shell (a leading accent · a bold name · a muted third segment), drawn from the canonical registries. Three consumable components, all siblings:

- **`AccountTypeChip`** — the account **type** as a chip: the type's colored Material glyph + label + a muted **Asset / Liability** group segment (e.g. *Checking · Asset*). Driven by the `ACCOUNT_TYPES` registry, the same source the type picker and row avatar use. `showGroup={false}` drops the group; `size` sm (row) / md (detail).
- **`AccountStatusChip`** — the account **status** as a chip: a tone-colored **dot** (status is a state, not an icon-category) + label + an optional muted **date** segment (e.g. *Open · since Mar 14, 2021*). `tone` maps to the dot color — income (open) · pending (closed) · outline (archived). The sibling structure to the type chip, with a dot where the type has a glyph.
- **`CustodianChip`** — the **custodian** as a chip (documented above): type glyph + name + muted ContactType segment (e.g. *JPMorgan Chase · Institution*).

> **Status reads "Open", and the lifecycle lives in one tile.** An account's non-closed status is labelled **Open** (not "Active") across the row chip, the detail tile, the *Any status* filter, and the page subtitle ("6 open · …"). The detail grid **consolidates** the former separate *Opened* / *Closed* tiles into the single **Status** tile: the status chip plus a lifecycle line that adapts to the state — `Opened {date}` when open, `Opened {date} · Closed {date}` when closed, `Opened {date} · Archived {date}` when archived. The collapsed row keeps the compact chip with the single most-relevant date. (Tags / contacts / currencies / budgets keep their own *Active / Archived* vocabulary — the rename is account-specific.)

**Menu conventions — the `ActionMenu` overflow (`more_vert`).** Two capabilities were added to the shared `ActionMenu`, used by every record table and list row:

- **`trailingIcon`** — a right-aligned Material icon on a menu item, revealed on hover/focus. The house use is the **`content_copy`** affordance on a Copy-ID item.
- **Glyph icons** — an item's `icon` (and the `Modal` lead-tile `icon`) may be a Material Icons ligature **or** any non-ligature character rendered as a typographic glyph. This is how the **Terms** action and dialog carry the **§** section glyph (matching the Terms section's identity), the same technique the `Collapsible` lead uses.

> **One "Copy ID" everywhere.** Every record's overflow menu carries a single standardized copy action: a **`fingerprint`** leading icon, the label **"Copy ID"** verbatim (never "Copy *X* ID"), and a hover-revealed **`content_copy`** trailing icon. This holds across Accounts, Transactions (`TxnTable`), Files (`FilesTable`), Contacts, Currencies, Tags, Exchange rates, Users, Tax statements, and Budgets — the identifier copied differs (id / currency code), the affordance does not.
