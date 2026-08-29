# Two-Factor Authentication (TOTP) — Implementation Notes

Implements issue #155. This note records **how** 2FA was built and, importantly, where the
implementation deliberately diverges from the original backend spec.

## Summary

ASP.NET Core Identity already ships a complete TOTP backend through
`MapIdentityApi<ApplicationUser>()` (registered in `Odyssey.Api/Program.cs`). Rather than
build the custom controller, pending-cookie, rate-limiter, and migration the spec described,
this change **wires the existing Blazor UI to Identity's built-in endpoints**. The decision
was made explicitly: *use Identity's own implementation wherever it already exists.*

The net result is **no new production backend code** — the work is frontend wiring, a QR
renderer, and a contract test suite that pins Identity's behaviour.

## What the spec asked for vs. what Identity already provides

| Spec endpoint | Built-in replacement (`MapIdentityApi`) |
|---|---|
| `GET /manage/2fa/status` | `POST /manage/2fa` with `{}` → returns `isTwoFactorEnabled`, `recoveryCodesLeft`, `sharedKey` |
| `POST /manage/2fa/setup` | `POST /manage/2fa` with `{}` → `sharedKey` (Identity generates a key if none exists) |
| `POST /manage/2fa/enable` | `POST /manage/2fa` with `{ "enable": true, "twoFactorCode": "…" }` → recovery codes |
| `POST /manage/2fa/disable` | `POST /manage/2fa` with `{ "enable": false }` |
| `POST /manage/2fa/reset-key` | `POST /manage/2fa` with `{ "resetSharedKey": true }` → new `sharedKey` (also disables 2FA) |
| `POST /manage/2fa/recovery-codes/regenerate` | `POST /manage/2fa` with `{ "resetRecoveryCodes": true }` |
| `POST /login` (202 + `requiresTwoFactor`) + `POST /login/2fa` | Built-in `POST /login`, which natively accepts `twoFactorCode` / `twoFactorRecoveryCode` and manages the pending state with Identity's own `Identity.TwoFactorUserId` cookie |

### Login challenge flow (built-in)

1. Client POSTs `{ email, password }` to `/login?useCookies=true`.
2. If 2FA is enabled, Identity refuses with **`401`** and ProblemDetails `detail: "RequiresTwoFactor"`,
   and sets the short-lived `Identity.TwoFactorUserId` cookie.
3. Client re-POSTs `{ email, password, twoFactorCode }` (the browser carries the pending cookie
   between the two calls). Identity validates and issues the full application cookie.

This is a two-request flow rather than the spec's `202` + separate `/login/2fa` endpoint, but it
is functionally identical and avoids the spec's `/login` route-shadowing approach — which does
not actually work (two endpoints on the same route+method raise `AmbiguousMatchException` at
request time rather than yielding precedence). The Codex spec review flagged this as blocking.

### Remember this device (opt-in, secure default)

The built-in `/login` ties `rememberClient` to `isPersistent`, which is `true` for our
`?useCookies=true` calls — so a TOTP sign-in **silently sets** Identity's `TwoFactorRememberMe`
cookie and later password-only logins from that browser would skip the challenge. That is not
acceptable as a default. The login page exposes a **"Remember this device" checkbox (default
off)**; unless the user ticks it, the page calls `POST /manage/2fa { forgetMachine: true }`
immediately after a successful TOTP sign-in to clear that cookie. So the challenge is required on
every login by default, and only trusted when the user explicitly opts in. (We can't set
`rememberClient` independently of session persistence on the built-in endpoint, so post-login
`forgetMachine` is the lever that keeps persistent sessions while making device-trust opt-in.)
Recovery-code logins are never remembered by Identity, so the checkbox is hidden on that path.

## Deliberate deviations from the spec

