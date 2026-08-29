repo: centralcmd/odyssey
branch: main
path: Odyssey.Client

## Last sync

date: 2026-08-25T08:15:20Z

### Updated in this project

- Read `OdsSettingRow`, `Settings.razor`/`.razor.css` and `FileAnalysisConsentPanel` to ground the runtime-settings design work in the shipped markup.
- System settings kit page extended to the full 42-row / 12-section catalogue with the text, decimal, warning and ceiling row patterns.
- New DS specimens for the consent gate's three disclosure states and the effective upload-cap messaging.
- `FileUpload` no longer hardcodes "up to 25 MB" in its default hint — the size clause is composed from `maxMegabytes`.

## Screen map

| This project | Built from / compared against |
|---|---|
| `ui_kits/web/SystemSettings.jsx` + `system-settings-data.js` | `Odyssey.Client/Pages/Settings.razor`, `Settings.razor.cs`, `Settings.razor.css` |
| `components/SecretSettingField.jsx` · `secretsettingfield.html` | **No shipped counterpart** — designed ahead of the `OdsSecretSettingRow` the encrypted-secret-store issue assumes. |
| `components/SecretClearDialog.jsx` · `secretcleardialog.html` | **No shipped counterpart** — the Clear-on-`Unreadable` confirmation, both `Kind` copy variants. |
| `components/SettingRow.jsx` · `settingrow.html` | `Odyssey.Client/Components/OdsSettingRow.razor`, `OdsSettingRow.razor.css` |
| `components/TextInputField.jsx` · `textinputfield.html` | `Odyssey.Client/Components/OdsFieldShell.razor`, `OdsNumberField.razor`, `OdsField.razor` |
| `components/ErrorSummary.jsx` | `Odyssey.Client/Pages/Settings.razor` (the `HasErrors` / disabled-Save path) |
| `components/consentgate.html` | `Odyssey.Client/Pages/Finance/FileAnalysisConsentPanel.razor` + `.razor.css`, `Odyssey.Client/Models/FileAnalysisConsent.cs` |
| `components/uploadcap.html` · `components/FileUpload.jsx` | the seven client upload-cap constant sites named in the Wave 4 spec |
| `ui_kits/web/admin.css` | `Odyssey.Client/Pages/Settings.razor.css` |

## Notes

The DS is the source of truth for this direction, so two decisions here deliberately
differ from what `main` currently ships and are meant to be adopted, not reconciled away:

- `.odc-setting-footer` is a tinted well flush to the card edges, not `padding: 0 16px 16px 70px`.
- The unsaved-change dot sits with the row **title**, not in the control column, so it survives a
  footer-slot control (`Settings.razor` keeps it in `.ss-right` today).

### Ahead of `main`: the encrypted secret settings

`SecretSettingField`, `SecretClearDialog` and the five secret rows on the system-settings kit page
have no counterpart in the repository — they were designed from the migration spec, not read from
shipped markup. Points for whoever implements them:

- Secrets are **not** part of the settings page's Save. Each row commits on its own request and
  announces its own outcome; the page's dirty count and disabled-Save path ignore them.
- **Each secret lives in its subject card, not in a Credentials group of its own** — the API key in
  *File analysis* beside the destination it is sent to, the SMTP username/password/hash key in
  *Email*, the pseudonymisation secret in *Data* beside the export carrying the same records.
  `Type == Secret` is what marks a row, so grouping stays a presentation choice.
- `Unreadable` surfaces as the page-header **signal** rollup (coral, `defaultOpen`), the same gesture
  Accounts, Insurance, Contracts and Tax statements use — deliberately *not* merged into the
  Save-blocking Problems rollup, since neither the cause nor the fix is a Save. Each row names the
  card it sits in, because the rows are scattered by subject.
- The spec requires `FileAnalysis:BaseUrl` to stay deploy-time configuration and be unreachable from
  `/api/system-settings`, but `fileAnalysisBaseUrl` is a shipped, admin-editable row on that page.
  The kit still renders it; its helper copy no longer claims the API key is deploy-time config.
  Unresolved — see the design note raised with the spec.
