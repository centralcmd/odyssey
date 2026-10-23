# Recorded Norges Bank responses

Real SDMX-JSON responses from Norges Bank's data API (issue #349), recorded on 2026-10-22 and stored
verbatim so the parsing and normalisation tests run against the provider's actual shape. Each file is
one response body:

| File | Request |
|---|---|
| `norgesbank-usd-sek-jpy-range.json` | `https://data.norges-bank.no/api/data/EXR/B.USD+SEK+JPY.NOK.SP?format=sdmx-json&startPeriod=2026-10-15&endPeriod=2026-10-21&locale=en` |
| `norgesbank-last-observation.json` | `https://data.norges-bank.no/api/data/EXR/B..NOK.SP?format=sdmx-json&lastNObservations=1&locale=en` |
| `norgesbank-first-observation-usd-eur.json` | `https://data.norges-bank.no/api/data/EXR/B.USD+EUR.NOK.SP?format=sdmx-json&firstNObservations=1&locale=en` |

The range file carries `UNIT_MULT = 2` series (JPY and SEK are quoted per 100). The last-observation
file includes the non-currency series `TWI`/`I44` and the discontinued `RUB`, `HRK` and `BGN`.

Licence: NLOD 2.0 (Source: Norges Bank). Re-record a file when the provider changes its response
shape, and keep the request URL above in step with `Odyssey.Core/Finance/ExchangeRateSync/Providers/`.
