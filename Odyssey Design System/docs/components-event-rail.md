# Components — Event rail

> Part of the [Odyssey Design System](../README.md) docs. Foundations, tokens and the component catalog live in the README; this file is the per-feature detail.


**`EventRail` / `EventRailItem` / `EventRailMarker`** is the **continuous-rail** history list: one unbroken line running the full height of the track, with 32px icon nodes sitting on it and markers — years, endpoints — on the same line *between* rows.

**It exists alongside `Timeline`, not instead of it.** `Timeline` draws a rail segment per item with a plain dot node and a right-hand figures column: right for an effective-dated record table (terms, estimates), where each row is a value to compare. `EventRail` is for a **log of things that happened**, where the reader follows the line. A `Timeline` cannot do the job with a flag, because its rail restarts per item — a marker between two rows would break the line.

Nodes are **opaque and haloed in the surface colour**, so the line stops at each circle rather than striking through it, and neutral by default (`color` is available where the kind's hue is genuinely the fastest read). `capTop` / `capEnd` say whether this page holds the real newest / oldest end of the log; an **uncapped end fades** rather than cutting, because a paged middle genuinely continues. Row `actions` sit inline after the date rather than pinned to the card edge — they belong to the entry being read — and anything given the `.odc-er-meta` class (a provenance line, say) reveals on the same hover or keyboard focus. A **marker** takes the same provenance through its `meta` prop, for an endpoint with an author worth naming.
