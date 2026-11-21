# Tiingo fixtures (issue #447)

Two kinds of file live here, and the difference matters.

**Recorded live** (2026-11-11, `api.tiingo.com`, no valid token involved — nothing to scrub):

- `error-403-no-token.json` — `GET /tiingo/daily/AAPL` with no `Authorization` header: `403`, `application/json`.
- `error-403-invalid-token.json` — the same request, and the prices request, with a syntactically valid but
  unissued token: `403`, `application/json`. Tiingo checks the token before the symbol: an unknown ticker gives the
  same response. This is why `403` maps to `Configuration` rather than the spec's provisional `UnsupportedInput`.

**Authored from Tiingo's public documentation** (shape as documented; values illustrative) — everything else. They
were not recorded with a live token, because none was available to the implementer. Recording them for real
(issue #447 §3.6) means replacing each file with a scrubbed recording of the same request; the tests read only
`ticker`, `exchangeCode`, `startDate`, `endDate`, `date` and `close`, so a recording that differs elsewhere needs no
test change.

`TiingoFixtureLintTests` fails on any token-shaped string in this folder outside the sentinel
`TIINGO-FIXTURE-SENTINEL`.
