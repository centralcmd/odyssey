# Changelog

All notable changes to this project are documented here. The format is maintained
automatically by [release-please](https://github.com/googleapis/release-please) from
[Conventional Commits](https://www.conventionalcommits.org/), and the project follows
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.23.0](https://github.com/centralcmd/odyssey/compare/v0.22.0...v0.23.0) (2026-09-04)


### Features

* a money value is edited as one control with its currency ([bcfa9b1](https://github.com/centralcmd/odyssey/commit/bcfa9b10baf0b69d69722e82b8a906c297a518f0))

## [0.22.0](https://github.com/centralcmd/odyssey/compare/v0.21.0...v0.22.0) (2026-09-02)


### ⚠ BREAKING CHANGES

* a policy party is written one at a time and carries its term
* an insurance policy carries four link collections

### Features

* a policy party is written one at a time and carries its term ([8513433](https://github.com/centralcmd/odyssey/commit/851343378813b0e950eb57139c55edf07dc00d56))
* an insurance policy carries four link collections ([0cbc3cf](https://github.com/centralcmd/odyssey/commit/0cbc3cfdfae7727a6796e2f68582d98f7747d24a))


### Documentation

* update design system ([16a3c3b](https://github.com/centralcmd/odyssey/commit/16a3c3b84550fb278f99fb02a168db918ef1415b))

## [0.21.0](https://github.com/centralcmd/odyssey/compare/v0.20.0...v0.21.0) (2026-09-01)


### ⚠ BREAKING CHANGES

* an insurance document belongs to a renewal period

### Features

* an insurance document belongs to a renewal period ([05ba3f2](https://github.com/centralcmd/odyssey/commit/05ba3f2de8b2494ccbfe61ef9a91f8b1d376e7dc))


### Documentation

* update design system ([3b44ffe](https://github.com/centralcmd/odyssey/commit/3b44ffe412a1e013f859d40b1d7652e539f8aa73))

## [0.20.0](https://github.com/centralcmd/odyssey/compare/v0.19.0...v0.20.0) (2026-09-01)


### Features

* **client:** flatten the record lists the design system stopped expanding ([8fc9bc5](https://github.com/centralcmd/odyssey/commit/8fc9bc55ad1306a2cc16de127aa75c453275b523))

## [0.19.0](https://github.com/centralcmd/odyssey/compare/v0.18.1...v0.19.0) (2026-08-31)


### Features

* **client:** extend the RecordCard rollout to Budgets and Tax statements ([#21](https://github.com/centralcmd/odyssey/issues/21)) ([6995282](https://github.com/centralcmd/odyssey/commit/69952820f93196051c1db2512b16c52478788b19))
* roll out the design system's RecordCard pattern across the four record lists ([#19](https://github.com/centralcmd/odyssey/issues/19)) ([c080108](https://github.com/centralcmd/odyssey/commit/c08010869bf0bc4a9971893406ee25137752b816))


### Documentation

* update design system ([f7816ca](https://github.com/centralcmd/odyssey/commit/f7816ca4c7bee9305f2a444b59ec8e95d4aeeba3))
* update design system ([0ee3635](https://github.com/centralcmd/odyssey/commit/0ee363568a88d94214f7fb5d59348c181e9d3615))
* update design system ([051effc](https://github.com/centralcmd/odyssey/commit/051effc83286570028f98ab53f57db8b4546884d))

## [0.18.1](https://github.com/centralcmd/odyssey/compare/v0.18.0...v0.18.1) (2026-08-31)


### Bug Fixes

* **client:** stop the first-run gate chain falling through ([#15](https://github.com/centralcmd/odyssey/issues/15)) ([40240df](https://github.com/centralcmd/odyssey/commit/40240df61a7701da4fe53d81ced575c9888c381b))

## [0.18.0](https://github.com/centralcmd/odyssey/compare/v0.17.0...v0.18.0) (2026-08-31)


### Features

* **infra:** support a private localhost-only deployment ([#12](https://github.com/centralcmd/odyssey/issues/12)) ([575908e](https://github.com/centralcmd/odyssey/commit/575908e6000eb96c6cf4ff7cf05f0096fed1a6ee))


### Bug Fixes

* SELinux Caddyfile mount, and identity startup guards that cried wolf on every boot ([#14](https://github.com/centralcmd/odyssey/issues/14)) ([f7b71b0](https://github.com/centralcmd/odyssey/commit/f7b71b0017e707979d641e0ef4107fb4ebc08a32))

## [0.17.0](https://github.com/centralcmd/odyssey/compare/v0.16.1...v0.17.0) (2026-08-30)


### Features

* **config:** move the SMTP transport into System settings ([e86ef35](https://github.com/centralcmd/odyssey/commit/e86ef35cf611dd6e35c69507cf2c4fd9fc8e6021))


### Bug Fixes

* harden the prod env template and drop the config adoption step ([#6](https://github.com/centralcmd/odyssey/issues/6)) ([f0be123](https://github.com/centralcmd/odyssey/commit/f0be123fbe858c39cc37dec5062f55df0ce71957))


### Documentation

* update design system ([d6225af](https://github.com/centralcmd/odyssey/commit/d6225af798efdd611e324e359c3530e82eef70d1))

## [0.16.1](https://github.com/centralcmd/odyssey/compare/v0.16.0...v0.16.1) (2026-08-29)


### Bug Fixes

* **config:** leave the example IMAGE_TAG empty instead of a stale pin ([2eec883](https://github.com/centralcmd/odyssey/commit/2eec883feeb7de27f99e26f3785a31caf0730770))

## 0.16.0 (2026-08-29)

Initial public release.

Odyssey is a .NET 10 full-stack personal finance application — an ASP.NET Core API, a
Blazor WebAssembly client, and a single EF Core model over MariaDB, covering accounts,
transactions, budgets, insurance policies, subscriptions, tax statements, and a journal
with tasks, photos, calendars and contacts.

This is the first commit of the public repository. Development before this point happened
in a private repository and its history is not carried over, so this entry stands in for
every release up to and including 0.16.0 rather than restating them. The version is
continuous with that work — the assemblies, the container image tags and `/healthz` all
report 0.16.0 — so nothing here is a renumbering.

Subsequent entries are generated by release-please and will appear above this one.
