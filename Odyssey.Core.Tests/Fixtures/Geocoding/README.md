# Recorded geocoding responses

Real responses from the two providers the location search proxies (issue #350), recorded on
2026-10-22 and stored verbatim so the parsing and mapping tests run against the providers' actual
shapes rather than a hand-written guess. Each file is one response body:

| File | Request |
|---|---|
| `kartverket-adresser-storgata-1.json` | `https://ws.geonorge.no/adresser/v1/sok?sok=storgata%201&treffPerSide=10` |
| `kartverket-stedsnavn-storgata-1.json` | `https://ws.geonorge.no/stedsnavn/v1/navn?sok=storgata%201&fuzzy=true&utkoordsys=4258&treffPerSide=10` |
| `kartverket-adresser-al.json` | `https://ws.geonorge.no/adresser/v1/sok?sok=%C3%A5l&treffPerSide=10` |
| `kartverket-stedsnavn-al.json` | `https://ws.geonorge.no/stedsnavn/v1/navn?sok=%C3%A5l&fuzzy=true&utkoordsys=4258&treffPerSide=10` |
| `openmeteo-lisbon.json` | `https://geocoding-api.open-meteo.com/v1/search?name=lisbon&count=10&language=en&format=json` |
| `openmeteo-no-results.json` | the same endpoint for a name with no match |

Both sources are CC BY 4.0 (© Kartverket, © Open-Meteo). Re-record a file when a provider changes
its response shape, and keep the request URL above in step with the code in
`Odyssey.Core/Journal/Geocoding/`.
