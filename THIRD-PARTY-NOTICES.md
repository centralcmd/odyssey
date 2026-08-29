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

The `Odyssey Design System/` directory is first-party material authored for this project
and is covered by the repository `LICENSE`. It bundles no third-party design kit, and
`_ds_bundle.js` embeds no third-party library — it is the project's own components compiled
from `components/*.jsx`.

Its `preview/*.html` and `components/*.html` pages do load React 18.3.1 and Babel Standalone
from the unpkg CDN at view time, so that a preview page opens in a browser with no build step.
Both are MIT-licensed. Note what this is and is not: those pages are authoring/reference
material for the design system, not part of the deployed application. Nothing under
`Odyssey.Client/` loads them, and the shipped app makes no third-party requests at runtime —
which is the reason the webfonts above are self-hosted rather than pulled from the Google
Fonts CDN.
