# Maintainability review — November 2026

The senior architect reviewer read the whole repository without running the app (a static review). The focus was dead code, obsolete code, and maintainability in general. Every finding below was checked by searching the live tree.

**Baseline**

- The Release build has **0 warnings**, because `TreatWarningsAsErrors` is on.
- A second Debug build turned on XML doc generation and the dead-code analyzers. That build is where the stale doc references (O6) come from.
- **None of the fixes needs a database migration.**

## Summary

| Category | High | Medium | Low |
|---|---|---|---|
| Dead code (nothing references it) | 0 | 3 | 4 |
| Obsolete leftovers from removed features | 0 | 1 | 6 |
| Unused packages | 0 | 2 | 2 |
| Duplication | 0 | 1 | 1 |
| Maintainability, config and docs | 0 | 3 | 4 |

Effort: **S** = small, **M** = medium. "Confirm?" means the item may be a deliberate keep, so check before acting on it.

## 1. Dead code

| ID | Sev | Where | Evidence | Fix | Effort | Confirm? |
|---|---|---|---|---|---|---|
| D1 | M | `Odyssey.ApiClient/Resources/JournalApiClient.cs:117,197` `ExportIcsAsync` | Referenced only in its own file. It also buffers exports that can reach 10 GB in memory. | Delete it and fix the crefs at lines 125 and 137. | S | — |
| D2 | M | `CalendarApiClient.cs:49/76/85/132` (`ExportAsync`, `ExportEventAsync`, `ExportPatternAsync`, `ExportAggregateAsync`); `JournalApiClient.cs:267/274` (`IJournalIcsApiClient.ExportAsync`/`ExportOneAsync`) | Called only from ApiClient.Tests. The comments say they are kept "for non-browser consumers", but the only such consumer (E2ETests.Api) uses none of them. | Delete them, or document which consumer they are kept for. | S–M | Yes |
| D3 | M | `PhotoMetadataService.ExtractForPhotoAsync` (`:38`) → `PhotoOriginalSource.ReadPrefixAsync` (`:55`); `StoredFileContentSource.ReadPrefixAsync` (`:64`) and `IsPresentAsync` (`:83`) | No callers anywhere. | Delete all four. | S | — |
| D4 | L | `JobRunRecorder.OpenBurst` (`:155`), `IcsImportGrouper.SpilledBytes` (`:83`), `PhotoOriginalWriter.HasReserve` (`:36`), `OdsTagColorPicker.BlocksSubmit` (`:340`), `NonPositiveExchangeRateException.ExchangeRateIds` (`:16`) | Each is referenced only by its own declaration. | Delete. | S | Yes, `ExchangeRateIds` only (may be wanted for diagnostics) |
| D5 | L | `FileValidationService.ComputeSha256HashAsync` (`:131`), `ContactService.ListAllMatching` (`:100`) | Only tests call them. | Delete them, or make them `internal`. | S | Yes, `ListAllMatching` |
| D6 | L | `Odyssey.Api.Tests.csproj:38` (`Compile Remove="FirstUserRoleAssignmentTests.cs"`); `Odyssey.Client.csproj:40-42` (`wwwroot\appsettings.Development.json`) | Both point at files that no longer exist. | Delete both items. | S | — |
| D7 | L | `odyssey-components.css`: `.odc-colorpick-label/-input/-help/-error` (3295–3300), `.odc-toast-action` (4983), `.trm-chart` (2196), `.trm-delta` (2185), `.trm-dir-refused` (2261). `AccountProfileSection.razor.css:13` `.acc-picture-meta`. `wwwroot/odyssey-logo-animated.svg`, `odyssey-wordmark.svg`. | No `.razor`, `.cs` or `.js` file uses them. | Delete, unless they are kept to match the design system. | S | Yes |

## 2. Obsolete leftovers from removed features

