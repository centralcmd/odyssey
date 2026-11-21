# Locations and weather points

Part of the architecture rules split out of the root [`CLAUDE.md`](../../CLAUDE.md). Read this before changing code in this area; the rules here are binding in the same way.

**Every place and address in the app is a `Location`** (issue #380). Events, series and entries link one
by id beside an optional detail string; a contact `Address` is a link row; `RealEstateDetails.LocationId`
is the property's address. Every key to `Locations` is `RESTRICT`. Six rules are easy to get backwards:

- **One write path: `LocationResolver` (`Odyssey.Core/Locations/`).** By id, `locations.read` is needed
  only when the link *changes* (re-sending the stored id needs no claim), a *new* link to an archived
  place is a `422`, and a contact or property link needs `Line1`/`City`/`CountryCode`. By fields,
  `locations.create` is checked *before* the lookup so a match and a miss look the same. Saves go through
  `resolver.SaveChangesAsync`, which re-reads on a `1062` on `AddressKey` and maps a `1452` to `422`.
  A geocode sent on the fields path (`latitude`/`longitude`/`timeZoneId`/`source`/`sourceReference` on
  `NewAddress` and `RealEstateDetailsDto`, `LocationGeocode`) **never overwrites an existing place**: it
  applies only to a place that path creates, while a match on `AddressKey` (a forwarder's survivor
  included), a `locationId`, a kept link and an in-place edit all ignore it. It is write-only — no read
  echoes it.
- **`AddressKey` is the identity, computed by `LocationAddressKey` (in `Odyssey.Dtos`, shared with the
  client), never by the database.** Case and whitespace fold; letters never do (`Å` ≠ `A`). A merged-away
  place keeps its key and forwards through `MergedIntoLocationId` (one hop), so find-or-create lands on
  the survivor.
- **`applyTo: AllUses` is the one path where a contact or property write edits a `Location`.** It needs
  `locations.update` and `expectedOtherUses`, recounted under `FOR UPDATE` inside one transaction with
  the record (`LocationTransactions`); a property writes its four fields only. It recomposes a `Name`
  or `AddressText` only when it equals the old auto-composition, and never touches coordinates. A
  case/whitespace-only change is promoted to it whatever `applyTo` says.
- **`LocationReferenceGuard` is the one definition of "used"**: the five tables plus a forwarder. Weather
  is not a use (issue #387): a place's weather link is a column on the place, and the preference and the
  household default name a weather point. Delete, merge, `?referenced=`, `usage` and
  `otherUses` all read it; a sixth referencing table extends it. Counts of a kind the caller cannot read
  are `null`, never `0`, and there is deliberately no total.
- **Merge and delete decide inside their own transaction**, first statement `FOR UPDATE` (both rows in
  ascending id order for a merge). The merge's per-class claim is the `DomainForbiddenException` shape,
  with a generic body; the delete `409` is generic too.
- **`locations.read` is pinned to exactly Admin, Owner, User and Guest**, `calendar.read` and
  `journal.read` each imply it, and every contact/property editor holds all three location write claims
  and all four record read claims (`AuthorizationPolicyTests`). Widening `locations.read` widens who
  reads every address. Logs carry ids and counts only — never a name, address or detail.

**Weather is kept per weather POINT, and a place only links to one** (issue #387). `WeatherPoint` is a named
position with its own versioned sites, schedule and data; every weather table is keyed on `WeatherPointId`,
and `Locations.WeatherPointId` (nullable, `SET NULL`) is the link — many places per point, never a point to a
point. Rules that are easy to get backwards:

- **The link has one writer**, `WeatherLinkService` (`PUT /api/locations/{id}/weather-point`, `locations.update`
  + `weather.read`), which locks the place `FOR UPDATE` first and maps a `1452` to `422` keyed
  `weatherPointId`. No location, contact, property or point write takes a point id or a location list; a merge
  keeps the survivor's link, else inherits the source's.
- **`WeatherPoints.CurrentSiteVersion` has no foreign key** — the site depends on the point, so a key back
  would be a cycle. The point service writes both in one save. `SettingsVersion` is the Admin's concurrency
  token (rotated by create, `PUT` and purge start only); the fetch fence stays on `WeatherPointSchedule.Version`.
- **Runs, observations and days reference the point with `RESTRICT`** (delete goes through the batched purger);
  sites, schedule and stations cascade; `WeatherForecastSteps` has no key to the point at all.
- **Distances are whole kilometres** (`WeatherDistance`), computed at read time, never stored.
- **A converted point kept its location's GUID.** Nothing may resolve a bare id across `Locations` and
  `WeatherPoints`.