| Spec item | Decision | Rationale |
|---|---|---|
| Custom `TwoFactorController` (6 endpoints) | **Dropped** | Identity's `POST /manage/2fa` covers all six operations. |
| `POST /login/2fa` + `odyssey-2fa-pending` Data-Protection cookie | **Dropped** | Built-in `/login` + Identity's `TwoFactorUserId` cookie already implement this, battle-tested. |
| `ApplicationUser.TwoFactorEnabledAt` + `AddTwoFactorEnabledAt` migration | **Dropped** | Identity does not track an enabled-at timestamp, and the built-in enable path cannot be hooked to set one without forking it. The UI now shows status + remaining recovery codes without a precise enabled-at date. **No migration is needed for this feature.** |
| Custom sliding-window rate limiter returning `429` | **Dropped** | Identity's account **lockout** already throttles failed password/TOTP attempts on the login path (`lockoutOnFailure: true`; `TwoFactorAuthenticatorSignInAsync` increments the failed count and returns `LockedOut`). The `/manage/2fa` enable path is session-authenticated, so a bad code only affects the caller's own account. |
| `TwoFactor:Enabled` feature toggle returning `503` | **Dropped** | Not an Identity concept; gating the built-in endpoints would require exactly the custom wrapping this approach avoids. Candidate for a future change if an ops kill-switch is needed. |
| Confirmation code required for disable / reset / regenerate | **Relaxed to session auth** | The built-in endpoints authorize these via the active session cookie (consistent with how `manage/info` password change is gated). The UI keeps a "type DISABLE" guard on the disable action. |

## Frontend changes

- `Odyssey.Client/Auth/AuthApiClient.cs` — `LoginAsync` now returns a `LoginOutcome`
  (`Success` / `RequiresTwoFactor` / `LockedOut` / `Failed`) parsed from the `/login` response;
  added `GetTwoFactorStatusAsync`, `EnableTwoFactorAsync`, `DisableTwoFactorAsync`,
  `ResetTwoFactorKeyAsync`, `RegenerateRecoveryCodesAsync`, and `ForgetTwoFactorMachineAsync`
  over `POST /manage/2fa`.
- `Odyssey.Client/Models/AuthModels.cs` — `LoginRequest` gains `TwoFactorCode` /
  `TwoFactorRecoveryCode`.
- `Odyssey.Client/Pages/Account.razor[.cs]` — the existing 2FA wizard (previously a UI-only
  preview) is wired to the API; the placeholder QR is replaced with a real scannable QR
  (PNG data-URI via **QRCoder**) built from the `otpauth://` URI off the live shared key.
  Enabling always regenerates the recovery codes (`resetRecoveryCodes`) so the "save your codes"
  step is shown even when re-enabling after a reset/disable — Identity returns no codes when a
  set is still on file, which would otherwise finish setup without ever displaying fallbacks.
- `Odyssey.Client/Pages/Auth/Login.razor` — adds a two-step verification phase shown when the
  API reports `RequiresTwoFactor`, with a recovery-code toggle and an opt-in "Remember this
  device" checkbox (default off → `forgetMachine` after sign-in; see above).

## Tests

`Odyssey.Api.Tests/TwoFactorAuthenticationTests.cs` pins Identity's behaviour so the
client's assumptions can't silently break:

- `/manage/2fa` rejects unauthenticated callers (`401`) — acceptance #11.
- Full lifecycle via `UserManager`: a standard RFC-6238 TOTP code (computed by the test) verifies
  against Identity's authenticator provider, recovery codes are single-use (acceptance #6),
  regeneration invalidates the old set, and key reset invalidates old codes (acceptance #9).
- HTTP round-trip: password-only login on a 2FA account is refused with `RequiresTwoFactor`,
  then a second request carrying the TOTP code completes sign-in (acceptance #1, #4, #5).
- Remember-device: a persistent TOTP sign-in remembers the browser (a later password-only login
  is accepted without a code), and `POST /manage/2fa { forgetMachine: true }` restores the
  challenge — the behaviour the login page's opt-in checkbox manages.
- Re-enable: with recovery codes still on file, a plain enable returns none, but enable with
  `resetRecoveryCodes: true` always returns a fresh set — so the wizard never finishes without
  showing fallback codes.
