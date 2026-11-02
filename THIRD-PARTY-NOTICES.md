# Third-party notices

Odyssey itself is licensed under the BSD 2-Clause License — see [`LICENSE`](LICENSE).

This file lists the third-party material Odyssey redistributes or depends on, and the
licenses that material is made available under. It is maintained separately from
`LICENSE` on purpose: the repository `LICENSE` is hashed at runtime and served as the
agreement users accept at sign-in (see `Odyssey.Context/Legal/LicenseDocumentProvider.cs`),
so any edit to that file invalidates every existing acceptance.

---

## Bundled fonts

Odyssey self-hosts its webfonts rather than loading them from the Google Fonts CDN, so
that the application makes no third-party requests at runtime and no visitor IP address
is disclosed to Google. The font files live in
[`Odyssey.Client/wwwroot/fonts/`](Odyssey.Client/wwwroot/fonts/) and are declared in
`Odyssey.Client/wwwroot/css/fonts.css`.

| Font | Files | Copyright | License |
|---|---|---|---|
| Roboto | `roboto-*.woff2` (9 subsets) | Copyright the Roboto Project Authors | Apache License 2.0 |
| Roboto Mono | `robotomono-*.woff2` (6 subsets) | Copyright the Roboto Mono Project Authors | Apache License 2.0 |
| Material Icons | `materialicons-*.woff2` | Copyright Google Inc. | Apache License 2.0 |

All three are distributed under the Apache License, Version 2.0. You may obtain a copy
of the license at:

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software distributed under
the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY
KIND, either express or implied. See the License for the specific language governing
permissions and limitations under the License.

The font binaries are redistributed unmodified. Their upstream sources are
<https://fonts.google.com/specimen/Roboto>, <https://fonts.google.com/specimen/Roboto+Mono>,
and <https://fonts.google.com/icons>.

---

## Bundled JavaScript: MapLibre GL JS

