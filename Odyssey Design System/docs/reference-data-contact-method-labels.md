# Reference data — Contact-method labels

> Part of the [Odyssey Design System](../README.md) docs. Foundations, tokens and the component catalog live in the README; this file is the per-feature detail.


Every row in a contact's **Addresses**, **EmailAddresses** and **PhoneNumbers** collections carries a **Label** — `AddressLabel` / `EmailLabel` / `PhoneLabel`. All three enums were written for a *person* (Home / Work / Mobile / Billing / Other), and Odyssey's contact list is overwhelmingly organizations — banks, insurers, utilities, landlords, employers — where "Home phone" and "Work email" mean nothing. Each enum now also carries an **organization vocabulary**, and every member is **scoped to the contact types it is valid for**. Sixteen new members; no existing ordinal moves. Registry + scope map: `components/ContactMethodLabelSelect.jsx` (`ADDRESS_LABELS` / `EMAIL_LABELS` / `PHONE_LABELS` + `ContactLabelScope`). Specimens: `preview/36b-data-contact-method-labels.html` (the vocabulary) and `components/contact-method-label-select.html` (the live picker and its states).

| Enum | Person | Organization |
|---|---|---|
| **AddressLabel** | Home *(default)* · Work · Billing · **Postal** · Other | **Visiting** *(default)* · **Registered** · **Branch** · Billing · **Postal** · Other |
| **EmailLabel** | Home → displayed **"Personal"** *(default)* · Work · Other | **General** *(default)* · **Support** · **Sales** · **Billing** · **Claims** · Other |
| **PhoneLabel** | Home *(default)* · Work · Mobile · Other | **Switchboard** *(default)* · **Support** · **Sales** · **Billing** · **Claims** · **Emergency** · **Direct** · Mobile · Other |

**The invariant — a contact method's label is valid for its contact's type.** It is held by five mechanisms and nothing else writes a label: the backfill migration, the `422` on the six contact-method write paths, the **clamp** on vCard import, the clamp over all three collections when a contact's type changes, and the client (the picker offers only valid labels, and `Validate` refuses anything else).

**Rules that matter to a designer.**

- **The default is the first offered member.** `ContactLabelScope.defaultFor(kind, type)` *is* `labelsFor(kind, type)[0]`, so display order and default can't drift apart. "Add phone number" on an organization opens on **Switchboard** already selected — the happy path is submit without touching the picker.
- **Clamp, never drop.** Import and the type-switch remap resolve an out-of-scope label to **`Other`** (valid for both types in all three enums) instead of refusing the record. A Person → Organization → Person round trip is therefore lossy by design, and the clamp never guesses a "closest" label.
- **An out-of-scope stored value reads as empty.** The trigger renders its placeholder when the value isn't in the option list, so the message on it is **required**-style ("Label is required."), never one naming a value that is nowhere on screen. That's the opposite of the server `422`, which *does* name the offending label and the valid set — the caller supplied it.
- **The option set is never rebuilt under the user.** A concurrent type switch does not live-swap the picker's contents (the WCAG 3.2.2 change-on-input hazard); the dialog keeps the set it opened with and the write fails server-side with an explicable message.
- **No new colour, no new widget.** Every entry reuses the neutral `oklch(0.74 0.02 250)` glyph the label registries already used — a label names a channel, it does not encode status — and meaning is carried by the label **text** on the tile, never by icon or colour alone.
- **Ordinals are the wire contract** (these enums serialize as integers): **1–19 is the person/shared band, 20+ the organization band**, per enum. The bands are *not* aligned across the three enums; the scope map, never the ordinal, decides validity.
- **Widening a label's scope is free; narrowing it is not.** Adding a type to a row leaves every stored row valid. Removing one — or removing a member — breaks the invariant with no migration and no guard, so it needs its own remap in the same commit. This is the general rule for any enum whose valid set depends on a sibling column.
- **Unknown keys resolve `Other` by key, never by position.** Organization members are appended *after* `Other`, so a last-entry fallback would render an undefined ordinal as `Branch` / `Claims` / `Direct` — plausible, specific, wrong.
- **vCard.** Export emits the nearest standard `TYPE` token plus `X-ODYSSEY-LABEL=<MemberName>` — and only where a label has no standard token of its own, so a file with no organization labels is byte-identical to the pre-change output. Import prefers the parameter (matched against the enum's member *names*, so no undefined ordinal can be persisted), falls back to the standard token, then clamps.
- **Norwegian anchors:** *fakturaadresse* → Billing · *postadresse* → Postal · *besøksadresse* → Visiting · *forretningsadresse* → Registered · *sentralbord* → Switchboard · *kundeservice* → Support · *skade* → Claims.

**Where it surfaces.** `Contacts.jsx` — the Label field on the new/edit address, email and phone dialog (`ContactMethodLabelSelect`, fed `kind` + the parent contact's type), the label text on every contact-method tile, and the `LabelChip` in the row layout. Contact **type is locked after creation** in the UI, so the remap has no client-visible surface. The kit's *Stored label out of scope (residual)* tweak seeds a row whose stored label has gone invalid: the picker falls back to its placeholder, and **Set as primary** — which rebuilds the row from stored values and bypasses the picker — surfaces the server's `422` message verbatim through a toast.

> **Vocabulary boundaries.** Closed set: no free-text or custom labels (a new PII surface, and label-based reading would stop being reliable). Deliberately excluded as too narrow for a personal-finance address book: Fax, Delivery/Warehouse, Press, Careers/HR, Booking, Pager, Textphone, Video. **Main** is excluded because every row already carries `IsPrimary`, and "Main" next to "Primary" is two names for one idea. There is no label-based filtering or search on the contacts list.
