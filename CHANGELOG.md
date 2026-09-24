# Changelog

All notable changes to this project are documented here. The format is maintained
automatically by [release-please](https://github.com/googleapis/release-please) from
[Conventional Commits](https://www.conventionalcommits.org/), and the project follows
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.34.0](https://github.com/centralcmd/odyssey/compare/v0.33.0...v0.34.0) (2026-09-24)


### ⚠ BREAKING CHANGES

* **core:** POST/PUT of a term with TermKind other than Fee is a 400, and ExistingAccount no longer carries CurrentInterestRate or CurrentInterestRateKind.
* /api/insurance-policies is removed, along with the insurance.create/read/update/delete claims and the InsuranceExpiringSoonWindowDays, InsuranceMaxSummaryPolicies, InsuranceMaxRenewalsPerPolicy, InsuranceMaxFilesPerParent and InsuranceMaxLinksPerPolicy settings. DELETE /api/contacts/{id} renames its detach flag to detachBlockingLinks and no longer reports an insuranceLinks extension. The data export no longer emits the seven insurance tables.
* /api/subscriptions is removed, along with the subscriptions.create/read/update/delete claims and the SubscriptionRenewalWindowDays, SubscriptionMaxSummaryRenewals and SubscriptionMaxSummarySubscriptions settings. The data export no longer emits a Subscriptions table.

### Features

* **client:** sync account contracts and party view to the design system ([#186](https://github.com/centralcmd/odyssey/issues/186)) ([50eb76c](https://github.com/centralcmd/odyssey/commit/50eb76c2fdc5107ea4500c78a2b060c6e326c55d))
* **client:** sync term surfaces to the kindless design system ([#185](https://github.com/centralcmd/odyssey/issues/185)) ([4f1074f](https://github.com/centralcmd/odyssey/commit/4f1074fbadb2c05e69b280149bed6b6ba2daacfb))
* contract reference number (backend + frontend) ([#183](https://github.com/centralcmd/odyssey/issues/183)) ([3622b9e](https://github.com/centralcmd/odyssey/commit/3622b9ef745b23e9b5f7713d8cbbc00250a3e20a))
* **core:** add deposit contract type with depositor and custodian roles ([#188](https://github.com/centralcmd/odyssey/issues/188)) ([adcb971](https://github.com/centralcmd/odyssey/commit/adcb97161823a558b5b9ea3982f60c7f653c4d9f))
* **core:** fold rate term kinds into fees and remove TermKind ([#184](https://github.com/centralcmd/odyssey/issues/184)) ([439691c](https://github.com/centralcmd/odyssey/commit/439691c4e4d5478b32680763030450832eb3b559))
* remove the standalone insurance policy feature ([#180](https://github.com/centralcmd/odyssey/issues/180)) ([d1a0aa5](https://github.com/centralcmd/odyssey/commit/d1a0aa5fd9a93be09185f0eb6c252441078ceb9f))
* remove the standalone subscriptions feature ([#176](https://github.com/centralcmd/odyssey/issues/176)) ([27f62de](https://github.com/centralcmd/odyssey/commit/27f62dedc58383ee2755c973c9601f3f10b3df24))
* sync contract terms chart, name picker and rate direction to the design system ([#182](https://github.com/centralcmd/odyssey/issues/182)) ([afb91fb](https://github.com/centralcmd/odyssey/commit/afb91fb73687a765c1af3c48376f48ffdc0bb799))


### Documentation

* update design system ([fabd3d2](https://github.com/centralcmd/odyssey/commit/fabd3d28f7fa61e41da151becbe2f0944a852752))
* update design system ([aa6ab17](https://github.com/centralcmd/odyssey/commit/aa6ab17e5643d1af3d5cbc2c78c7e4d0d4d50dca))
* update design system ([aeeb3ce](https://github.com/centralcmd/odyssey/commit/aeeb3ce214f321e4da7d1fecade209e85790f93b))
* update design system ([bb6b2fc](https://github.com/centralcmd/odyssey/commit/bb6b2fcd2a64daec6f4069e9aad23409ffeb900a))
* update design system ([0468094](https://github.com/centralcmd/odyssey/commit/04680941f11c6ad395d8cb030be493e494c965ee))
* update design system ([3105a09](https://github.com/centralcmd/odyssey/commit/3105a09d70e9a3a01c38cd753c69e6d4382eb7ba))
* update design system ([654badd](https://github.com/centralcmd/odyssey/commit/654badd14579fcec80b2b4f4f09efa7845dee285))
* update design system ([3677683](https://github.com/centralcmd/odyssey/commit/3677683bc85231c5847332a67efd67b669d15ae4))

## [0.33.0](https://github.com/centralcmd/odyssey/compare/v0.32.1...v0.33.0) (2026-09-21)


### Features

* **client:** withhold the signature dates from the new-contract dialog ([430e49e](https://github.com/centralcmd/odyssey/commit/430e49e796f891af1fa08edcb15fc10695895279))
* contract event automation (backend + frontend) ([#163](https://github.com/centralcmd/odyssey/issues/163)) ([9c42c7f](https://github.com/centralcmd/odyssey/commit/9c42c7f77bedebe4d445a63eceb171e93a648c3e))
* contract object parties and a universal Guarantor (backend + frontend) ([#174](https://github.com/centralcmd/odyssey/issues/174)) ([cf07baa](https://github.com/centralcmd/odyssey/commit/cf07baaf5789eb9a1f62d9b1fbaa3d4093d4cf8c))
* contract smart tags (backend + frontend) ([#173](https://github.com/centralcmd/odyssey/issues/173)) ([2a49283](https://github.com/centralcmd/odyssey/commit/2a49283e21be65176e9de368b288626cc7f6ec38))
* contract term money direction (backend + frontend) ([#162](https://github.com/centralcmd/odyssey/issues/162)) ([a1293ae](https://github.com/centralcmd/odyssey/commit/a1293ae2cc70ab405b3fade1fd8dc938855fdbc2))
* **core:** contract party role matrix and the Loan contract type ([#160](https://github.com/centralcmd/odyssey/issues/160)) ([b434561](https://github.com/centralcmd/odyssey/commit/b4345616a0d533c704a69f966d00ba2231d45892))
* sync the frontend to the updated design system ([#156](https://github.com/centralcmd/odyssey/issues/156)) ([eac7e98](https://github.com/centralcmd/odyssey/commit/eac7e983dfec5ba5299c4da1b803d6ceb0c70eee))
* sync the smaller design-system changes since [#156](https://github.com/centralcmd/odyssey/issues/156) ([#170](https://github.com/centralcmd/odyssey/issues/170)) ([955d048](https://github.com/centralcmd/odyssey/commit/955d048129039700595144f086145159eb3e556a))


### Bug Fixes

* **client:** caption a contract party with its type alone ([ac7f495](https://github.com/centralcmd/odyssey/commit/ac7f4959123ae2f29b5f60cb20110f663e1ad309))
* **core:** refuse deleting a transaction tag that is still in use ([#171](https://github.com/centralcmd/odyssey/issues/171)) ([b7b18b7](https://github.com/centralcmd/odyssey/commit/b7b18b73c72e0091ab7b4b1f72541987d1dd8907))
* **shared:** bound the account smart-tag cap by the tagIds filter limit ([#172](https://github.com/centralcmd/odyssey/issues/172)) ([1c5d0ae](https://github.com/centralcmd/odyssey/commit/1c5d0ae37d3c6203b47543da66171a3f62168092))


### Documentation

* update design system ([2b461ae](https://github.com/centralcmd/odyssey/commit/2b461ae500d491200cc50400509172894b4516b6))
* update design system ([9ebc418](https://github.com/centralcmd/odyssey/commit/9ebc4188a98504e49548b70403a4ac4cea4b15de))
* update design system ([37b3e0e](https://github.com/centralcmd/odyssey/commit/37b3e0e0f49843196d97383813070b2fecd8daa3))
* update design system ([0700936](https://github.com/centralcmd/odyssey/commit/070093662ac08a4258e2b9acf21def1006dffdf8))
* update design system ([230e9f5](https://github.com/centralcmd/odyssey/commit/230e9f5229206feecca0478b87ef3493dc4c3edb))
* update design system ([b9528d1](https://github.com/centralcmd/odyssey/commit/b9528d1ef4fbedd6ae18b06c2acb352b7bfa5be0))
* update design system ([3586d93](https://github.com/centralcmd/odyssey/commit/3586d936a2f73778ba604dc3cf176d7ab63fb4e3))

## [0.32.1](https://github.com/centralcmd/odyssey/compare/v0.32.0...v0.32.1) (2026-09-20)


### Bug Fixes

* **client:** drop the table chrome from the empty contract documents section ([#150](https://github.com/centralcmd/odyssey/issues/150)) ([51c284b](https://github.com/centralcmd/odyssey/commit/51c284b223827829b47df9c6b462f225fbb41546))

## [0.32.0](https://github.com/centralcmd/odyssey/compare/v0.31.0...v0.32.0) (2026-09-20)


### ⚠ BREAKING CHANGES

* rename AccountTerm to Term and rework the cadence into interval + count ([#123](https://github.com/centralcmd/odyssey/issues/123))

### Features

* contract document validity fields ([#149](https://github.com/centralcmd/odyssey/issues/149)) ([14849da](https://github.com/centralcmd/odyssey/commit/14849daeb0bdca469f97ede561dae7dd17943091))
* contract events ([#143](https://github.com/centralcmd/odyssey/issues/143)) ([76fe765](https://github.com/centralcmd/odyssey/commit/76fe76505acc2ea87f55a69b3ebb549d04886396))
* contract party roles and terms ([#134](https://github.com/centralcmd/odyssey/issues/134)) ([a7ca7ea](https://github.com/centralcmd/odyssey/commit/a7ca7eabd0bab7d849233b9b5b7483fa76bf488d))
* contract signature lifecycle (Ready / Signed) ([#147](https://github.com/centralcmd/odyssey/issues/147)) ([1b8f023](https://github.com/centralcmd/odyssey/commit/1b8f023f0913e4dd0fdd7d300fab1824ecef1d3c))
* contract terms — what an agreement costs, as a dated history ([#137](https://github.com/centralcmd/odyssey/issues/137)) ([0f6a010](https://github.com/centralcmd/odyssey/commit/0f6a010560e6ab12b207bdefcaf99535385e0207))
* contracts run rate, next charges and four more contract types ([#139](https://github.com/centralcmd/odyssey/issues/139)) ([9da4b6d](https://github.com/centralcmd/odyssey/commit/9da4b6d6dc731c180fe51f8963d4cc56558d12f5))
* pause a contract ([#142](https://github.com/centralcmd/odyssey/issues/142)) ([70e189e](https://github.com/centralcmd/odyssey/commit/70e189e2453530671ccca956d99f47a19eb14846))
* rename AccountTerm to Term and rework the cadence into interval + count ([#123](https://github.com/centralcmd/odyssey/issues/123)) ([330dffa](https://github.com/centralcmd/odyssey/commit/330dffadce0bfae16760899257c91e4887a33d2c))


### Bug Fixes

* **client:** follow MudBlazor 9.10.0's menu roles in OdsTypeSelect and OdsMenu ([#133](https://github.com/centralcmd/odyssey/issues/133)) ([78b23e0](https://github.com/centralcmd/odyssey/commit/78b23e017ce24d261705c0d14e9019ef038d0ae7))
* **client:** sync the contracts surface to the design system ([#148](https://github.com/centralcmd/odyssey/issues/148)) ([fa1440a](https://github.com/centralcmd/odyssey/commit/fa1440a71eba54328acfac815cee6f14192b2e22))


### Documentation

* **client:** order the design-system sync and require a completeness pass ([a558aec](https://github.com/centralcmd/odyssey/commit/a558aec77a8b6e6ed81323354b7b13acf5ab0267))
* deduplicate the Claude skills onto shared rules files ([#116](https://github.com/centralcmd/odyssey/issues/116)) ([da718ec](https://github.com/centralcmd/odyssey/commit/da718ec27a8161fb7cb75ada5c03fc08147adbfa))
* **infra:** split the spec writer into backend and frontend skills ([f952011](https://github.com/centralcmd/odyssey/commit/f952011559547eb8561b20b7532be6d90f011fa1))
* require a milestone decision when filing an issue ([dfc8ce7](https://github.com/centralcmd/odyssey/commit/dfc8ce785aae9f29ee88a2ecec3eb406ac57af59))
* update design system ([036a76b](https://github.com/centralcmd/odyssey/commit/036a76bf4900dc7752f78c9f00624ae04b30fb67))
* update design system ([88b870a](https://github.com/centralcmd/odyssey/commit/88b870a46254e23239d89c1a96d9534d1cb36204))
* update design system ([3994682](https://github.com/centralcmd/odyssey/commit/3994682abc5a7121d6af8191c75d1e4f705d8e93))
* update design system ([9762e8c](https://github.com/centralcmd/odyssey/commit/9762e8c3f6a3df6e08c3ef7682f3e088f28b6614))
* update design system ([05ce3a3](https://github.com/centralcmd/odyssey/commit/05ce3a3224c7e06a4feb94c7764bd8f846142289))
* update design system ([2c5eabb](https://github.com/centralcmd/odyssey/commit/2c5eabbb0fa57156b067d11ca129cdd12705ca07))
* update design system ([c6a4a23](https://github.com/centralcmd/odyssey/commit/c6a4a23471cacc928d94394390bcbbd500042b50))
* update design system ([2391b99](https://github.com/centralcmd/odyssey/commit/2391b99dca29253d151929a4f99436993e60e9b5))


### CI/CD

* Bump the actions group with 3 updates ([102ecd4](https://github.com/centralcmd/odyssey/commit/102ecd4be87e2349a9fe2e0bc8e23a403fd61acd))
* Bump the docker group across 3 directories with 3 updates ([487f49a](https://github.com/centralcmd/odyssey/commit/487f49ab87c5840c7f8c2b111e00b4a9b20e31f7))
* stop Microsoft.NET.Test.Sdk landing in two dependabot groups ([18f04c5](https://github.com/centralcmd/odyssey/commit/18f04c57761eeb5cfd7b65efa1b74f071e76b933))

## [0.31.0](https://github.com/centralcmd/odyssey/compare/v0.30.0...v0.31.0) (2026-09-18)


### Features

* **client:** drop the visible filename caption from journal photo tiles ([#113](https://github.com/centralcmd/odyssey/issues/113)) ([f1595ff](https://github.com/centralcmd/odyssey/commit/f1595ff9e07e051581d1dd84269ddeefb70e6b9f))


### Bug Fixes

* **api:** evict the finance-caps entry for the insurance request caps ([#114](https://github.com/centralcmd/odyssey/issues/114)) ([22c4508](https://github.com/centralcmd/odyssey/commit/22c450897136c4f170c9a4c1c562dca2df5c4a02))


### Documentation

* update design system ([c74ebc8](https://github.com/centralcmd/odyssey/commit/c74ebc8151d96584fbe3cfdc6339ba08e5023b19))

## [0.30.0](https://github.com/centralcmd/odyssey/compare/v0.29.0...v0.30.0) (2026-09-17)


### Features

* sync the frontend to the task list card and journal entry card ([#111](https://github.com/centralcmd/odyssey/issues/111)) ([3b90421](https://github.com/centralcmd/odyssey/commit/3b90421ec2ba98843f1bcbb182ec6ebfdafd4fd6))


### Documentation

* update design system ([871b2d9](https://github.com/centralcmd/odyssey/commit/871b2d930904f714611bf48654a904c66e130d24))

## [0.29.0](https://github.com/centralcmd/odyssey/compare/v0.28.0...v0.29.0) (2026-09-17)


### Features

* **client:** merchant column and filter, party tile action menu ([#104](https://github.com/centralcmd/odyssey/issues/104)) ([3588d48](https://github.com/centralcmd/odyssey/commit/3588d48ad3e6f9658caea4167c32d5e0db9c2ae2))
* contact profile pictures and organization logos ([#91](https://github.com/centralcmd/odyssey/issues/91)) ([70a6d08](https://github.com/centralcmd/odyssey/commit/70a6d080a67624689d3fb115e60d9785cc850201))
* **infra:** pre-pull the testcontainers images at session start ([5d8bf6f](https://github.com/centralcmd/odyssey/commit/5d8bf6f50d1f27542dd9aac7bff5a11f5ab4116a))
* real net-worth history series for the dashboard chart ([08e74d7](https://github.com/centralcmd/odyssey/commit/08e74d7e09c1e6802420269c64a053c5fbef8bba))
* sync the frontend to the RowActions, Timeline and journal RecordCard design update ([#109](https://github.com/centralcmd/odyssey/issues/109)) ([8fec6db](https://github.com/centralcmd/odyssey/commit/8fec6db525cbd0ba219e99798bfd271b137e43a7))
* user profile pictures ([#107](https://github.com/centralcmd/odyssey/issues/107)) ([2c00f31](https://github.com/centralcmd/odyssey/commit/2c00f315216b7d0edec47ab977000241661cca08))


### Bug Fixes

* **api:** resolve file attribution ids to display names ([#108](https://github.com/centralcmd/odyssey/issues/108)) ([41ed051](https://github.com/centralcmd/odyssey/commit/41ed0514611baa5ed4f9eeb7cba90facaff3d7e5))
* **client:** compute dashboard net worth from the totals endpoint ([#89](https://github.com/centralcmd/odyssey/issues/89)) ([55ea77d](https://github.com/centralcmd/odyssey/commit/55ea77dcfb002652937c79f86ec121b768092c22))
* **client:** pin the informational chart strokes to device space ([74dbb8b](https://github.com/centralcmd/odyssey/commit/74dbb8b20ab307e1c80b4a22039e3df135ec0ed2)), closes [#102](https://github.com/centralcmd/odyssey/issues/102)
* **client:** show liability donut amounts unsigned ([#85](https://github.com/centralcmd/odyssey/issues/85)) ([686e6de](https://github.com/centralcmd/odyssey/commit/686e6dea83dc9403fed435ce640207421f447e1c))
* **client:** stroke the informational chart axis at 3:1 ([73b7c09](https://github.com/centralcmd/odyssey/commit/73b7c09a1b40e65eaed72a5da86a9e08e8415f78)), closes [#97](https://github.com/centralcmd/odyssey/issues/97)
* **core:** key net worth off the open/closed term, not Archived ([#105](https://github.com/centralcmd/odyssey/issues/105)) ([faaaa39](https://github.com/centralcmd/odyssey/commit/faaaa392d0057fbcdfcb83dea580b9d9b87ec007))
* **infra:** build the compose stack behind TLS interception ([#100](https://github.com/centralcmd/odyssey/issues/100)) ([d295469](https://github.com/centralcmd/odyssey/commit/d295469284e21d8896964f7ed1670c3efab26bf1))
* **infra:** make the session hook detect the SDK instead of assuming a path ([ccfa814](https://github.com/centralcmd/odyssey/commit/ccfa81422c4b85b5b98663b9e7a38528050cc76e))


### Documentation

* correct the private static field naming rule ([17622b7](https://github.com/centralcmd/odyssey/commit/17622b7c1309db0904e9b81ecc2a739f4dd72155))
* record the expanded environment-configuration script ([cec8ef8](https://github.com/centralcmd/odyssey/commit/cec8ef850b3e15b1f0ab4204e9b0fa961544523e))
* update design system ([1182d01](https://github.com/centralcmd/odyssey/commit/1182d0132938bfe7169b0a384b5d1f0497cdcffe))
* update design system ([753c218](https://github.com/centralcmd/odyssey/commit/753c21858ef991c0683954b8aee55a24e3d794fd))
* update design system ([3e50428](https://github.com/centralcmd/odyssey/commit/3e50428abad1a2731d13ebff7135d403abdb2391))
* update design system ([56d6f3f](https://github.com/centralcmd/odyssey/commit/56d6f3f6437a911a395caf7ae759138a600cfdb3))
* update design system ([7653b7e](https://github.com/centralcmd/odyssey/commit/7653b7e66489971f1ff77d0628173392da92ef34))
* update design system ([3371703](https://github.com/centralcmd/odyssey/commit/3371703812b90d54a1281dae3caaacb71f81f8c2))
* update design system ([5234c6e](https://github.com/centralcmd/odyssey/commit/5234c6e9345ee3ba8a2b425ff91eee4654fafc75))

## [0.28.0](https://github.com/centralcmd/odyssey/compare/v0.27.0...v0.28.0) (2026-09-14)


### Features

* **migrations:** drop the issue [#75](https://github.com/centralcmd/odyssey/issues/75) change-archive tables ([53a76a2](https://github.com/centralcmd/odyssey/commit/53a76a2deed4b709e36906eda7763466c585e314)), closes [#78](https://github.com/centralcmd/odyssey/issues/78)

## [0.27.0](https://github.com/centralcmd/odyssey/compare/v0.26.0...v0.27.0) (2026-09-14)


### ⚠ BREAKING CHANGES

* **core:** identify a budget item by its transaction tag

### Features

* **client:** mark required fields only and add card select ([6e381a4](https://github.com/centralcmd/odyssey/commit/6e381a4c229c820188334a973bfda9d55fb26f6a))
* **client:** sync record body and transaction dialog to design system ([c23c42d](https://github.com/centralcmd/odyssey/commit/c23c42d11f82af84ae56d956ddc0cac8d0d0533f))
* **core:** identify a budget item by its transaction tag ([f1204f1](https://github.com/centralcmd/odyssey/commit/f1204f165ed265e74277fc8dc62c77d09729e820))


### Bug Fixes

* **client:** expose required state to assistive tech and fix card select keys ([4365166](https://github.com/centralcmd/odyssey/commit/43651668f0a34db09d336accda0a9c480193e25f))


### Documentation

* update design system ([b0da16e](https://github.com/centralcmd/odyssey/commit/b0da16e583bd7549c179c61349953a3bce481e8b))
* update design system ([551014f](https://github.com/centralcmd/odyssey/commit/551014fff1c55286e2d7cf3a31565276336b5f9c))
* update design system ([351f182](https://github.com/centralcmd/odyssey/commit/351f182dc756a926c6d597560781973e9faa1c7b))


### CI/CD

* bump anthropics/claude-code-action in the actions group ([dfaec2c](https://github.com/centralcmd/odyssey/commit/dfaec2c2e92526abb4de75081d074cb1affd3967))
* bump the docker group across 3 directories with 3 updates ([8b9fcb8](https://github.com/centralcmd/odyssey/commit/8b9fcb87f20cf5128e6c2eb435f48d3ecbfca727))

## [0.26.0](https://github.com/centralcmd/odyssey/compare/v0.25.0...v0.26.0) (2026-09-11)


### Features

* **api:** export insurance, contracts, tax statements and subscriptions ([efc1cf7](https://github.com/centralcmd/odyssey/commit/efc1cf7662824dccda371c8eb09a4e1ca930a7ca))


### Bug Fixes

* **client:** make OdsTypeSelect's popup a real listbox ([6807e84](https://github.com/centralcmd/odyssey/commit/6807e840ef7ce78ac68f0304e2859c4cc4f8a069))


### Documentation

* update design system ([7b019e7](https://github.com/centralcmd/odyssey/commit/7b019e722778260b7e1208faf45074b6200d4ec1))

## [0.25.0](https://github.com/centralcmd/odyssey/compare/v0.24.0...v0.25.0) (2026-09-11)


### Features

* **core:** contact aliases and life-cycle dates ([772c04e](https://github.com/centralcmd/odyssey/commit/772c04eaf140e39f19067508c80c50da70b97f01))
* **core:** name an account term by its label, and cut TermKind to three ([cc47400](https://github.com/centralcmd/odyssey/commit/cc474000dc4aa9bb10b54c044ce33757d7557dac))
* label an organization's contact methods with an organization vocabulary ([126fd9c](https://github.com/centralcmd/odyssey/commit/126fd9c8bef098d6eba134d2d1a0f677d12bf15a))


### Bug Fixes

* **client:** stop re-signing a liability's interest rate ([#55](https://github.com/centralcmd/odyssey/issues/55)) ([0d3fb8f](https://github.com/centralcmd/odyssey/commit/0d3fb8f36052248744c1f4908ca47d8d109950a3))


### Refactoring

* **core:** remove unused contact relationship type ([bfbb518](https://github.com/centralcmd/odyssey/commit/bfbb518fe278e0dd34760844a561863058c03f75))
* **data:** drop dead columns, fix contact uid collation, retune indexes ([#46](https://github.com/centralcmd/odyssey/issues/46)) ([5850de2](https://github.com/centralcmd/odyssey/commit/5850de2060018ae10814e7a9a2e2342a639bb374))


### Documentation

* update design system ([0dd8b33](https://github.com/centralcmd/odyssey/commit/0dd8b33697646bf8bc9ac042bd4878b38b258532))

## [0.24.0](https://github.com/centralcmd/odyssey/compare/v0.23.1...v0.24.0) (2026-09-08)


### Features

* a picker that can select a contact or a tag can create one ([330c7c8](https://github.com/centralcmd/odyssey/commit/330c7c8d7c449860136b1c914f503694ab89abaa))

## [0.23.1](https://github.com/centralcmd/odyssey/compare/v0.23.0...v0.23.1) (2026-09-08)


### Documentation

* explain the aspire dashboard's dev-certificate banner on linux ([6d4c95c](https://github.com/centralcmd/odyssey/commit/6d4c95c65852b8cabbe2acf01b440960ce6b574e))
* update design system ([42bd158](https://github.com/centralcmd/odyssey/commit/42bd158e712bf3420e1515162d39225ec6126d71))

## [0.23.0](https://github.com/centralcmd/odyssey/compare/v0.22.0...v0.23.0) (2026-09-06)


### Features

* a money value is edited as one control with its currency ([bcfa9b1](https://github.com/centralcmd/odyssey/commit/bcfa9b10baf0b69d69722e82b8a906c297a518f0))


### CI/CD

* bump anthropics/claude-code-action in the actions group ([cd24b96](https://github.com/centralcmd/odyssey/commit/cd24b96979361297cd064bc0d83d4a8ab86d63bc))
* bump nginxinc/nginx-unprivileged ([eb60a5c](https://github.com/centralcmd/odyssey/commit/eb60a5c01105e580b29c1690029236303cb244f2))

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