The location map (issue #371) draws with **MapLibre GL JS 6.14.0**, redistributed unmodified in
[`Odyssey.Client/wwwroot/lib/maplibre-gl/`](Odyssey.Client/wwwroot/lib/maplibre-gl/) (two module files
renamed from `.mjs` to `.js`; their bytes are unchanged). It is served from the deployment itself, never
from a CDN. Version, source, hashes and the re-check procedure are in that folder's `README.md`.

| Component | Copyright | License |
|---|---|---|
| MapLibre GL JS 6.14.0 | Copyright (c) 2023, MapLibre contributors; portions Copyright (c) 2020, Mapbox | BSD 3-Clause License. `Odyssey.Client/wwwroot/lib/maplibre-gl/LICENSE.txt` carries the full text and the notices of the code MapLibre itself bundles |
| pmtiles 4.5.0 | Copyright 2021 Protomaps LLC | BSD 3-Clause License. `Odyssey.Client/wwwroot/lib/pmtiles/LICENSE.txt` carries the full text (taken from the source repository: the npm package ships none) |
| fflate (bundled inside `pmtiles.js`) | Copyright (c) 2023 Arjun Barrett | MIT License. `Odyssey.Client/wwwroot/lib/pmtiles/fflate-LICENSE.txt` |

The default dark basemap, `Odyssey.Client/wwwroot/maps/odyssey-dark.json`, is a **modified copy of
the Fiord Color GL style** as served by OpenFreeMap (its building layer and its label colours are
changed; the file's `metadata` records what and why). Copyright (c) 2024, MapTiler.com & OpenMapTiles contributors;
Copyright (c) 2015, CartoDB Inc. Its code is under the BSD 3-Clause License and its design under
CC-BY 4.0. `Odyssey.Client/wwwroot/maps/odyssey-dark.LICENSE.txt` carries the full text, copied from
https://github.com/openmaptiles/fiord-color-gl-style/blob/master/LICENSE.md; it derives
from "CartoDB Basemaps", designed by Stamen and Paul Norman for CartoDB Inc., CC-BY 3.0. The map's
caption credits OpenMapTiles as the licence requires.

The **self-hosted basemap** (issue #384) ships its styles, glyphs and sprite in the client image; only the
tile file is the operator's:

| Component | Where | License |
|---|---|---|
| Self-hosted dark style | `Odyssey.Client/wwwroot/maps/self-hosted-dark.json` | Odyssey Dark above, with only its sources, glyphs and sprite changed: the same Fiord Color GL terms, full text in `self-hosted-dark.LICENSE.txt` |
| Self-hosted light style | `Odyssey.Client/wwwroot/maps/self-hosted-light.json` | OpenFreeMap `liberty` (OpenFreeMap styles: MIT), a fork of OSM Liberty and OSM Bright: code BSD 3-Clause, design CC-BY 3.0 and CC-BY 4.0. Modified (sources, glyphs, sprite; the Natural Earth raster layer removed). Full texts in `self-hosted-light.LICENSE.txt` |
| Noto Sans glyphs (Regular, Bold, Italic; ten Latin, Greek and Cyrillic ranges as built by OpenFreeMap) | `Odyssey.Client/wwwroot/maps/self-hosted/fonts/` | SIL Open Font License 1.1, `fonts/OFL.txt` |
| OpenFreeMap `ofm` sprite (Maki icons and a public-domain arrow) | `Odyssey.Client/wwwroot/maps/self-hosted/sprites/` | Maki: CC0 1.0 Universal; the arrow: public domain. `sprites/LICENSE.txt` |

The test fixture `TestShared/Basemap/basemap.pmtiles` is OpenStreetMap data (ODbL 1.0) in the OpenMapTiles
schema (CC-BY 4.0); see `TestShared/Basemap/ATTRIBUTION.md`.

With the default basemap, the map's tiles, glyphs and sprites are **not** bundled: the browser fetches them from the configured
tile host (OpenFreeMap by default). Their data licences (OpenStreetMap's ODbL, OpenMapTiles) are credited
by the configured attribution caption under the map — see `docs/deployment.md` → *Location map*.

---

## NuGet dependencies

All package versions are pinned centrally in
[`Directory.Packages.props`](Directory.Packages.props). Every dependency is under a
permissive license; none are copyleft, and none impose obligations on Odyssey's own
licensing.

| Package | License |
|---|---|
| `Microsoft.*` (ASP.NET Core, EF Core, Extensions, Identity) | MIT |
| MudBlazor | MIT |
| Pomelo.EntityFrameworkCore.MySql | MIT |
| MySqlConnector | MIT |
| Mapster | MIT |
| MailKit / MimeKit | MIT |
| Ical.Net | MIT |
| QRCoder | MIT |
| Swashbuckle.AspNetCore | MIT |
| MessagePack | MIT |
| Bogus | MIT |
| Microsoft.Playwright | MIT |
| Aspire (AppHost SDK, Hosting) | MIT |
| bunit | MIT |
| Testcontainers | MIT |
| coverlet.collector | MIT |
| MetadataExtractor | Apache License 2.0 |
| xunit | Apache License 2.0 |
| AwesomeAssertions | Apache License 2.0 |
| Moq | BSD 3-Clause |
| Xunit.SkippableFact | Microsoft Public License (MS-PL) |

Two entries carry history worth knowing before changing them:

- **AwesomeAssertions replaces FluentAssertions.** FluentAssertions 7.x was Apache-2.0;
  8.0 moved to the Xceed license, which is free for open source but charges for commercial
  use. Rather than freeze at 7.2.2 indefinitely or take on a licence that would bind any
  future commercial use of this codebase, Odyssey moved to the Apache-2.0 community fork,
  which continues the same lineage and API.
- **`Xunit.SkippableFact` is MS-PL**, the only non-MIT/Apache/BSD entry here. It is a
  test-only dependency and is never shipped in a published artifact.

MetadataExtractor was chosen over ImageSharp specifically to avoid the latter's
Split license.

---

## Design system

The Odyssey Design System is first-party material authored for this project. It is no
longer in this repository; it lives in a claude.ai artifact (see
`.claude/design-system-artifact.md`), and nothing from it is distributed with the application.
Its preview pages load React 18.3.1 from a CDN at view time; that happens inside the artifact,
not in the deployed application. Nothing under `Odyssey.Client/` loads them, and the shipped
app makes no third-party requests at runtime — which is the reason the webfonts above are
self-hosted rather than pulled from the Google Fonts CDN.
