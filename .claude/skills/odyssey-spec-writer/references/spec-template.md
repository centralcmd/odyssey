# Spec Template Reference

This file defines what goes in each section of an Odyssey feature specification.
It is used by the `odyssey-spec-writer` skill. Read it when writing or reviewing a spec.

---

## Document Header

```markdown
# {{ Feature Name }} (Draft v{{ N }})
```

---

## 1. Overview

**Purpose:** 2–4 sentence summary of the feature. Must answer:
- What is being built?
- Why (user value)?

**Subsection: Primary user value**
Use a bullet list of 2–4 concrete user-facing benefits.

**Example:**
```markdown
# Account File AI Transaction Import (Draft v1)

## 1. Overview

This document proposes a first-draft feature specification for an **Analyze File** workflow
on account-attached files. The feature adds a dedicated AI-analysis action (magic-wand/sparkles
icon) next to existing file actions (download, delete, view), then uses a Claude-powered
extraction pipeline to parse supported files (CSV, PDF statements) into normalized transactions
that can be reviewed and imported into the database.

### Primary user value
- Reduce manual transaction entry from statements and exports.
- Allow mixed file formats from banks/credit cards.
- Keep users in control by requiring review before final import.
```

---

## 2. Goals and Non-Goals

**Purpose:** Define scope boundaries explicitly. Prevents scope creep and clarifies MVP.

**Goals:** Numbered list. Each item = one deliverable or capability.

**Non-Goals (v1):** Numbered list. Each item = something explicitly deferred. Always include a version qualifier like `(v1)`.

---

## 3. User Experience

**Purpose:** Describe the UX flow from the user's perspective. Skip for purely backend features.

**Subsections:**
- **Entry points** — where in the UI this feature appears
- **States in UI** — numbered list of all UI states the user can encounter
- **User flow** — numbered step-by-step walkthrough of the happy path

Keep UI state names consistent with what developers will use in code (PascalCase is fine).

---

## 4. Supported File Types and Detection

**Purpose:** Enumerate supported formats and how the system identifies them.

Only include this section if the feature handles file uploads or processing.

**Subsections:**
- **Allowed MIME/extensions (v1)** — table or bullet list of MIME type + extension pairs
- **Detection strategy** — numbered steps describing how the backend validates file type

---

## 5. Architecture Proposal

**Purpose:** High-level system design. No implementation details, just components and data flow.

**Subsections:**
- **High-level components** — numbered list of logical components with 1-line descriptions
- **Processing pipeline** — numbered ordered steps of the end-to-end flow

Avoid class names. Use logical names like "Claude Analysis Service", "Import Service".

---

## 6. Data Model Additions

**Purpose:** Define new database entities and fields needed for the feature.

**Format:** One `###` subsection per new entity. List fields as bullet points with:
- Field name
- Type annotation or FK relationship in parentheses
- Optional: brief note if non-obvious

Use `> Note:` blockquotes for design decisions or deferred items.

**Example:**
```markdown
### `FileAnalysisJob`
- `Id`
- `FileId` (FK)
- `Status` (Queued, Running, Completed, Failed, Cancelled)
- `AnalyzerProvider` (required enum; `0=None`, `1=Claude`)

> Note: MVP does not include a separate ImportedTransactionLink table.
```

---

## 7. API Endpoints

**Purpose:** Document the HTTP interface.

**Format:** Numbered list. Each entry must include:
- HTTP method + path
- One-line description
- Response code on success

**Example:**
```markdown
1. `POST /api/accounts/{accountId}/files/{fileId}/analyze`
   - Starts job.
   - Returns `202 Accepted` + `analysisJobId`.
```

---

## 8. LLM Prompt/Output Contract

**Purpose:** Define how the system communicates with an LLM provider (e.g. Claude).

Only include if the feature uses an LLM. Skip otherwise with `> Not applicable for this feature.`

**Subsections:**
- **Principles** — bullet list of prompt/output design rules
- **Output schema** — JSON code block of the expected response structure with field types
- **Guardrails** — bullet list of error handling and retry rules

---

## 9. Validation and Mapping Rules

**Purpose:** Business rules for data validation and field mapping.

**Subsections:**
- **Validation rules** — bullet list; include required fields, range checks, fallback rules
- **Mapping to internal model** — bullet list describing how extracted data maps to existing domain entities

---

## 10. Security, Privacy, and Compliance Considerations

**Purpose:** Force explicit thinking about data exposure and access control.

**Format:** Numbered list. Must cover at minimum:
1. User consent for any third-party data sharing
2. Encryption at rest
3. Sensitive data redaction in logs
4. Tenant/user isolation
5. Audit trail requirements

---

## 11. Error Handling and Fallback Behavior

**Purpose:** Define how the system behaves when things go wrong.

**Subsections:**
- **Expected failure classes** — bullet list of named failure modes
- **UX behavior** — bullet list of user-facing responses per failure class (or general fallback guidance)

---

## 12. Performance Targets

**Purpose:** Set measurable expectations. Prevents "it's slow" being a surprise post-launch.

**Format:** Bullet list. Each item: scenario + target metric + percentile.

**Example:**
```markdown
- Small CSV (<2 MB): completion under 10 seconds (P50).
- Medium PDFs (2–10 MB): completion under 60 seconds (P50).
- Hard timeout per job: 3–5 minutes configurable.
```

---

## 13. Rollout Plan

**Purpose:** Phase the delivery.

**Subsection: Phase 1 (MVP)**
Bullet list of what is included in the initial release. Keep it tight — this is the commit.

---

## 14. Feature Toggle and Provider Configuration

**Purpose:** Ensure the feature can be disabled at runtime and is configurable without code changes.

**Required content:**
- List of `appsettings.json` keys with types (use dot-notation: `FeatureX:Enabled`)
- Behavior when `Enabled=false`: return `503 Service Unavailable` with a stable error code
- Note on SDK / HTTP client approach if third-party APIs are involved

**Example config keys:**
```markdown
- `FileAnalysis:Enabled` (bool)
- `FileAnalysis:Provider` (e.g. `Claude`)
- `FileAnalysis:ApiKey`
- `FileAnalysis:TimeoutSeconds`
```

---

## 15. Database Migration Approach

**Purpose:** Ensure schema changes are reproducible and tracked.

**Required content:**
- Confirm use of EF Core migrations (`dotnet ef migrations add`)
- Confirm migrations are committed with the feature branch
- Migration naming convention: descriptive, feature-scoped

---

## 16. Suggested Acceptance Criteria for MVP

**Purpose:** Testable done-criteria for the feature.

**Format:** Numbered list. Each criterion must be independently verifiable — no vague language like "works correctly" or "handles errors".

**Minimum criteria to include:**
1. Happy-path user flow works end-to-end
2. Async status updates work
3. At least one real fixture tested (file, data, etc.)
4. Edit/accept/reject flows work
5. Source traceability on imported records
6. Failed states show actionable error messages
7. Authorization enforced on all endpoints
8. Feature toggle enforced (503 when disabled)