| ID | Sev | Where | Problem | Fix | Effort |
|---|---|---|---|---|---|
| O1 | L | `AddContractPartyDialogTests.cs:22`, `ContractPartyTileTests.cs:36`, `ContractDocumentsSectionTests.cs:30`, `PartyTermTests.cs:7`, `AddContractPartyDialog.razor:38`, `ContractGenerator.cs:16,64,263`, `ContractPartyRoleGuardTests.cs:306`, `NetWorthMembershipSourceTests.cs:20`, `ContactMutationLockRetiredTests.cs:10` | Comments still describe the removed insurance-policy feature and name test classes that were deleted with it. "Three party kinds" should now be Account, Institution and Property. | Rewrite or delete the comments. | S |
| O2 | M | `Odyssey.MigrationService/MigrationDriftException.cs:9-14`, `MigrationRunner.cs:232` | The remarks and the `contextName` parameter assume there are two EF contexts sharing one history table. There is now one. | Correct the remarks and drop `contextName`/`ContextName`. Four test call sites change. | S |
| O3 | L | `docker-compose.yml:82-87`, `Odyssey.AppHost/AppHost.cs:172-176`, `Odyssey.MigrationService.csproj:28-29` | One comment says the mail transport still comes from config; issue #8 moved it into settings. Another implies no deployment needs a settings carry-over, which stopped being true at v0.35.0. One names `Odyssey.Journal`. | Reword. | S |
| O4 | L | `.github/ISSUE_TEMPLATE/bug_report.yml:24,26,97`, `feature_request.yml:24`, `.claude/agents/senior-tester.md` | They name `Odyssey.Finance`, a project that was merged into `Odyssey.Core`. | Change to `Odyssey.Core (Finance/)`. | S |
| O5 | L | `PasswordResetMailTests.cs:105`, `SwaggerProductionLockoutTests.cs:77` | They mention the deleted `EmailOptionsProductionValidationTests`. | Point at `EmailTransportStartupTests`. | S |
| O6 | L | About 50 unresolved or malformed doc references (CS1574/CS0419/CS1570), e.g. `PropertyFileServiceTests.cs:20` (`FileReferenceGuard`), `TaxStatementStatusVisuals.cs:9` (`OdsCoverageStatus`), `ContractEvent.cs:18,35`, `IcsPlaces.cs:173`, the `*LimitsCache.Fallback` crefs, `JobRuns.cs:274`, `UpdateContractFileRequest.cs:15` | They point at removed or renamed types. | Fix them, then do M3. | S–M |

**Already clean (O7).** These removed features have no code left behind: `FileBlob`/`FileBlobId`, the two backfill jobs, `IFileRenameObserver`, `SystemSettingsConfigAdoption`, `EmailOptions`, the `data.export` claim, the `Odyssey Design System/` folder, and the old split projects. What remains are retired-ordinal comments and retirement guard tests, which CLAUDE.md requires. **Keep them.**

## 3. Unused packages

| ID | Sev | Package | Where | Evidence | Fix | Confirm? |
|---|---|---|---|---|---|---|
| P1 | M | `Swashbuckle.AspNetCore.Filters` | `Odyssey.Api.csproj:19`, `Directory.Packages.props:65` | No code uses it. | Remove from both files. | — |
| P2 | M | `Microsoft.AspNetCore.OpenApi` | `Odyssey.Api.csproj:11` | No `AddOpenApi`/`MapOpenApi`; Swashbuckle generates the OpenAPI document. | Remove, then build. | Yes |
| P3 | L | `Moq`, `AwesomeAssertions` | `Odyssey.Core.Tests.csproj:44,46` | Moq is unused there. AwesomeAssertions is used by one file, `DemoDataSetTests.cs`, with about 30 assertions (the review first counted 2). | Drop Moq. Keep AwesomeAssertions unless that file is rewritten. | — |
| P4 | L | `Ical.Net`, `Mapster`, `DataProtection.Abstractions`, `Extensions.DependencyInjection`, `MySqlConnector` | `Odyssey.TestData.csproj:15-21` | No direct use; they arrive through Core/Context anyway. | Remove, then build. | — |

**Checked and kept:**
- `MessagePack` is pinned on purpose against a known vulnerability.
- `MetadataExtractor` in Core is used at runtime by the photo-library metadata extractor.

## 4. Duplication

| ID | Sev | Where | Problem | Fix | Effort |
|---|---|---|---|---|---|
| U1 | M | About 12 `Contract*Tests` files in Core.Tests, plus 5 `Contract*RelationalTests` in IntegrationTests | Each copies its own `Parties`/`Files`/`Summaries`/`CreateService`/`ServerContext` helpers. Many are never called, and `DropAsync` (`ContractFileValidityRelationalTests.cs:290`) is dead. | One shared `ContractServiceHarness`. | M |
| U2 | L | `Odyssey.Client/Services/ContactQuickCreate.cs`, `TagQuickCreate.cs` | Identical bookkeeping for the temporary ids they hand out. Their `_`-prefixed fields also break the naming rule for `Services/`. | Extract a shared base class and fix the field names. | S–M |

