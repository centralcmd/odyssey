# Vendored MapLibre GL JS

The location map (issue #371) draws with **MapLibre GL JS 6.14.0**, vendored here rather than taken from a
CDN or a Blazor wrapper. `wwwroot/js/location-map.js` is the only file that imports it, and only on the
first map, never at app start. The service worker keeps this folder out of its install-time precache.

| | |
|---|---|
| Version | `maplibre-gl@6.14.0` (published 2026-10-11) |
| Source | https://registry.npmjs.org/maplibre-gl/-/maplibre-gl-6.14.0.tgz |
| npm integrity | `sha512-L7U9y9TyPJ3P/JJbW+eH+A3FzdPq+3fnZFqqeCwf/S6cRGganHwVI5b/C8/zOYzreUzsloDPZVVaMQA94wOt4A==` |
| Licence | BSD-3-Clause, `LICENSE.txt` (also listed in `THIRD-PARTY-NOTICES.md`) |
| Maintainers on npm | 4 (`nyurik`, `harelmazor`, `maplibreorg`, `birkskyum`), under the MapLibre organisation |

## The files, byte-identical to the package

| Here | In the package | SHA-256 |
|---|---|---|
| `maplibre-gl.js` | `dist/maplibre-gl.mjs` | `244d36122c1398da3a8259817cc6d71e04321afe6404f32606b4b6c6cc4f26fb` |
| `maplibre-gl-worker.js` | `dist/maplibre-gl-worker.mjs` | `5f1e2f0e1624082b7e88a06e4c86085992b0f8c02929801e552f3f7a119856a9` |
| `maplibre-gl.css` | `dist/maplibre-gl.css` | `bacba6a85a867fcf602f2ff88331b8acf7edf3fbd063a519888cfbff39970193` |
| `LICENSE.txt` | `LICENSE.txt` | `ee5fc05a0677eaf69601d2c7db0d9ecd6cc27c3abc1d0733bc9ed34707cf8ef2` |

The two `.mjs` files are renamed to `.js` and are otherwise unchanged. nginx's stock `mime.types` maps
`.js` but not `.mjs`, and a module script served as `application/octet-stream` is refused by the browser.
Because the main module would otherwise look for `maplibre-gl-worker.mjs`, `location-map.js` sets the
worker URL explicitly with `setWorkerUrl`. Their trailing `sourceMappingURL` comments still name the
`.mjs.map` files, which are not vendored, so devtools shows a 404 for the map and nothing else.

`MapLibreBundleIntegrityTests` (`Odyssey.Client.Tests`) pins every hash above and the version named in this
file, so an unreviewed byte change fails CI.

## CSP

- 6.x ships ES modules: `import()` loads the main module from `'self'`, so `script-src` is unchanged.
- A **same-origin** worker URL is started as `new Worker(url, { type: 'module' })`, so `worker-src 'self'`
  is enough. The blob fallback only runs for a cross-origin worker URL, which this deployment never sets.
- No `unsafe-eval` is needed. The worker has one `globalThis.eval`, in the loader for a user-supplied
  plugin script (`importScriptInWorkers` / the RTL text plugin). Odyssey never calls it, and the CSP
  would block it.

## Re-checking the vendored copy

Dependabot and `dotnet list package --vulnerable` cannot see these files. On every Odyssey release:

1. Read https://github.com/maplibre/maplibre-gl-js/security/advisories and the release notes since
   6.14.0.
2. On any advisory that affects the pinned version, bump. Pick a release at least two weeks old.
3. To bump: `npm pack maplibre-gl@<version>` into an empty directory, copy
   `dist/maplibre-gl.mjs` → `maplibre-gl.js`, `dist/maplibre-gl-worker.mjs` → `maplibre-gl-worker.js`,
   `dist/maplibre-gl.css` and `LICENSE.txt`, then update the version and the hashes here and in
   `MapLibreBundleIntegrityTests`.
4. Exercise the map on the Compose stack: the CSP build has no `blob:` and no `unsafe-eval` to fall back on.
