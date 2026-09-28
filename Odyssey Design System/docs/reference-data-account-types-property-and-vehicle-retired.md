# Reference data — Account types (Property and Vehicle retired)

> Part of the [Odyssey Design System](../README.md) docs. Foundations, tokens and the component catalog live in the README; this file is the per-feature detail.


**A house or a car is a Property record, never an account.** `AccountType.Property` (6) and `AccountType.Vehicle` (7) are **retired** (*Retire Property and Vehicle Account Types — Backend, Draft v3*; follows #167). The ordinals are **permanent holes** — never reused, never re-added — and a database `CHECK` (`CK_Accounts_AccountTypeNotRetired`) forbids them. `ACCOUNT_TYPES` (`components/AccountTypeSelect.jsx`) and `OdysseyData.accountTypes` no longer carry either key, so `AccountTypeSelect` stops offering them and `AccountTypeChip` cannot render them. The asset group still reads **Cash · Checking · Savings · Investment · Pension · Other asset**; `AccountClassification` is ordinal, so the holes need no change there.

- **No new UI surface.** The change is a one-shot data migration plus the enum removal. Nothing on Accounts or Properties gains a control, notice or banner for it.
- **Where the data went.** Each type-6/7 account became a Property with **the same GUID** (6 → Real estate, 7 → Vehicle, detail `Kind = Other`), carrying its estimates, smart tags, file links (`AccountFileType` coarsened to `PropertyFileType`; unmatched values → Other) and contract-party links. `AccountNumber` lands in the property's **Notes**; the custodian is not carried over.
- **Accounts with transactions were kept**, re-typed to **Other asset** and archived; their smart tags are copied, not moved. Such an account and its property share one GUID — `/api/accounts/{id}` and `/api/properties/{id}` return two different records. A deleted account's old `/accounts/{id}` link is a 404; the same id resolves under Properties.
- **Colours.** The two retired account hues (`0.72 0.14 255`, `0.78 0.14 170`) now belong only to `propertyTypes` (Real estate, Vehicle). The client's `--acct-property(-soft)` / `--acct-vehicle(-soft)` custom properties are removed with the switch arms; the design system never shipped them.
- **Estimates.** The recommended subset drops to **Other asset · Investment · Pension** (`estimateRecommendedTypes`); the non-recommended hint points a house or car to Properties.
- **Kit seed.** The former `Maple St Residence` account ('7') is gone — the house is property `p-maple` — and the solar-panel contract's `Object` party now names the property. `preview/32` and `preview/50` demo estimates on an Other-asset and an Investment account instead.
- **API.** `POST`/`PUT /api/accounts` with `accountType: 6` or `7` is a `400` keyed `AccountType`; no read returns either value.
