# Reference data — Contact types

> Part of the [Odyssey Design System](../README.md) docs. Foundations, tokens and the component catalog live in the README; this file is the per-feature detail.


The **ContactType** enum (`Odyssey.Finance.Dtos/ContactType.cs`) has exactly six members — **Merchant · Person · Organization · Company · Institution · Other** — and `Other` is the `NewContact` default. The enum carries *only* a name; the **icon** and **color** for each type are a design-system decision, so they live in one canonical registry, `OdysseyData.contactTypes` (`ui_kits/web/data.js`), the sister of `accountTypes`. Every surface that renders a contact — the table's leading avatar, the type chip, the detail tile, and the inline/create pickers — reads that registry, so a type looks identical everywhere and a recolor is a one-line edit. `Contacts.jsx` no longer hard-codes the list; it sources `CP_TYPES` from the registry. Specimen: `preview/33-data-contact-types.html`.

| Type | Material icon | Color (oklch) | Meaning |
|---|---|---|---|
| **Merchant** | `storefront` | `0.79 0.115 188` (teal) | A shop, store, or service you pay — the everyday spending contact. |
| **Person** | `person` | `0.80 0.15 150` (green) | An individual — a friend, landlord, or contractor. |
| **Organization** | `corporate_fare` | `0.72 0.16 295` (violet) | A non-commercial body — charity, club, HOA, association. |
| **Company** | `business` | `0.76 0.13 225` (blue) | A registered business — typically an employer or vendor. |
| **Institution** | `account_balance` | `0.75 0.16 330` (magenta) | A bank, lender, government, or utility. |
| **Other** *(default)* | `category` | `0.74 0.02 250` (neutral) | The DTO default — anything outside the categories above. |

> **Encoding rule.** These hues identify a *category*; they share the categorical chroma/lightness band with `accountTypes` (L ~0.72–0.80, C ~0.12–0.16) so the two registries read as one family. They never encode income / expense / status, and brand **tide** / **sea** stay out of the scale. The avatar fills the soft (16%) tint behind the `color` glyph; the type chip is an outline carrying the same icon — never a filled swatch. Keep the registry keys in lockstep with the C# enum.

**Typed pickers.** Two consumable components turn the registry into ready-made controls, so a feature never re-wires the option list: **`ContactTypeSelect`** (single — the Type field on the create / edit contact forms) and **`ContactTypeMultiSelect`** (the ledger-header Type filter). Both are thin wrappers over the base `Select` / `MultiSelect`, pre-fed `CONTACT_TYPES` so every option renders its Material icon in its category color — value is the enum key, and everything the base control takes (label, help, error, `align`, …) passes through. Pass `types` to subset or reorder (e.g. drop `Other`). `CONTACT_TYPES` is exported on the DS namespace as the consumable layer's source of truth (mirrors the kit's `OdysseyData.contactTypes`). To support them, the base **`Select`** and **`MultiSelect`** gained an optional per-option **`icon`** + **`iconColor`** — additive and backward-compatible (options without an icon render exactly as before), so any select can carry a leading glyph. Specimen: `preview/25-components-type-pickers.html` (live), registry card `preview/33-data-contact-types.html`.

**Fields.** Beyond Type, a contact carries an **alias list** (see *Contact aliases & lifecycle dates*), a Person's optional **MiddleName** / **DateOfDeath**, an Organization's optional **EstablishedDate** / **DissolvedDate**, **Name** (≤ 128 chars, required), a server-derived **NormalizedName**, an optional **Description** (≤ 1024 chars, edited as a multi-line `Field`), and an optional **OrganizationNumber** — a free-text registration/tax identifier (string, ≤ 64 chars). It surfaces as a `mono` detail tile and a single-column field on both the create and inline-edit forms; null/empty renders as `—`. The synthetic record ID is **not** shown as a detail tile (it lives only in the *Copy contact ID* menu item).
