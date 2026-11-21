# Vendored pmtiles

The self-hosted basemap (issue #384) reads one PMTiles archive with HTTP range requests through the
**pmtiles** JS library, vendored here rather than taken from a CDN. `wwwroot/js/location-map.js` is the only
file that loads it, on the first map only, next to MapLibre. The service worker keeps this folder out of its
install-time precache.

| | |
|---|---|
| Version | `pmtiles@4.5.0` (published 2026-08-10) |
| Source | https://registry.npmjs.org/pmtiles/-/pmtiles-4.5.0.tgz (git head `3b10e67edb65c6b04549f74c0279cef8328d859c`) |
| npm integrity | `sha512-CBeD4SoUluFziGdy/8k7FOjQxQy486n+929W/tWophauvMICpkZ26vGBWhDt/6b1FZQWNaf4jFdT/5+q0Wii5w==` |
| Licence | BSD-3-Clause, `LICENSE.txt`; it bundles `fflate` (MIT, `fflate-LICENSE.txt`). Both are listed in `THIRD-PARTY-NOTICES.md` |
| Maintainers on npm | 1 (`bdon`), Protomaps |

## The files

| Here | From | SHA-256 |
|---|---|---|
| `pmtiles.js` | `dist/pmtiles.js` in the npm tarball, byte-identical | `caf981bc46f6327ee7e65d5dc964d89d38a69f60edca2bd4c5c890c21b554c6c` |
| `LICENSE.txt` | `LICENSE` in `protomaps/PMTiles` at the tarball's git head (the tarball carries none) | `0371c38f338835f7fc13ed71176f3d92144e22c8b736a31cced57adbbeb647b3` |
| `fflate-LICENSE.txt` | `LICENSE` in the `fflate@0.8.2` tarball (pmtiles depends on `^0.8.2`) | `805f6cb28bb8b6d3a0badd83c93bccd9671fa01a3b4b92b7042b0743325ac243` |

**Why the classic build.** The ESM build (`dist/esm/index.js`) imports the bare specifier `"fflate"`, which a
browser cannot resolve without a bundler or an import map, and the CSP forbids an inline import map.
`dist/pmtiles.js` bundles `fflate` and defines the global `pmtiles`, so `location-map.js` loads it as a
`<script src>` from `'self'`. Its trailing `sourceMappingURL` names `pmtiles.js.map`, which is not vendored, so
devtools shows a 404 for the map and nothing else.

`PmtilesBundleIntegrityTests` (`Odyssey.Client.Tests`) pins every hash above and the version named here, and
asserts the bundle contains no `eval(`, `new Function`, `Worker(` or `blob:`.

## CSP

None of `script-src`, `worker-src` or `connect-src` changes: the script is same-origin, it starts no worker,
and it reads the archive with `fetch` against the deployment itself (`connect-src 'self'`).

## Re-checking the vendored copy

Dependabot and `dotnet list package --vulnerable` cannot see these files. On every Odyssey release, with
MapLibre's check (`../maplibre-gl/README.md`):

1. Read https://github.com/protomaps/PMTiles/security/advisories and the JS changelog since 4.5.0.
2. On any advisory that affects the pinned version, bump. Pick a release at least two weeks old.
3. To bump: `npm pack pmtiles@<version>` into an empty directory, copy `dist/pmtiles.js`, take `LICENSE` from
   the repository at the tarball's `gitHead`, re-check the bundle for `eval(`, `new Function`, `Worker(` and
   `blob:`, then update the version and the hashes here and in `PmtilesBundleIntegrityTests`.
4. Exercise the self-hosted map on the Compose stack.
