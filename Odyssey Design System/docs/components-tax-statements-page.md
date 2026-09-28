# Components — Tax Statements page

> Part of the [Odyssey Design System](../README.md) docs. Foundations, tokens and the component catalog live in the README; this file is the per-feature detail.


The **Tax Statements page** (`TaxStatements.jsx`) is the yearly-tax record screen at `/tax-statements` — one expandable record per fiscal year, the sister of the Accounts and Budgets pages. Reference build: `ui_kits/web/TaxStatements.jsx`; template: `templates/tax-statements/`.

**It reuses the record scaffold; its net-new piece is reconciliation.** Header, expandable `.acct-item` rows, metadata wells, and collapsibles are the same atoms documented for Accounts. The defining view is the **reconciliation report** — a table (or tiles, via a tweak) contrasting three columns: **Declared** (figures from the official statement), **Odyssey-derived** (net worth from accounts, advance tax + actual income summed from tagged transactions), and **Variance**. The variance cell reads mint ✓ when reconciled, amber when it differs, disabled when a derived figure is unavailable. Rows group under **Net worth · Income · Tax**, where the Tax group shows assessed tax, advance paid (statement-implied `assessed − settlement` vs. tag-derived), and the settlement.

**The cross-year settlement is the modelling principle.** Advance tax is withheld *within* the income year; the post-assessment settlement (additional tax / refund) is **declared, not derived**, and paid the following year — so it stays out of that year's derived advance-tax figure. This is surfaced in the Tax section of the table, not a separate band.

**It leans on existing systems, not new ones.** The header **Overview** holds three `LineChart`s (net worth · assessed tax · accumulated tax, year over year). Data conditions (account balances not yet synced; off-currency transactions excluded) flow through the **problem/signal system** documented for Accounts — header rollup, row severity chip, and the `acct-problem` fix-it alert. File attach reuses the `AfmUpload` dropzone; tag selection is two `MultiSelect`s edited inline in the record's edit form.

> **Stack reality check.** Mirrors the *Yearly Tax Statement* backend: `TaxStatement` declared figures + `TaxStatementTag` (tax-payment / income roles) + `TaxStatementFile`, with the reconciliation `TaxStatementReport` computed on read. Derived net worth degrades gracefully (`derived.available=false`) when account balances aren't computed.
