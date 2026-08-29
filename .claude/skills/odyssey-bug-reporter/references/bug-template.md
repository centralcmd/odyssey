# Bug Report Template Reference

Defines what goes in each section of an Odyssey bug report. Used by the `odyssey-bug-reporter` skill — read it when writing or reviewing a report. The structure mirrors the repo's `.github/ISSUE_TEMPLATE/bug_report.yml`, enriched with the **Root cause** and **Suggested fix** sections that make Odyssey bug reports actionable (see issues #157 and #158 for full real examples).

---

## Title

`bug: <concise symptom>` — lowercase after the prefix, present tense, under ~72 chars. Describe the *symptom*, not the fix.
- Good: `bug: new users get an "Unable to load preferences" error toast (404 treated as failure)`
- Bad: `bug: preferences broken`

---

## Context line (optional)

A single italic line giving provenance. Omit if not relevant.

```markdown
_Found while testing #155 (2FA)._
```

---

## Summary

1–3 sentences: what's wrong and the user-visible impact. Lead with the symptom; you may name the cause in a trailing clause.

```markdown
A newly-created user (with no saved preferences yet) gets a red error toast
"Unable to load preferences: … 404 (Not Found)" on first load, because the
preferences GET treats a 404 ("nothing saved yet") as a hard failure instead
of falling back to defaults.
```

---

## Metadata block

The required triage fields, compact, right after the summary:

```markdown
**Affected area:** Client / Frontend (Odyssey.Client — `Theme/UserPreferenceService`)
**Environment:** Docker Compose (`docker compose up --build`)
**Version:** `main` @ <commit sha or branch>
```

**Affected area** — pick exactly one from the official list:
API · Client / Frontend · Finance · Auth · File Storage · User Preferences · Database / EF Core Migrations · Docker / Infrastructure · Other / Unknown

---

## Reproduction

Numbered, deterministic steps from a known starting state. Include the literal command/URL where it helps.

```markdown
1. Create/confirm a fresh user that has never saved preferences.
2. Log in and load any page (e.g. `/account`).
3. A red snackbar appears: `Unable to load preferences: … 404 (Not Found)`.
```

---

## Expected

What *should* happen. One or two sentences.

```markdown
No error toast. A missing preference means "use defaults" — exactly how the
newer `PageStateService.LoadAsync` already treats a 404.
```

---

## Actual

What happens instead — the observable failure (status code, exception, blank screen, wrong data).

```markdown
Error toast on every fresh-user session until a preference is first saved.
Functionally harmless (defaults still apply) but noisy and confusing.
```

---

## Logs / console output (optional)

Relevant output in a fenced block: browser console, `docker compose logs api`, a stack trace. **Omit the whole section** if there's nothing useful.

```text
Failed to load resource: the server responded with a status of 404 (Not Found)
```

---

## Root cause  ← the differentiator

The precise cause, tied to code. Include:
- `path/File.cs:line` (or a small range).
- A fenced snippet of the offending code.
- One line on *why* it misbehaves.
- Optional clarifying contrast ("a sibling does this right at X").
- If unconfirmed, say so and mark `needs verification`.

```markdown
`Odyssey.Client/Theme/UserPreferenceService.cs` → `LoadUserPreferencesAsync()`:

​```csharp
var getResponse = await apiHttpClient.SendAsync(getRequest);
getResponse.EnsureSuccessStatusCode();   // ~line 82: throws on 404
...
catch (HttpRequestException ex)
{
    snackbar.Add($"Unable to load preferences: {ex.Message}", Severity.Error);  // ~line 97
}
​```

A fresh user has no `preferences-page` row → GET returns 404 →
`EnsureSuccessStatusCode()` throws → the catch surfaces it as an error toast.
The sibling `PageStateService.LoadAsync` already handles 404 → defaults.
```

---

## Suggested fix

A concrete, actionable direction derived from the root cause. Reference the file/method. Flag risk or `needs verification` if the fix could have side effects.

```markdown
Treat 404 (and arguably any non-2xx) as "no saved preferences → use
`DefaultUserPreferences`", mirroring `PageStateService.LoadAsync`
(`Odyssey.Client/Services/PageStateService.cs:45-47`). Reserve the toast for
genuine/unexpected errors.
```

---

## Additional context (optional)

Screenshots, related issues/PRs, regression range, frequency, workarounds. Omit if empty.

---

## Full worked examples

Two complete reports produced in this exact format:
- **#157** — `bug: hard refresh / direct navigation to /login, /register, etc. returns a blank 405 page` (Docker / Infrastructure — NGINX).
- **#158** — `bug: new users get an "Unable to load preferences" error toast (404 treated as failure)` (Client / Frontend).

Both lead with the symptom, pin the root cause to a `file:line` with a quoted snippet, and end with an actionable fix — that combination is the bar to clear.
