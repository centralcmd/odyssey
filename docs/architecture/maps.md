# The location map (MapLibre)

Part of the architecture rules split out of the root [`CLAUDE.md`](../../CLAUDE.md). Read this before changing code in this area; the rules here are binding in the same way.

**The location map draws with vendored MapLibre GL JS, behind one interop seam** (issues #370, #371).
`Odyssey.Client/wwwroot/lib/maplibre-gl/` holds the upstream files byte-for-byte (its `README.md` records the
version, source and re-check procedure); `wwwroot/js/location-map.js` is the only importer, and only on the
first map. Rules that are easy to get backwards:

- **No CDN and no Blazor wrapper.** `MapLibreBundleIntegrityTests` pins every vendored hash, so a bump is a
  reviewed change to the files, the README and the test together. Dependabot cannot see these files.
- **The module files are `.js`, not `.mjs`.** nginx's stock `mime.types` has no `.mjs`, and a module script
  served as `application/octet-stream` is refused; `location-map.js` sets the worker URL to match.
- **Nothing is built from an HTML string, and MapLibre's attribution control stays off.** A location name
  and a remote style's attribution are untrusted; `LocationMapSourceLintTests` fails on `innerHTML`,
  `setHTML`, `MarkupString` and friends, and on any `attributionControl` other than `false`. The caption
  under the frame is Blazor's, from `GET /api/map-styles`.
- **A client-held default style URL is forbidden.** `MapStylesCache` has no fallback: a failure is the
  map's `config-unavailable` state, never a URL restated in the client.
- **The shipped dark basemap is Odyssey's own file, `wwwroot/maps/odyssey-dark.json`** — OpenFreeMap `fiord`
  with its buildings lifted from ~16 % opacity (1.1:1, houses invisible) to 90 % (~1.6:1) and every label to
  ≥ 4.5:1. It is served same-origin, so the CSP is unchanged, but its tiles, glyphs and sprites still come from
  OpenFreeMap: `MapOptions.ShippedStyleRemoteOrigins` declares that, so the off-switch (empty origins) refuses
  to start with it selected. A root-relative style otherwise passes the origin check on its own. A new style
  under `wwwroot/maps/` that loads from a remote host must be listed there; `MapStylesApiTests` fails if not.
  `OdysseyDarkBasemapTests` pins the contrast, the tile host and the licence (full text beside the file).
  Nothing under `maps/` is precached.
- **The self-hosted basemap (issue #384) is one PMTiles file nginx serves behind `auth_request`.** The
  client container serves exactly `/basemap/basemap.pmtiles` from the read-only `basemap` volume; nginx asks
  `GET /api/map-styles/basemap-access` (`locations.read`) and caches the 204 for 60 s. Rules that are easy to
  get backwards: the cache key is the **whole `Cookie` header** — the identity cookie is chunked and its base
  value is the literal `chunks-N` in every session, so any single-cookie key is an auth bypass; the `Range`
  check is an **allow-list** (`^bytes=[0-9]+-[0-9]+$`), because nginx answers an unknown unit or an
  over-`max_ranges` request with the whole file; the rate limits key on the **last `X-Forwarded-For`
  element** (behind Caddy every connection has one address); and **no `add_header`** in those locations.
  `pmtiles` is vendored like MapLibre (`lib/pmtiles/`, `PmtilesBundleIntegrityTests`) as its classic build,
  whose ESM build imports a bare `"fflate"` the CSP cannot map. `MapOptionsValidator` checks the file's
  header at start (V1–V11) and fails closed. `BasemapNginxTests` exercises the template in the real image.
- **The service worker must not precache the map's files** (`offlineAssetsExclude`), or the first visit
  downloads MapLibre for a page the user may never open.