## 5. Maintainability, config and docs

| ID | Sev | Problem | Fix | Effort | Confirm? |
|---|---|---|---|---|---|
| M1 | M | About 380 `[SwaggerOperation]`/`[SwaggerParameter]` attributes have no effect: `OpenApiExplorerExtension.cs:72` never calls `options.EnableAnnotations()`. | Add the call. | S | Yes — check that `swagger.json` has no `summary` fields today |
| M2 | M | `.editorconfig:182-190,205-210` says private statics are camelCase and calls the Blazor `_camelCase` style legacy, both against CLAUDE.md. The carve-out also covers all of `Odyssey.Client/**.cs`. | Make statics PascalCase, scope the carve-out to components, rewrite the comments. | S | — |
| M3 | M | Nothing stops stale doc references from piling up again (that is how O6 grew). | Turn on `GenerateDocumentationFile`, suppress only CS1591, after fixing O6. Crefs across project boundaries become `<c>…</c>`. | M | — |
| M4 | L | `csharp_prefer_braces = true:warning` is declared but not enforced: 2,097 IDE0011 violations. | Either relax it to `when_multiline`, or enforce it with one `dotnet format` pass. | S/M | Decision needed |
| M5 | L | 237 `{ get; init; }` properties in `Odyssey.Dtos` (185 in `WeatherDtos.cs`) break the DTO rule. They are read-only response DTOs. | Convert them, or write the exemption into CLAUDE.md. | S–M | Decision needed |
| M6 | L | `Odyssey.TestData/Fixtures/ContactImageFixtures.cs` (499 lines of adversarial test images) is built into the MigrationService and Worker images. Only tests use it. | Move it to `TestShared/Fixtures/`. | S | — |
| M7 | L | `WeatherDayObserved.DayDefinition`/`PrecipitationDayDefinition` (`WeatherDtos.cs:389-390`) are never set. | Delete, or keep if they describe the wire format on purpose. | S | Yes |

### Largest files

| # | File | Lines | Suggested split |
|---|---|---|---|
| 1 | `Odyssey.Client/wwwroot/css/odyssey-components.css` | 5121 | one file per component family |
| 2 | `Odyssey.Core/Journal/CalendarIcsService.cs` | 3402 | export planner/writer; import grouping/apply; attachment phases A/B |
| 3 | `Odyssey.Client/Pages/Settings.razor.cs` | 3385 | more partials: the catalogue, then one per settings group |
| 4 | `Odyssey.Core/Journal/ContactVCardService.cs` | 1920 | export vs import |
| 5 | `Odyssey.Core/Journal/ContactService.cs` | 1744 | queries vs changes vs conversion |
| 6 | `Odyssey.Client/Pages/Internet/NewsPage.razor.cs` | 1680 | child components |
| 7 | `Odyssey.Core/Internet/News/Html/NewsHtmlPrescan.cs` | 1672 | tokenizer vs nesting model |
| 8 | `Odyssey.Core/Journal/Thumbnails/PhotoThumbnailJob.cs` | 1496 | claiming, rendering, crash breaker |
| 9 | `Odyssey.Client/Pages/Finance/ContractsCard.razor.cs` | 1292 | summary header vs list |
| 10 | `Odyssey.Api/SystemSettings/SystemSettingsRegistry.cs` | 1254 | one partial per settings group |

## Phase 1 status

Phase 1 is done, on the branch for this PR.

| Item | Result |
|---|---|
| D6 | Both stale `.csproj` items removed. |
| P1, P4 | Packages removed. |
| P3 | `Moq` removed. `AwesomeAssertions` kept (see P3). |
| D1, D3 | Removed. |
| D4 | Removed, except `ExchangeRateIds` (still waiting on a decision). |
| O1–O5 | Comments and templates rewritten. |
| O2 | `contextName`/`ContextName` dropped. The remarks now reflect the single context. |
| M1 | `EnableAnnotations()` added. A new test, `OpenApiSecuritySchemeTests.SwaggerOperationAnnotations_ReachTheDocument`, failed before the change and passes after it. |
| M2 | `.editorconfig` updated: private statics are PascalCase, and the `_camelCase` carve-out is limited to `Pages/`, `Components/` and `Layout/`. The fields in `ContactQuickCreate`/`TagQuickCreate` were renamed to match. This is the naming half of U2; the shared base class is still open. |

