# Reference data — Contract types

> Part of the [Odyssey Design System](../README.md) docs. Foundations, tokens and the component catalog live in the README; this file is the per-feature detail.


**`ContractType`** (`OdysseyData.contractTypes`, helper `contractTypeInfo`; DS picker **`ContractTypeSelect`**, registry export `CONTRACT_TYPES`; specimen `components/typeselect.html`, Deposit specimen `preview/58d`) — what kind of agreement a contract is. Ten members, listed in **reading order, not ordinal order**:

| Type | Ordinal | Icon | Color (oklch) | Meaning |
|---|---|---|---|---|
| **Employment** | 0 | `work` | `0.76 0.13 225` | An employment agreement. |
| **Service** | 1 | `home_repair_service` | `0.78 0.14 170` | A service agreement — utilities, telecoms. |
| **Rental** | 2 | `cottage` | `0.79 0.14 60` | A tenancy or lease. |
| **Insurance** | 4 | `shield` | `0.75 0.14 290` | A policy held as an agreement. |
| **Subscription** | 5 | `autorenew` | `0.76 0.14 320` | A recurring supply agreement. |
| **Purchase** | 6 | `shopping_bag` | `0.78 0.14 140` | A one-off acquisition, by completion date. |
| **Loan** | 8 | `account_balance` | `0.77 0.13 100` | Money advanced to the household, to repay. |
| **Deposit** | 9 | `lock_clock` | `0.76 0.13 258` | Money the household places with a bank or landlord, to have returned — fixed-term, notice, or rental deposit. |
| **Membership** | 7 | `card_membership` | `0.77 0.13 20` | A club, gym, union or association. |
| **Other** *(default)* | 3 | `description` | `0.74 0.02 250` | Anything outside the categories above. |

**Appended members read beside their nearest neighbour.** Loan (8) reads after Purchase; Deposit (9) reads after Loan, its mirror. **`Other` must stay the trailing entry** — it is the documented fallback for an out-of-range value, so an older client shows a type it cannot name as Other. Deposit's hue sits in the widest remaining gap (between Employment 225 and Insurance 290); Other at 250 is near-achromatic and does not compete. The list filter, the New/Edit picker and the run-rate "by type" rows all read this registry, so a new member needs no page change.
