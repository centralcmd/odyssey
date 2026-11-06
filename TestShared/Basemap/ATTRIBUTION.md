# Self-hosted basemap test fixture

`basemap.pmtiles` is a 276 KB PMTiles v3 archive of OpenMapTiles-schema vector tiles, zoom 10–14, covering about
2 × 2 km around the demo data's *Cabin* location (61.52914 N, 10.13802 E, Ringebu). Issue #384's integration
and end-to-end tests serve it in place of an operator's extract. The demo *Home* location (Oslo) lies outside it.

- **Data:** © OpenStreetMap contributors, Open Database License 1.0 (https://www.openstreetmap.org/copyright).
  This file is a derivative database and stays under the ODbL.
- **Schema:** © OpenMapTiles, CC-BY 4.0 (https://github.com/openmaptiles/openmaptiles/blob/master/LICENSE.md).
- **Source:** the tiles were fetched once, on 2026-11-02, from OpenFreeMap's planet build
  (`https://tiles.openfreemap.org/planet`) and written with the Python `pmtiles` package by `build_fixture.py`
  beside this file. An operator builds a real extract with Planetiler instead (`docs/deployment.md` →
  *Self-hosting the basemap*).

Regenerate it only deliberately: the tests pin nothing about its tiles, but its bounds must keep containing the
Cabin location and excluding Home.
