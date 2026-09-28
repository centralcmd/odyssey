# Reference data — Contract party roles

> Part of the [Odyssey Design System](../README.md) docs. Foundations, tokens and the component catalog live in the README; this file is the per-feature detail.


**`ContractPartyRole`** (`OdysseyData.contractPartyRoles`, helper `conPartyRoleInfo`; DS picker **`ContractPartyRoleSelect`**, registry exports `CONTRACT_PARTY_ROLES` + `CONTRACT_PARTY_ROLE_MATRIX`; specimen `preview/58c`) — what a linked record **does** in an agreement, orthogonal to its kind. Twenty live members: **Employee** (1, `badge`) · **Employer** (2, `corporate_fare`) · **Buyer** (3, `shopping_bag`) · **Seller** (4, `sell`) · **Other** (6, `more_horiz`) · **Landlord** (7, `vpn_key`) · **Tenant** (8, `home`) · **Insurer** (9, `shield`) · **Policyholder** (10, `assignment_ind`) · **Insured** (11, `health_and_safety`) · **Beneficiary** (12, `volunteer_activism`) · **Lender** (13, `savings`) · **Borrower** (14, `request_quote`) · **Guarantor** (15, `verified_user`) · **Broker** (16, `handshake`, deliberately low-chroma because it is legal everywhere and must not read as a category) · **Object** (17, `category`) · **Property** (18, `holiday_village`) · **Collateral** (19, `lock`) · **Depositor** (20, `account_balance_wallet`, hue 235) · **Custodian** (21, `account_balance`, hue 350). The Deposit pair is the mirror of Lender/Borrower — separate members, not aliases. `Custodian` shares its word with the account-level custodian on purpose and is independent of it; it does **not** block contact deletion. Specimen for the Deposit type and its roles: `preview/58d`.

**`Unspecified` (0) and `ServiceProvider` (5) are retired, and their ordinals are permanent holes.** A role is now **required on every party write**, so "nobody has said" can no longer be created and the member only ever described history; supplying a service is what `Seller` already meant. Reusing a retired ordinal is forbidden — it would make an unmigrated row mean something new rather than nothing. The kit has **no default role anywhere**: the picker opens on a placeholder and Save stays disabled until one is chosen. A legacy row with no role is drawn as an **absence** (`.con-role.unset`, muted, no colour) — never as the deliberate `Other`, and never as a category.

**Which roles are legal is a matrix, not a free list.** `OdysseyData.contractPartyRoleMatrix` declares, per contract type, the **suggested** roles (legal, offered first) and the **allowed** ones (legal, offered after); anything in neither is **rejected** with a `422`. **78 of the 200 cells are legal.** This is the client half of the server's shared `ContractPartyRoleMatrix` declaration — one symbol named by both the validator and the picker, never a client-side copy of a server rule. Three readers sit on it: `conRoleLegality(type, role)` (the single cell lookup), `conRolesForType(type)` (the legal rows, suggested first, each tagged `group`) and `conPartiesRejectedByType(parties, type)` (the type-change refusal). `conRoleListText(type)` writes the sentence both the picker helper and the refusal need.

| Type | Suggested | Also allowed |
|---|---|---|
| Employment | Employee · Employer | Guarantor · Broker · Other |
| Service | Buyer · Seller | Object · Guarantor · Broker · Other |
| Rental | Landlord · Tenant · Property | Object · Guarantor · Broker · Other |
| Insurance | Insurer · Policyholder · Insured · Beneficiary | Guarantor · Broker · Other |
| Subscription | Buyer · Seller | Object · Guarantor · Broker · Other |
| Purchase | Buyer · Seller · Property | Object · Guarantor · Broker · Other |
| Loan | Lender · Borrower · Collateral | Object · Guarantor · Broker · Other |
| Deposit | Depositor · Custodian | Object · Collateral · Guarantor · Broker · Other |
| Membership | Buyer · Seller | Object · Guarantor · Broker · Other |
| Other | Other | every other role |

**Suggested carries no server meaning** — it is the ordering, declared once beside the legality it has to stay consistent with. **Every type has at least one suggested role**, so the picker's first group is never empty; `Other`-the-type suggests `Other`-the-role, because a contract that is "none of the above" has no vocabulary to offer. **Insurance is the only type with four**, mirroring the four link collections on an insurance policy — and `Insured` is **one member for both party kinds**, since the kind discriminator already says whether the covered thing is an account or a contact. An **unknown contract type reports `allowed`** rather than refusing: a type this client has never heard of must not make the server's legal roles unpickable. An **unknown role key** renders honestly as *Unrecognised role*, because ordinals append server-side and a stale client can be handed a member it cannot name.