Out of scope and left as they are: comments in `Odyssey.TestData/Catalog/Contacts.cs` that explain the demo contacts in terms of insurance "link collections". Rewriting them needs the demo-data context, so they belong with O6 in Phase 2.

## Phase 2 status

Phase 2 is done, on the branch for this PR.

| Item | Result |
|---|---|
| O6 | About 50 stale or malformed doc references fixed. A reference to a type in a project this one cannot reference (Core naming an Api type) became `<c>Name</c>`; the rest now point at the right type or member. Two orphaned doc blocks were removed (`JobRuns.cs`, `WeatherFetchService.cs`). |
| M3 | `Directory.Build.props` turns on `GenerateDocumentationFile` for every project. A stale `cref`/`paramref` or malformed doc XML now fails the build. Three doc warnings are silenced, with the reason written beside them: CS1591 (member has no doc comment), CS1573 (one `<param>` tag missing, ~490 sites) and CS0419 (a cref to an overloaded method, which still links). A client publish was checked: no `.xml` doc files ship to the browser. |
| M6 | `ContactImageFixtures.cs` moved to `TestShared/Fixtures/` (namespace `Odyssey.Testing.Fixtures`) and linked into Api.Tests, Core.Tests, IntegrationTests and E2ETests.Api. It no longer builds into the MigrationService and Worker images. |
| U2 | `ContactQuickCreate` and `TagQuickCreate` share a `QuickCreateLedger` (temporary ids, pending creates, resolution). Composition, not a base class. |
| P3 | `DemoDataSetTests.cs` rewritten with xUnit `Assert`. `AwesomeAssertions` removed from the project, `Directory.Packages.props`, `dependabot.yml` and `THIRD-PARTY-NOTICES.md`. The notices now say tests use `Assert` only and that FluentAssertions 8+ should not come back (Xceed licence). |
| Contacts.cs | The insurance "link collection" comments now say what the household contacts are for today. The seeded description strings (demo data) are unchanged. |

## Implementation plan

**Phase 1: quick wins** (S effort, safe, one PR)

1. D6: delete the two `.csproj` items for missing files.
2. P1, P3, P4: remove the unused packages, then build.
3. D1, D3, D4 (except `ExchangeRateIds`): delete the dead members.
4. O1–O5: clean up comments and templates.
5. O2: fix the `MigrationDriftException` remarks and drop `contextName`.
6. M2: align `.editorconfig` with CLAUDE.md.
7. M1: add `EnableAnnotations()` after confirming the current `swagger.json`.

**Phase 2: guard rails**

1. O6, then M3: fix the stale doc references and enforce them in the build.
2. M6: move the image fixtures to TestShared.
3. U2: shared quick-create base.
4. P3: rewrite the ~30 AwesomeAssertions calls in `Odyssey.Core.Tests/DemoDataSetTests.cs` with xUnit `Assert`, then remove `AwesomeAssertions` from `Odyssey.Core.Tests.csproj` and `Directory.Packages.props`. It is the only file using it; every other test uses `Assert`.
5. The insurance comments in `Odyssey.TestData/Catalog/Contacts.cs`.

**Phase 3: refactors and decisions**

1. U1: shared contract-service test harness.
2. D2, D5, D7, P2, M7: decide each one, then act.
3. M4, M5: policy decisions.
4. File splits, starting with `Settings.razor.cs` and `CalendarIcsService.cs`.

## Checked and already clean

- No TODO/FIXME/HACK, no commented-out code, no `NoWarn`.
- The dead-member and unused-variable analyzers (IDE0051/0052, CS0168/0169/0219/0414) report 0.
- Every `#pragma` has a reason.
- No `s_` prefixes and no camelCase statics.
- Every DI registration is consumed. Every JS export is called and every `wwwroot` asset is referenced, apart from D7. Every TestShared type is used.
- The mirrored Context/Dtos enums are deliberate (`EnumMirrorParityTests`).
- No leftover `Email__*`/`EMAIL_*` config keys.
