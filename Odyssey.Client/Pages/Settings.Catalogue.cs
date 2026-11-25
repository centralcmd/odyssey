using MudBlazor;
using Odyssey.Dtos.Application;
using Odyssey.Client.Components;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages;

public partial class Settings
{
    // ── Settings catalog (single source of truth — drives render AND search) ──────────────────
    //
    // Count caps → system-settings.update; SIZE caps → system-settings.security.update (issue #343
    // §10 item 4) — the availability knob (size) sits behind the stricter claim. A group carries
    // RoundTrip when it has both an export- and an import-count cap that must satisfy export ≤
    // import (§9) — all four groups do now that Tasks gained an export row cap as a follow-up
    // (previously "Non-Goal 2": no export cap at all).
    internal enum SettingClaim { None, Security, Count }
    // Date and Action arrived with the exchange-rate sync card. Action is a button that
    // acts at once or navigates, and is not part of Save.
    internal enum SettingControl { Toggle, Number, Size, Capacity, Text, Decimal, Percent, Date, Action, LongText }

    /// <summary>The one non-saving control kind — never dirty, never validated, never written.</summary>
    internal static bool IsTileOnly(SettingControl control) =>
        control is SettingControl.Action;

    /// <summary>
    /// One row. <see cref="Load"/> and <see cref="Write"/> are the catalogue's half of the same
    /// single-declaration argument the server registry makes (issue #421 Wave 0b): before them, every
    /// key was named in THREE places with no compile-time link between them — here, in
    /// <c>ApplyLoaded</c>, and in the <c>SystemSettingsUpdate</c> initialiser in <c>Save</c>. A key
    /// missing from <c>ApplyLoaded</c> threw <c>KeyNotFoundException</c> from a render path with no
    /// <c>ErrorBoundary</c> to catch it; a key missing from <c>Save</c> failed silently — the row
    /// edited, flagged dirty, saved green, and changed nothing.
    ///
    /// <para>
    /// Explicit delegates, never reflection over property names, for the same reason the server side
    /// uses them: a renamed DTO property must break the build, not quietly lose its claim gate.
    /// </para>
    /// </summary>
    internal sealed record SettingItem(
        string Key, string Icon, string Title, string Description, SettingClaim Claim,
        SettingControl Control = SettingControl.Toggle, int Min = 0, int Max = 0,
        // The SystemSettingsUpdate property this row writes. Stated rather than derived from Key,
        // because the five original #349 rows use display-shaped keys ("require2fa") that do not
        // camel-case back to their property name. It is the join the server's `errors` dictionary
        // needs, and a guard test asserts every writable property is claimed by exactly one row.
        string? Field = null,
        Action<Settings, SystemSettingsDto>? Load = null,
        Action<Settings, SystemSettingsUpdate>? Write = null,
        decimal DecimalMin = 0m, decimal DecimalMax = 0m,
        Func<SystemSettingsDto, int>? MaxFrom = null,
        // The server-published FLOOR, mirroring MaxFrom. Needed by issue #434's one raise-only key:
        // without it a published floor has no route to the control at all — the controls bind
        // Min="@item.Min" as a static int and ErrorFor compares against item.Min, so the row would
        // offer 1 and then be rejected with a 400 the field never warned about.
        Func<SystemSettingsDto, int>? MinFrom = null,
        // A static unit pinned inside the input's trailing edge — "days", "min", "MB", "%". Not helper
        // text: the helper slot is where the error message goes, so a unit written there disappears
        // exactly when the value is wrong and the reader most needs to know what they are typing
        // (Odyssey Design System · NumberField `unit`). SettingControl.Size supplies "MB" itself.
        string? Unit = null,
        // Native inputmode on a Text row — "url" / "email" — so a phone keyboard offers the right
        // keys. A hint only: the value is still validated by the server, never by the keyboard.
        string? InputMode = null,
        // Placeholder on a Text row, for a value whose SHAPE is not obvious from the label. It shows
        // the form, never a value the reader might take for the current one.
        string? Placeholder = null,
        // The SHARED shape rule for a Text row, from Odyssey.Dtos — the same delegate the server's
        // descriptor validates with, not a re-implementation. Returns an error message, or null when
        // the value is acceptable. Never called for an empty value: an AllowEmpty row is legal empty,
        // and every other row reports "Enter a value" first.
        //
        // Without it the row round-trips to the server to learn its value is malformed, which the
        // page already avoids for every bound it can see. It is only possible at all because the
        // rules live in Odyssey.Dtos, which has zero project references and is reachable from WASM.
        Func<string, string?>? Rule = null,
        // A Text row whose EMPTY value is legal and MEANS something, rather than an unfilled field
        // (issue #8). Mirrors StringSetting.AllowEmpty on the server, which is the authority — this
        // flag only stops ErrorFor reporting "Enter a value" for a state the API accepts. Without it
        // the two mail rows could never be cleared: an empty host would be a client-side error that
        // disables Save, so configuring mail would be a one-way door.
        bool AllowEmpty = false,
        // A Text row whose value is chosen from a list served at read time rather than typed (issue
        // #348's household weather default). Still a Text row for dirty-tracking, validation and save —
        // only the control differs. The options are the server's; the server re-checks the choice on
        // save, so a list gone stale while the page was open is refused there, not trusted here.
        Func<Settings, IReadOnlyList<OdsOption>>? OptionsFrom = null,
        // A TIGHTEN-ONLY row (the design system's `ceiling`): its value can be lowered but never raised,
        // because a request-DTO annotation or a transport limit enforces the same number earlier in the
        // pipeline. Drives the "lower only" marker and the range sentence. Stated rather than inferred
        // from MaxFrom: a published bound is not by itself a one-way door — the aggregate calendar caps
        // and the metadata read size publish theirs only so the control can bound itself.
        bool LowerOnly = false,
        // A client-authored advisory the row ALWAYS carries (the design system's `adviseAlways`, issue
        // #473 §3.6): the News allow-list's "never list internal service names". It sits below every
        // server-authored message, so a projection fault on the row still outranks it.
        string? StandingAdvisory = null,
        // A row that is NOT RENDERED for a caller who cannot edit it, rather than shown read-only (issue
        // #473 §3.6: the private-host allow-list). Its Write still sends null for that caller, as every
        // row the caller cannot edit does.
        bool HideWithoutClaim = false);

    internal sealed record RoundTripPair(string ExportKey, string ImportKey);

    /// <param name="Panel">A read-only panel the card opens with, above its fields (the Storage card's health panel).</param>
    internal sealed record SettingSection(
        string Group, string Icon, IReadOnlyList<SettingItem> Items, RoundTripPair? RoundTrip = null, string? Panel = null);

    internal static readonly IReadOnlyList<SettingSection> Sections =
    [
        new("Security", Icons.Material.Filled.Shield,
        [
            new("require2fa", "verified_user", "Require two-factor authentication",
                "Every user must set up an authenticator app to sign in. Stored only — not enforced yet.",
                SettingClaim.Security,
                Field: nameof(SystemSettingsUpdate.RequireTwoFactor),
                Load: (p, dto) => p.SetBoolLoaded("require2fa", dto.RequireTwoFactor), Write: (p, req) => req.RequireTwoFactor = p.BoolRequest("require2fa")),
            new("registration-approval", "how_to_reg", "Require admin approval for new registrations",
                "New sign-ups stay disabled until an administrator approves the account.",
                SettingClaim.Security,
                Field: nameof(SystemSettingsUpdate.RegistrationRequireAdminApproval),
                Load: (p, dto) => p.SetBoolLoaded("registration-approval", dto.RegistrationRequireAdminApproval), Write: (p, req) => req.RegistrationRequireAdminApproval = p.BoolRequest("registration-approval")),
            new("email-confirmation", "mark_email_read", "Require email confirmation before sign-in",
                "Users must confirm their email address before their first sign-in is allowed.",
                SettingClaim.Security,
                Field: nameof(SystemSettingsUpdate.EmailRequireConfirmation),
                Load: (p, dto) => p.SetBoolLoaded("email-confirmation", dto.EmailRequireConfirmation), Write: (p, req) => req.EmailRequireConfirmation = p.BoolRequest("email-confirmation")),
        ]),
        // ── AI file analysis (issue #421 Wave 1) ─────────────────────────────────────────────────
        // Row icons are ligatures resolved against the SELF-HOSTED, frozen classic Material Icons
        // font in wwwroot/fonts — not Material Symbols and not the live Google Fonts set. A name the
        // snapshot lacks does not fall back to nothing: the font ligates the longest prefix it does
        // know and renders the rest as literal text, so "event_upcoming" produced an `event` glyph
        // followed by "_upcoming" — two glyphs in one tile. Use a ligature this client already
        // renders somewhere; that is the only offline proof available that it exists in the snapshot.
        // ONE section, though its rows split across both write claims: SettingSection carries
        // per-ITEM claims, and two same-named sections would emit duplicate group ids and two
        // identical <h2>s. Group icon is an Icons.Material.Filled constant, not a ligature — the
        // group slot renders through MudIcon, where a ligature shows nothing; row icons ARE ligatures.
        new("File analysis", Icons.Material.Filled.SmartToy,
        [
            // ── issue #439. Enabled -> Model -> Provider base URL sit at the TOP of the section: the
            // switch and the destination frame every disclosure row below them. All three take the
            // SECURITY claim, so all three are audited by the derived rule — the switch authorises
            // transferring personal data to a third party, the model is stamped on every job, and the
            // base URL is where the document and the configured API key actually go.
            new("fileAnalysisEnabled", "verified_user", "AI document analysis",
                "When off, no document is sent for analysis and every analysis endpoint answers 503. "
                + "Turning it on does not by itself transfer anything: each analysis still requires the "
                + "user's per-document consent. Read live on every request rather than from the settings "
                + "cache, so turning it off stops the next transfer instead of the next one after a "
                + "30-second window. Every change is written to the audit log.",
                SettingClaim.Security,
                Field: nameof(SystemSettingsUpdate.FileAnalysisEnabled),
                Load: (p, dto) => p.SetBoolLoaded("fileAnalysisEnabled", dto.FileAnalysisEnabled),
                Write: (p, req) => req.FileAnalysisEnabled = p.BoolRequest("fileAnalysisEnabled")),
            new("fileAnalysisModel", "badge", "Model",
                "The model each analysis is sent to, and the model recorded against it. Analyses already "
                + "completed keep the model they ran under. A stored value that cannot be used makes "
                + "analysis refuse rather than fall back to the shipped default — a job stamp must never "
                + "name a model that did not run.",
                SettingClaim.Security, SettingControl.Text, Max: 128,
                Field: nameof(SystemSettingsUpdate.FileAnalysisModel),
                Load: (p, dto) => p.SetTextLoaded("fileAnalysisModel", dto.FileAnalysisModel),
                Write: (p, req) => req.FileAnalysisModel = p.TextRequest("fileAnalysisModel")),
            new("fileAnalysisBaseUrl", "send", "Provider base URL",
                "Where analysis requests are sent. Must be an absolute https:// address with no path — "
                + "the provider appends /v1/messages itself — and the stored API key is sent to this "
                + "host, so change it only to a host you control or trust. Redirects are not followed, so "
                + "the API key and the document reach only this host. The host is recorded on each job — "
                + "GDPR Art. 30(1)(e). Every change is written to the audit log.",
                SettingClaim.Security, SettingControl.Text, Max: 256,
                Field: nameof(SystemSettingsUpdate.FileAnalysisBaseUrl),
                Load: (p, dto) => p.SetTextLoaded("fileAnalysisBaseUrl", dto.FileAnalysisBaseUrl),
                Write: (p, req) => req.FileAnalysisBaseUrl = p.TextRequest("fileAnalysisBaseUrl"),
                InputMode: "url", Placeholder: "https://…"),
            new("fileAnalysisProcessor", "corporate_fare", "Data processor",
                "The organisation that receives uploaded documents. Named at the consent gate and in the sentence "
                + "each user affirms. Recorded on each analysis job — GDPR Art. 13(1)(e). Odyssey can only "
                + "advise on the name, not verify it. Every change is written to the audit log.",
                SettingClaim.Security, SettingControl.Text, Max: 128,
                Field: nameof(SystemSettingsUpdate.FileAnalysisProcessor),
                Load: (p, dto) => p.SetTextLoaded("fileAnalysisProcessor", dto.FileAnalysisProcessor),
                Write: (p, req) => req.FileAnalysisProcessor = p.TextRequest("fileAnalysisProcessor")),
            new("fileAnalysisProcessorRegion", "public", "Processor region",
                "Where processing happens. Nothing the server can see reveals the real region, so this value is "
                + "trusted as entered — a wrong one is not detectable. Shown at the consent gate and recorded "
                + "on each job — GDPR Chapter V. Every change is written to the audit log.",
                SettingClaim.Security, SettingControl.Text, Max: 128,
                Field: nameof(SystemSettingsUpdate.FileAnalysisProcessorRegion),
                Load: (p, dto) => p.SetTextLoaded("fileAnalysisProcessorRegion", dto.FileAnalysisProcessorRegion),
                Write: (p, req) => req.FileAnalysisProcessorRegion = p.TextRequest("fileAnalysisProcessorRegion")),
            new("fileAnalysisLawfulBasis", "gavel", "Lawful basis",
                "Recorded on every job and shown under the affirmation. Change it when the legal analysis "
                + "changes, not to relabel past jobs. Shown at the consent gate under the sentence each user "
                + "affirms. Every change is written to the audit log.",
                SettingClaim.Security, SettingControl.Text, Max: 128,
                Field: nameof(SystemSettingsUpdate.FileAnalysisLawfulBasis),
                Load: (p, dto) => p.SetTextLoaded("fileAnalysisLawfulBasis", dto.FileAnalysisLawfulBasis),
                Write: (p, req) => req.FileAnalysisLawfulBasis = p.TextRequest("fileAnalysisLawfulBasis")),
            new("fileAnalysisPrivacyNoticeUrl", "link", "Privacy notice URL",
                "Linked from the consent gate. Absolute https:// only — the value is rendered as a live link. "
                + "Linked at the consent gate — GDPR Art. 13(1)(e). Odyssey checks the URL. Every change is "
                + "written to the audit log.",
                SettingClaim.Security, SettingControl.Text, Max: 256,
                Field: nameof(SystemSettingsUpdate.FileAnalysisPrivacyNoticeUrl),
                Load: (p, dto) => p.SetTextLoaded("fileAnalysisPrivacyNoticeUrl", dto.FileAnalysisPrivacyNoticeUrl),
                Write: (p, req) => req.FileAnalysisPrivacyNoticeUrl = p.TextRequest("fileAnalysisPrivacyNoticeUrl"),
                InputMode: "url", Placeholder: "https://…"),
            new("fileAnalysisMaxFutureTransactionDays", "update", "Future-dated transaction window",
                "How far ahead of today an extracted transaction date may fall before it is rejected as a misread.",
                SettingClaim.Count, SettingControl.Number, Min: 1, Max: 3650,
                Field: nameof(SystemSettingsUpdate.FileAnalysisMaxFutureTransactionDays),
                Load: (p, dto) => p.SetIntLoaded("fileAnalysisMaxFutureTransactionDays", dto.FileAnalysisMaxFutureTransactionDays),
                Write: (p, req) => req.FileAnalysisMaxFutureTransactionDays = p.IntRequest("fileAnalysisMaxFutureTransactionDays"),
                Unit: "days"),
            new("fileAnalysisMatchAutoLinkThreshold", "join_inner", "Auto-link confidence threshold",
                "At or above this, an extracted match is linked automatically; below it, suggested for review. "
                + "Higher links less. Each job records the threshold it ran under.",
                // Percent, not Decimal: the value is STORED as a 0.0-1.0 fraction but entered as a
                // whole percent (Odyssey Design System · NumberField). "0.62" gives the reader no clue
                // it is a proportion, and a two-decimal stepper is a fiddly target; the page scales at
                // the control boundary and the stored contract is untouched.
                SettingClaim.Count, SettingControl.Percent,
                Field: nameof(SystemSettingsUpdate.FileAnalysisMatchAutoLinkThreshold),
                Load: (p, dto) => p.SetDecimalLoaded("fileAnalysisMatchAutoLinkThreshold", dto.FileAnalysisMatchAutoLinkThreshold),
                Write: (p, req) => req.FileAnalysisMatchAutoLinkThreshold = p.DecimalRequest("fileAnalysisMatchAutoLinkThreshold"),
                DecimalMin: 0.01m, DecimalMax: 1.00m, Unit: "%"),
            // ── issue #434 keys 1-3. MaxTokens takes the SECURITY claim: it is a direct third-party
            // spend lever, and AuditChanges is derived from that claim, so it is the only way a change
            // to it gets an audit entry. The Photos group below mixes claims for the same reason,
            // which correctly fires the existing partial-permissions note.
            new("fileAnalysisMaxTokens", "format_list_numbered", "Maximum response tokens",
                "The output ceiling on each extraction call. A lower value truncates long statements; a "
                + "higher one costs more per document. Each job records the value it ran under, so a "
                + "truncated extraction stays distinguishable from a model failure.",
                SettingClaim.Security, SettingControl.Number, Min: 1024, Max: 64000,
                Field: nameof(SystemSettingsUpdate.FileAnalysisMaxTokens),
                Load: (p, dto) => p.SetIntLoaded("fileAnalysisMaxTokens", dto.FileAnalysisMaxTokens),
                Write: (p, req) => req.FileAnalysisMaxTokens = p.IntRequest("fileAnalysisMaxTokens")),
            new("fileAnalysisMatchTimeoutSeconds", "history_toggle_off", "Match timeout",
                "How long the match call may run before the job is marked failed and falls back to manual "
                + "review.",
                SettingClaim.Count, SettingControl.Number, Min: 5, Max: 600,
                Field: nameof(SystemSettingsUpdate.FileAnalysisMatchTimeoutSeconds),
                Load: (p, dto) => p.SetIntLoaded("fileAnalysisMatchTimeoutSeconds", dto.FileAnalysisMatchTimeoutSeconds),
                Write: (p, req) => req.FileAnalysisMatchTimeoutSeconds = p.IntRequest("fileAnalysisMatchTimeoutSeconds"),
                Unit: "sec"),
            // Administrator guidance (issues #433, #434). Multi-line LongText rows: an OdsNoteField counted
            // against the NORMALISED length the server stores, with no native maxlength — an over-limit
            // paste is kept and explained, never truncated. Validated by the shared PromptAdditionRule.
            new("fileAnalysisExtractionPromptAddition", "edit_note", "Extraction guidance",
                "Optional. Extra instructions added to every statement extraction, after Odyssey's own rules — "
                + "for example how your bank formats dates or which lines aren't transactions. Sent to "
                + ProcessorToken + " with each analysis. Don't include personal data or passwords. Each analysis "
                + "records the guidance it ran under; the analysis log shows its fingerprint, never the text.",
                SettingClaim.Count, SettingControl.LongText, Max: PromptAdditionRule.MaxLength,
                Field: nameof(SystemSettingsUpdate.FileAnalysisExtractionPromptAddition),
                Load: (p, dto) => p.SetTextLoaded("fileAnalysisExtractionPromptAddition", dto.FileAnalysisExtractionPromptAddition),
                Write: (p, req) => req.FileAnalysisExtractionPromptAddition = p.TextRequest("fileAnalysisExtractionPromptAddition"),
                Placeholder: PromptPlaceholder,
                AllowEmpty: true,
                Rule: PromptAdditionRule.Validate),
            new("fileAnalysisMatchPromptAddition", "rule", "Matching guidance",
                "Optional. Extra instructions for matching transactions to contacts and tags — for example local "
                + "prefixes such as VIPPS* or BankAxept. Sent to " + ProcessorToken + " with each match. Don't "
                + "include personal data or passwords. Each match records the guidance it ran under; the analysis "
                + "log shows its fingerprint, never the text.",
                SettingClaim.Count, SettingControl.LongText, Max: PromptAdditionRule.MaxLength,
                Field: nameof(SystemSettingsUpdate.FileAnalysisMatchPromptAddition),
                Load: (p, dto) => p.SetTextLoaded("fileAnalysisMatchPromptAddition", dto.FileAnalysisMatchPromptAddition),
                Write: (p, req) => req.FileAnalysisMatchPromptAddition = p.TextRequest("fileAnalysisMatchPromptAddition"),
                Placeholder: PromptPlaceholder,
                AllowEmpty: true,
                Rule: PromptAdditionRule.Validate),
        ]),
        // ── Transactional email (issue #421 Wave 2, extended by issue #8) ─────────────────────────
        // Row icons are ligatures the client already renders elsewhere — see the note above the File
        // analysis section for why an unproven name is not a safe guess here.
        //
        // The TRANSPORT rows come first, matching the design system's Email group: they frame
        // everything below them — whether mail leaves at all, where it goes, and whether the
        // credential beneath them travels encrypted. Issue #421 Non-Goal 2 kept them out of this store
        // entirely (a writable host harvests the relay credential, because the sender connects and
        // THEN authenticates); issue #8 moved them and closed that structurally instead — changing the
        // host, or turning STARTTLS off, clears the stored credential in the same transaction. Save()
        // gates on a confirmation before either lands.
        new("Email", Icons.Material.Filled.MarkEmailRead,
        [
            new("emailSmtpHost", "send", "SMTP host",
                "The relay every transactional mail is sent through. A hostname or IP literal only — no scheme, port, path or credentials. Empty means mail is not configured: every send is logged and skipped. "
                + "The client connects before it authenticates, so whatever host is set here receives the "
                + "stored SMTP credential. Changing it clears that credential in the same save, and the "
                + "change binds on the next send with no restart.",
                SettingClaim.Security, SettingControl.Text, Max: EmailSmtpHostRule.MaxLength,
                Field: nameof(SystemSettingsUpdate.EmailSmtpHost),
                Load: (p, dto) => p.SetTextLoaded("emailSmtpHost", dto.EmailSmtpHost),
                Write: (p, req) => req.EmailSmtpHost = p.TextRequest("emailSmtpHost"),
                InputMode: "url",
                Placeholder: "smtp.example.net",
                // Empty is the "not configured" state and the only route back to it — see the flag.
                AllowEmpty: true,
                Rule: EmailSmtpHostRule.Validate),
            new("emailSmtpPort", "format_list_numbered", "SMTP port",
                "The port the relay is reached on. 587 for STARTTLS submission, 465 for implicit TLS, 25 for an "
                + "unauthenticated internal relay. Changing it, to any other port, clears the stored SMTP credential "
                + "in the same save: every port is a live listener, so a new port is a different endpoint the "
                + "credential was not entered for.",
                SettingClaim.Security, SettingControl.Number,
                // Named from the shared bound pair, never a literal: the same two constants drive the
                // server's [Range], the registry descriptor and the send path's read clamp.
                Min: SystemSettingsBounds.EmailSmtpPortMin, Max: SystemSettingsBounds.EmailSmtpPortMax,
                Field: nameof(SystemSettingsUpdate.EmailSmtpPort),
                Load: (p, dto) => p.SetIntLoaded("emailSmtpPort", dto.EmailSmtpPort),
                Write: (p, req) => req.EmailSmtpPort = p.IntRequest("emailSmtpPort")),
            new("emailUseStartTls", "lock", "Use STARTTLS",
                "Upgrade the connection to TLS after connecting — the right setting for port 587. Turn it off only for implicit TLS on 465, where the connection is encrypted from the start. "
                + "Turning it off clears the stored SMTP credential in the same save: a credential entered "
                + "for an encrypted transport must not be replayed over a cleartext one, where passive "
                + "network position alone is enough to read it.",
                SettingClaim.Security,
                Field: nameof(SystemSettingsUpdate.EmailUseStartTls),
                Load: (p, dto) => p.SetBoolLoaded("emailUseStartTls", dto.EmailUseStartTls),
                Write: (p, req) => req.EmailUseStartTls = p.BoolRequest("emailUseStartTls")),
            new("emailClientBaseUrl", "link", "Client base URL",
                "The public origin every confirmation and password-reset link is composed against. Absolute https:// with no query, fragment or credentials; http:// is accepted for loopback addresses so the dev stack keeps working. "
                + "Anyone who can change this receives password-reset tokens for any address they know, so "
                + "the value is audited by host and checked against the origin you are browsing from. Empty "
                + "means links cannot be composed and the reset mail carries a code to paste instead.",
                SettingClaim.Security, SettingControl.Text, Max: EmailClientBaseUrlRule.MaxLength,
                Field: nameof(SystemSettingsUpdate.EmailClientBaseUrl),
                Load: (p, dto) => p.SetTextLoaded("emailClientBaseUrl", dto.EmailClientBaseUrl),
                Write: (p, req) => req.EmailClientBaseUrl = p.TextRequest("emailClientBaseUrl"),
                InputMode: "url",
                Placeholder: "https://odyssey.example.net",
                AllowEmpty: true,
                Rule: EmailClientBaseUrlRule.Validate),
            new("emailFromAddress", "alternate_email", "From address",
                "The sender on every transactional mail. Must stay an address the relay is authorised to send "
                + "as, or SPF/DKIM will fail and mail will be dropped silently.",
                SettingClaim.Security, SettingControl.Text, Max: 256,
                Field: nameof(SystemSettingsUpdate.EmailFromAddress),
                Load: (p, dto) => p.SetTextLoaded("emailFromAddress", dto.EmailFromAddress),
                Write: (p, req) => req.EmailFromAddress = p.TextRequest("emailFromAddress"),
                InputMode: "email"),
            new("emailFromName", "badge", "From name",
                "The display name beside the sender address.",
                SettingClaim.Security, SettingControl.Text, Max: 128,
                Field: nameof(SystemSettingsUpdate.EmailFromName),
                Load: (p, dto) => p.SetTextLoaded("emailFromName", dto.EmailFromName),
                Write: (p, req) => req.EmailFromName = p.TextRequest("emailFromName")),
            new("emailPerRecipientLimit", "filter_list", "Messages per recipient",
                "How many mails one address may receive per window. Bounds password-reset flooding.",
                SettingClaim.Security, SettingControl.Number, Min: 1, Max: 1000,
                Field: nameof(SystemSettingsUpdate.EmailPerRecipientLimit),
                Load: (p, dto) => p.SetIntLoaded("emailPerRecipientLimit", dto.EmailPerRecipientLimit),
                Write: (p, req) => req.EmailPerRecipientLimit = p.IntRequest("emailPerRecipientLimit")),
            new("emailPerRecipientWindowMinutes", "timelapse", "Recipient window",
                "The window the per-recipient limit is counted over. A longer window is a tighter throttle.",
                SettingClaim.Security, SettingControl.Number, Min: 1, Max: 1440,
                Field: nameof(SystemSettingsUpdate.EmailPerRecipientWindowMinutes),
                Load: (p, dto) => p.SetIntLoaded("emailPerRecipientWindowMinutes", dto.EmailPerRecipientWindowMinutes),
                Write: (p, req) => req.EmailPerRecipientWindowMinutes = p.IntRequest("emailPerRecipientWindowMinutes"),
                Unit: "min"),
            // ── issue #434 key 14. RAISE-ONLY, and the first row on this page with a server-published
            // FLOOR — the reason SettingItem needed MinFrom to mirror MaxFrom. The static Min names the
            // same shared constant the server's [Range] minimum does, so the load-phase fallback and
            // the published floor are the same number rather than two literals that could drift.
            new("emailMaxTrackedRecipients", "send", "Tracked recipients",
                "How many addresses the throttle holds at once. At capacity it stops throttling, so a "
                + "smaller table weakens the limit above rather than tightening it. Raising it also speeds the "
                + "sweep that brings the table back under capacity. Existing entries age out "
                + "over up to a full window, so a change is not instant in either direction.",
                SettingClaim.Security, SettingControl.Number,
                Min: SystemSettingsDefaults.EmailMaxTrackedRecipients, Max: 200000,
                Field: nameof(SystemSettingsUpdate.EmailMaxTrackedRecipients),
                Load: (p, dto) => p.SetIntLoaded("emailMaxTrackedRecipients", dto.EmailMaxTrackedRecipients),
                Write: (p, req) => req.EmailMaxTrackedRecipients = p.IntRequest("emailMaxTrackedRecipients"),
                MinFrom: dto => dto.EmailMaxTrackedRecipientsFloor),
        ]),
        new("Data", Icons.Material.Filled.Storage,
        [
            // Background jobs (issue #409 §3.6): the worker prunes finished runs older than this once an hour.
            new("jobRunRetentionDays", "history", "Background job history",
                "How long a finished background-job run is kept in the run log before it is removed.",
                SettingClaim.Count, SettingControl.Number,
                Min: SystemSettingsBounds.JobRunRetentionDaysMin, Max: SystemSettingsBounds.JobRunRetentionDaysMax,
                Field: nameof(SystemSettingsUpdate.JobRunRetentionDays),
                Load: (p, dto) => p.SetIntLoaded("jobRunRetentionDays", dto.JobRunRetentionDays),
                Write: (p, req) => req.JobRunRetentionDays = p.IntRequest("jobRunRetentionDays"),
                Unit: "days"),
        ]),
        // ── AI recipe import (issue #602). The switch is an integration switch (settings.md): its own row,
        // independent of AI document analysis, with the security claim because it authorises sending recipe
        // sources to the processor configured under File analysis. Retention is a plain count.
        new("AI recipe import", Icons.Material.Filled.AutoAwesome,
        [
            new("recipeImportEnabled", "auto_awesome", "AI recipe import",
                "When off, no recipe text or file is sent to the AI processor: Import with AI is unavailable and "
                + "imports still waiting fail without sending. Imports already made stay readable. It uses the "
                + "processor, model, address and API key set under File analysis. Read live on every request. "
                + "Every change is written to the audit log.",
                SettingClaim.Security,
                Field: nameof(SystemSettingsUpdate.RecipeImportEnabled),
                Load: (p, dto) => p.SetBoolLoaded("recipeImportEnabled", dto.RecipeImportEnabled),
                Write: (p, req) => req.RecipeImportEnabled = p.BoolRequest("recipeImportEnabled")),
            new("recipeImportRetentionDays", "auto_delete", "Keep imports for",
                "How long a finished recipe import keeps the text and files that were sent and its draft recipe. "
                + "After that they are removed; a record that something was sent stays. Recipes already created are never affected.",
                SettingClaim.Count, SettingControl.Number,
                Min: SystemSettingsBounds.RecipeImportRetentionDaysMin, Max: SystemSettingsBounds.RecipeImportRetentionDaysMax,
                Field: nameof(SystemSettingsUpdate.RecipeImportRetentionDays),
                Load: (p, dto) => p.SetIntLoaded("recipeImportRetentionDays", dto.RecipeImportRetentionDays),
                Write: (p, req) => req.RecipeImportRetentionDays = p.IntRequest("recipeImportRetentionDays"),
                Unit: "days"),
        ]),
        // ── Weather (issue #348). Nothing is fetched until the contact is set: MET Norway's terms ask
        // every client for one, and it travels in the User-Agent of every request. The Frost client id
        // joins this card from SecretCatalogue.
        new("Weather", Icons.Material.Filled.WbSunny,
        [
            new("weatherUserAgentContact", "contact_mail", "Contact for MET Norway",
                "How MET Norway can reach whoever runs this instance — an email address or an https:// URL. It "
                + "is sent in the User-Agent of every weather request, as MET's terms require. Empty means no "
                + "weather is fetched at all. Use a role address (weather@…, ops@…) rather than a person's: "
                + "it leaves the deployment with every request.",
                SettingClaim.Count, SettingControl.Text, Max: WeatherRules.ContactMaxLength,
                Field: nameof(SystemSettingsUpdate.WeatherUserAgentContact),
                Load: (p, dto) => p.SetTextLoaded("weatherUserAgentContact", dto.WeatherUserAgentContact),
                Write: (p, req) => req.WeatherUserAgentContact = p.TextRequest("weatherUserAgentContact"),
                InputMode: "email",
                Placeholder: "weather@example.net",
                AllowEmpty: true,
                Rule: WeatherRules.ValidateContact),
            new("weatherRefreshIntervalMinutes", "update", "Forecast refresh interval",
                "How often a weather point set to refresh on a schedule asks MET Norway for a newer forecast. MET's own "
                + "Expires header can stretch it; nothing shortens it.",
                SettingClaim.Count, SettingControl.Number,
                Min: SystemSettingsBounds.WeatherRefreshIntervalMinutesMin, Max: SystemSettingsBounds.WeatherRefreshIntervalMinutesMax,
                Field: nameof(SystemSettingsUpdate.WeatherRefreshIntervalMinutes),
                Load: (p, dto) => p.SetIntLoaded("weatherRefreshIntervalMinutes", dto.WeatherRefreshIntervalMinutes),
                Write: (p, req) => req.WeatherRefreshIntervalMinutes = p.IntRequest("weatherRefreshIntervalMinutes"),
                Unit: "min"),
            new("weatherMaxTrackedPoints", "format_list_numbered", "Points on a schedule",
                "How many weather points may refresh their forecast in the background. Others are fetched only "
                + "when someone looks. Lowering it pauses the most recently enabled ones first.",
                SettingClaim.Count, SettingControl.Number,
                Min: SystemSettingsBounds.WeatherMaxTrackedPointsMin, Max: SystemSettingsBounds.WeatherMaxTrackedPointsMax,
                Field: nameof(SystemSettingsUpdate.WeatherMaxTrackedPoints),
                Load: (p, dto) => p.SetIntLoaded("weatherMaxTrackedPoints", dto.WeatherMaxTrackedPoints),
                Write: (p, req) => req.WeatherMaxTrackedPoints = p.IntRequest("weatherMaxTrackedPoints")),
            new("weatherDefaultPointId", "home", "Household default weather point",
                "The weather everyone sees until they pick their own on the dashboard. Only weather points with "
                + "their forecast on can be chosen.",
                SettingClaim.Count, SettingControl.Text,
                Field: nameof(SystemSettingsUpdate.WeatherDefaultPointId),
                Load: (p, dto) => p.SetTextLoaded("weatherDefaultPointId", dto.WeatherDefaultPointId),
                Write: (p, req) => req.WeatherDefaultPointId = p.TextRequest("weatherDefaultPointId"),
                AllowEmpty: true,
                Rule: WeatherRules.ValidateDefaultPointId,
                OptionsFrom: p => p.WeatherDefaultOptions()),
        ]),
        // ── Per-request caps (issue #421 Wave 3) ──────────────────────────────────────────────────
        // These were invisible before: the Contracts and PhotoLibrary config sections had no
        // appsettings entry at all, and the two journal caps were `private const` in their services.
        new("Contracts", Icons.Material.Filled.Description,
        [
            new("contractMaxPartiesPerContract", "groups", "Max parties per contract",
                "Upper limit on the counterparties one contract may name.",
                SettingClaim.Count, SettingControl.Number, Min: SystemSettingsBounds.ContractMaxPartiesPerContractMin, Max: SystemSettingsBounds.ContractMaxPartiesPerContractMax,
                Field: nameof(SystemSettingsUpdate.ContractMaxPartiesPerContract),
                Load: (p, dto) => p.SetIntLoaded("contractMaxPartiesPerContract", dto.ContractMaxPartiesPerContract),
                Write: (p, req) => req.ContractMaxPartiesPerContract = p.IntRequest("contractMaxPartiesPerContract")),
            new("contractMaxFilesPerContract", "attach_file", "Max files per contract",
                "Upper limit on the documents attached to a single contract.",
                SettingClaim.Count, SettingControl.Number, Min: SystemSettingsBounds.ContractMaxFilesPerContractMin, Max: SystemSettingsBounds.ContractMaxFilesPerContractMax,
                Field: nameof(SystemSettingsUpdate.ContractMaxFilesPerContract),
                Load: (p, dto) => p.SetIntLoaded("contractMaxFilesPerContract", dto.ContractMaxFilesPerContract),
                Write: (p, req) => req.ContractMaxFilesPerContract = p.IntRequest("contractMaxFilesPerContract")),
            new("contractMaxTermsPerContract", "gavel", "Max terms per contract",
                "Upper limit on term (rate/fee) entries recorded against one contract. Higher than the "
                + "parties and files caps because a term series grows by supersession over the life of "
                + "the agreement — each price change is a new entry, not an edit.",
                SettingClaim.Count, SettingControl.Number, Min: SystemSettingsBounds.ContractMaxTermsPerContractMin, Max: SystemSettingsBounds.ContractMaxTermsPerContractMax,
                Field: nameof(SystemSettingsUpdate.ContractMaxTermsPerContract),
                Load: (p, dto) => p.SetIntLoaded("contractMaxTermsPerContract", dto.ContractMaxTermsPerContract),
                Write: (p, req) => req.ContractMaxTermsPerContract = p.IntRequest("contractMaxTermsPerContract")),
            new("contractMaxSmartTagsPerContract", "sell", "Max smart tags per contract",
                "Upper limit on the saved tag filters one contract may carry. Its ceiling is the number "
                + "of tag ids the transactions list accepts in one query, because that is what the "
                + "filter is resolved through.",
                SettingClaim.Count, SettingControl.Number,
                Min: SystemSettingsBounds.ContractMaxSmartTagsPerContractMin,
                Max: SystemSettingsBounds.ContractMaxSmartTagsPerContractMax,
                Field: nameof(SystemSettingsUpdate.ContractMaxSmartTagsPerContract),
                Load: (p, dto) => p.SetIntLoaded(
                    "contractMaxSmartTagsPerContract", dto.ContractMaxSmartTagsPerContract),
                Write: (p, req) => req.ContractMaxSmartTagsPerContract =
                    p.IntRequest("contractMaxSmartTagsPerContract")),
            new("contractMaxSummaryContracts", "format_list_numbered", "Max contracts in summary",
                "Upper limit on the contracts the summary roll-up aggregates over.",
                SettingClaim.Count, SettingControl.Number, Min: SystemSettingsBounds.ContractMaxSummaryContractsMin, Max: SystemSettingsBounds.ContractMaxSummaryContractsMax,
                Field: nameof(SystemSettingsUpdate.ContractMaxSummaryContracts),
                Load: (p, dto) => p.SetIntLoaded("contractMaxSummaryContracts", dto.ContractMaxSummaryContracts),
                Write: (p, req) => req.ContractMaxSummaryContracts = p.IntRequest("contractMaxSummaryContracts")),
            // The two windows sit in the Contracts group rather than in a group of their own: an
            // administrator looking for "how far ahead do contracts warn me" looks under Contracts.
            new("contractEndingWindowDays", "hourglass_bottom", "“Ending soon” window",
                "How many days ahead an active contract's end date is flagged as ending soon. The same "
                + "window defines the two groups either side of it — a term about to start, and one "
                + "that ran out and has not been archived — so widening it widens all three.",
                SettingClaim.Count, SettingControl.Number,
                Min: SystemSettingsBounds.ContractEndingWindowDaysMin,
                Max: SystemSettingsBounds.ContractEndingWindowDaysMax,
                Field: nameof(SystemSettingsUpdate.ContractEndingWindowDays),
                Load: (p, dto) => p.SetIntLoaded("contractEndingWindowDays", dto.ContractEndingWindowDays),
                Write: (p, req) => req.ContractEndingWindowDays = p.IntRequest("contractEndingWindowDays"),
                Unit: "days"),
            new("contractChargeWindowDays", "event_repeat", "“Upcoming transactions” window",
                "How many days ahead a contract's next recurring charge or receipt is surfaced. Each is "
                + "derived from the fee terms in force — nothing is scheduled or stored.",
                SettingClaim.Count, SettingControl.Number,
                Min: SystemSettingsBounds.ContractChargeWindowDaysMin,
                Max: SystemSettingsBounds.ContractChargeWindowDaysMax,
                Field: nameof(SystemSettingsUpdate.ContractChargeWindowDays),
                Load: (p, dto) => p.SetIntLoaded("contractChargeWindowDays", dto.ContractChargeWindowDays),
                Write: (p, req) => req.ContractChargeWindowDays = p.IntRequest("contractChargeWindowDays"),
                Unit: "days"),
            new("contractMaxSummaryCharges", "receipt_long", "Max upcoming transactions shown per direction",
                "Upper limit on the next-movement rows listed in the page-header panel. It applies to "
                + "outgoing charges and incoming receipts separately, so the panel can list up to twice "
                + "this number in total.",
                SettingClaim.Count, SettingControl.Number,
                Min: SystemSettingsBounds.ContractMaxSummaryChargesMin,
                Max: SystemSettingsBounds.ContractMaxSummaryChargesMax,
                Field: nameof(SystemSettingsUpdate.ContractMaxSummaryCharges),
                Load: (p, dto) => p.SetIntLoaded("contractMaxSummaryCharges", dto.ContractMaxSummaryCharges),
                Write: (p, req) => req.ContractMaxSummaryCharges = p.IntRequest("contractMaxSummaryCharges")),
        ]),
        // The two photo caps are TIGHTEN-ONLY, and this is the first use of MaxFrom: their ceiling is
        // a compile-time constant that also drives [MaxLength] on the photo request DTOs, so model
        // validation would reject an over-cap request before the setting was ever consulted. The
        // control bounds itself from the server-supplied ceiling rather than offering 100,000 and
        // letting the save fail.
        new("Photos", Icons.Material.Filled.PhotoLibrary,
        [
            new("photoMaxLinksPerKind", "sell", "Max links per photo",
                $"Upper limit on the tags, people or albums one photo may link to. Cannot be raised above "
                + $"{PhotoLimits.MaxLinksPerKind:N0} — the request format rejects a longer list before this "
                + "setting is consulted.",
                SettingClaim.Count, SettingControl.Number, Min: 1, Max: 50,
                Field: nameof(SystemSettingsUpdate.PhotoMaxLinksPerKind),
                Load: (p, dto) => p.SetIntLoaded("photoMaxLinksPerKind", dto.PhotoMaxLinksPerKind),
                Write: (p, req) => req.PhotoMaxLinksPerKind = p.IntRequest("photoMaxLinksPerKind"),
                MaxFrom: dto => dto.PhotoMaxLinksPerKindCeiling, LowerOnly: true),
            // ── issue #434 keys 4-5. The read size takes the SECURITY claim: all nine existing
            // megabyte rows do, every one of them is also a resource bound, and on the ordinary claim
            // this would be the only unaudited megabyte cap in a ten-row set — governing a per-upload
            // byte-array multiplier reachable by every uploader.
            new("photoMetadataReadMegabytes", "sd_storage", "Metadata read size",
                "How much of an accepted image is read to pull EXIF. Beyond this, extraction is skipped and "
                + "the photo is still stored.",
                SettingClaim.Security, SettingControl.Size, Min: 1, Max: 16,
                Field: nameof(SystemSettingsUpdate.PhotoMetadataReadMegabytes),
                Load: (p, dto) => p.SetIntLoaded("photoMetadataReadMegabytes", dto.PhotoMetadataReadMegabytes),
                Write: (p, req) => req.PhotoMetadataReadMegabytes = p.IntRequest("photoMetadataReadMegabytes"),
                MaxFrom: dto => dto.PhotoMetadataReadMegabytesCeiling),
            new("photoMetadataExtractionTimeoutSeconds", "schedule", "Metadata extraction timeout",
                "How long EXIF extraction may run per photo. On timeout the photo is stored without metadata.",
                SettingClaim.Count, SettingControl.Number, Min: 1, Max: 120,
                Field: nameof(SystemSettingsUpdate.PhotoMetadataExtractionTimeoutSeconds),
                Load: (p, dto) => p.SetIntLoaded(
                    "photoMetadataExtractionTimeoutSeconds", dto.PhotoMetadataExtractionTimeoutSeconds),
                Write: (p, req) => req.PhotoMetadataExtractionTimeoutSeconds =
                    p.IntRequest("photoMetadataExtractionTimeoutSeconds"),
                Unit: "sec"),
            new("photoMaxAlbumMembers", "collections", "Max photos per album",
                $"Upper limit on the photos one album may hold. Cannot be raised above "
                + $"{PhotoLimits.MaxAlbumMembers:N0} — the request format rejects a longer list before this "
                + "setting is consulted.",
                SettingClaim.Count, SettingControl.Number, Min: 1, Max: 1000,
                Field: nameof(SystemSettingsUpdate.PhotoMaxAlbumMembers),
                Load: (p, dto) => p.SetIntLoaded("photoMaxAlbumMembers", dto.PhotoMaxAlbumMembers),
                Write: (p, req) => req.PhotoMaxAlbumMembers = p.IntRequest("photoMaxAlbumMembers"),
                MaxFrom: dto => dto.PhotoMaxAlbumMembersCeiling, LowerOnly: true),
        ]),
        // The upload cap is ceiling-bounded like the two photo caps, but its ceiling is startup
        // configuration rather than a compile-time constant: raising it past the transport limit would
        // be refused by Kestrel before the setting was ever read.
        new("Storage", Icons.Material.Filled.Storage,
        [
            new("fileStorageMaxUploadMegabytes", "sd_storage", "Maximum upload file size",
                "Largest single file accepted by any upload surface, and by a task import storing an attached "
                + "file. Cannot be raised above the request-size "
                + "ceiling the server starts with.",
                SettingClaim.Security, SettingControl.Size,
                Min: SystemSettingsBounds.FileStorageMaxUploadMegabytesMin, Max: SystemSettingsBounds.FileStorageMaxUploadMegabytesMax,
                Field: nameof(SystemSettingsUpdate.FileStorageMaxUploadMegabytes),
                Load: (p, dto) => p.SetIntLoaded("fileStorageMaxUploadMegabytes", dto.FileStorageMaxUploadMegabytes),
                Write: (p, req) => req.FileStorageMaxUploadMegabytes = p.IntRequest("fileStorageMaxUploadMegabytes"),
                MaxFrom: dto => dto.UploadMegabytesCeiling, LowerOnly: true),
            // The deletion grace (issue #346 §4.3): the retention period for deleted personal data on the file
            // volumes, so it takes the security claim and every change is audited. The server's two advisories
            // (below 24 h, above 72 h) render in the row's Advisory slot.
            new("fileStorageDeletionGraceHours", "delete_sweep", "Deletion grace period",
                "How long a deleted file stays on disk before it is erased, so a backup taken just before the delete still matches the disk. Files holding personal data, such as photo thumbnails, are erased at once and ignore this.",
                SettingClaim.Security, SettingControl.Number,
                Min: SystemSettingsBounds.FileStorageDeletionGraceHoursMin, Max: SystemSettingsBounds.FileStorageDeletionGraceHoursMax,
                Field: nameof(SystemSettingsUpdate.FileStorageDeletionGraceHours),
                Load: (p, dto) => p.SetIntLoaded("fileStorageDeletionGraceHours", dto.FileStorageDeletionGraceHours),
                Write: (p, req) => req.FileStorageDeletionGraceHours = p.IntRequest("fileStorageDeletionGraceHours"),
                Unit: "hours"),
        ], Panel: StorageHealthPanelKey),
        new("Journal limits", Icons.Material.Filled.MenuBook,
        [
            new("journalEntryMaxLinksPerKind", "link", "Max links per journal entry",
                "Upper limit on the records one journal entry may link to, per kind.",
                SettingClaim.Count, SettingControl.Number, Min: 1, Max: 100000,
                Field: nameof(SystemSettingsUpdate.JournalEntryMaxLinksPerKind),
                Load: (p, dto) => p.SetIntLoaded("journalEntryMaxLinksPerKind", dto.JournalEntryMaxLinksPerKind),
                Write: (p, req) => req.JournalEntryMaxLinksPerKind = p.IntRequest("journalEntryMaxLinksPerKind")),
            new("journalTaskMaxLinksPerKind", "link", "Max links per task",
                "Upper limit on the records one task may link to, per kind.",
                SettingClaim.Count, SettingControl.Number, Min: 1, Max: 100000,
                Field: nameof(SystemSettingsUpdate.JournalTaskMaxLinksPerKind),
                Load: (p, dto) => p.SetIntLoaded("journalTaskMaxLinksPerKind", dto.JournalTaskMaxLinksPerKind),
                Write: (p, req) => req.JournalTaskMaxLinksPerKind = p.IntRequest("journalTaskMaxLinksPerKind")),
            new("calendarEventMaxLinksPerKind", "attach_file", "Max attachments per event",
                "Upper limit on the files, and separately the photos, one calendar event or recurring series may carry.",
                SettingClaim.Count, SettingControl.Number,
                Min: SystemSettingsBounds.CalendarEventMaxLinksPerKindMin,
                Max: SystemSettingsBounds.CalendarEventMaxLinksPerKindMax,
                Field: nameof(SystemSettingsUpdate.CalendarEventMaxLinksPerKind),
                Load: (p, dto) => p.SetIntLoaded("calendarEventMaxLinksPerKind", dto.CalendarEventMaxLinksPerKind),
                Write: (p, req) => req.CalendarEventMaxLinksPerKind = p.IntRequest("calendarEventMaxLinksPerKind")),
        ]),
        new("Contacts import & export", Icons.Material.Filled.Contacts,
        [
            new("contactVCardMaxExportRows", "file_download", "Maximum contacts per export",
                "Upper limit on the rows a vCard (.vcf) export may produce. \"No limit\" keeps exports unbounded.",
                SettingClaim.Count, SettingControl.Capacity, Min: 1, Max: 1_000_000,
                Field: nameof(SystemSettingsUpdate.ContactVCardMaxExportRows),
                Load: (p, dto) => p.SetCapacityLoaded("contactVCardMaxExportRows", dto.ContactVCardMaxExportRows), Write: (p, req) => req.ContactVCardMaxExportRows = p.CapacityRequest("contactVCardMaxExportRows")),
            new("contactVCardMaxImportEntries", "file_upload", "Maximum contacts per import",
                "Upper limit on the entries accepted from an imported vCard file.",
                SettingClaim.Count, SettingControl.Capacity, Min: 1, Max: 1_000_000,
                Field: nameof(SystemSettingsUpdate.ContactVCardMaxImportEntries),
                Load: (p, dto) => p.SetCapacityLoaded("contactVCardMaxImportEntries", dto.ContactVCardMaxImportEntries), Write: (p, req) => req.ContactVCardMaxImportEntries = p.CapacityRequest("contactVCardMaxImportEntries")),
            // ── issue #434 key 12. TIGHTEN-ONLY: each repeatable property costs a sibling query AND its
            // own save, multiplied by an import entry cap that ships unlimited, so any number above the
            // shipped 200 would be a guess about a product of three unbounded terms. The static Max
            // names the same shared constant the server's [Range] maximum does.
            new("contactVCardMaxRepeatablePropertiesPerEntry", "list_alt", "Repeatable properties per contact",
                "How many emails, phones or addresses one imported contact may carry. Each one is saved "
                + "individually, and that cost multiplies by the import cap above — which ships unbounded.",
                SettingClaim.Count, SettingControl.Number,
                Min: 1, Max: SystemSettingsDefaults.ContactVCardMaxRepeatablePropertiesPerEntry,
                Field: nameof(SystemSettingsUpdate.ContactVCardMaxRepeatablePropertiesPerEntry),
                Load: (p, dto) => p.SetIntLoaded(
                    "contactVCardMaxRepeatablePropertiesPerEntry",
                    dto.ContactVCardMaxRepeatablePropertiesPerEntry),
                Write: (p, req) => req.ContactVCardMaxRepeatablePropertiesPerEntry =
                    p.IntRequest("contactVCardMaxRepeatablePropertiesPerEntry"),
                MaxFrom: dto => dto.ContactVCardMaxRepeatablePropertiesPerEntryCeiling, LowerOnly: true),
            new("contactVCardMaxExportMegabytes", "sd_storage", "Maximum export file size",
                "Largest vCard (.vcf) file an export may produce. A too-large export is truncated rather "
                + "than exceeding this, so the download fails cleanly instead of silently.",
                SettingClaim.Security, SettingControl.Size, Min: 1, Max: 1024,
                Field: nameof(SystemSettingsUpdate.ContactVCardMaxExportMegabytes),
                Load: (p, dto) => p.SetIntLoaded("contactVCardMaxExportMegabytes", dto.ContactVCardMaxExportMegabytes), Write: (p, req) => req.ContactVCardMaxExportMegabytes = p.IntRequest("contactVCardMaxExportMegabytes")),
            new("contactVCardMaxImportMegabytes", "sd_storage", "Maximum import file size",
                "Largest vCard (.vcf) upload accepted. Above ~64 MB, also raise the reverse proxy's body-size limit.",
                SettingClaim.Security, SettingControl.Size, Min: 1, Max: 1024,
                Field: nameof(SystemSettingsUpdate.ContactVCardMaxImportMegabytes),
                Load: (p, dto) => p.SetIntLoaded("contactVCardMaxImportMegabytes", dto.ContactVCardMaxImportMegabytes), Write: (p, req) => req.ContactVCardMaxImportMegabytes = p.IntRequest("contactVCardMaxImportMegabytes")),
        ],
        RoundTrip: new RoundTripPair("contactVCardMaxExportRows", "contactVCardMaxImportEntries")),
        new("Calendars import & export", Icons.Material.Filled.CalendarMonth,
        [
            new("calendarIcsMaxExportEvents", "file_download", "Maximum events per export",
                "Upper limit on the VEVENTs an iCalendar (.ics) export may produce.",
                SettingClaim.Count, SettingControl.Capacity, Min: 1, Max: 1_000_000,
                Field: nameof(SystemSettingsUpdate.CalendarIcsMaxExportEvents),
                Load: (p, dto) => p.SetCapacityLoaded("calendarIcsMaxExportEvents", dto.CalendarIcsMaxExportEvents), Write: (p, req) => req.CalendarIcsMaxExportEvents = p.CapacityRequest("calendarIcsMaxExportEvents")),
            new("calendarIcsMaxImportEvents", "file_upload", "Maximum events per import",
                "Upper limit on the VEVENTs accepted from an imported .ics file.",
                SettingClaim.Count, SettingControl.Capacity, Min: 1, Max: 1_000_000,
                Field: nameof(SystemSettingsUpdate.CalendarIcsMaxImportEvents),
                Load: (p, dto) => p.SetCapacityLoaded("calendarIcsMaxImportEvents", dto.CalendarIcsMaxImportEvents), Write: (p, req) => req.CalendarIcsMaxImportEvents = p.CapacityRequest("calendarIcsMaxImportEvents")),
            new("calendarIcsMaxExportMegabytes", "sd_storage", "Maximum export file size",
                "Largest iCalendar (.ics) file an export may produce, attached files included — they travel "
                + "inside it. An export over this is refused before the "
                + "download starts, or stopped mid-download — never truncated.",
                SettingClaim.Security, SettingControl.Size,
                Min: SystemSettingsBounds.CalendarIcsMaxExportMegabytesMin, Max: SystemSettingsBounds.CalendarIcsMaxExportMegabytesMax,
                Field: nameof(SystemSettingsUpdate.CalendarIcsMaxExportMegabytes),
                Load: (p, dto) => p.SetIntLoaded("calendarIcsMaxExportMegabytes", dto.CalendarIcsMaxExportMegabytes), Write: (p, req) => req.CalendarIcsMaxExportMegabytes = p.IntRequest("calendarIcsMaxExportMegabytes")),
            new("calendarIcsMaxImportMegabytes", "sd_storage", "Maximum import file size",
                "Largest iCalendar (.ics) upload accepted, attached files included. It cannot usefully exceed the "
                + "reverse proxy's body-size "
                + "limit; see the deployment guide.",
                SettingClaim.Security, SettingControl.Size,
                Min: SystemSettingsBounds.CalendarIcsMaxImportMegabytesMin, Max: SystemSettingsBounds.CalendarIcsMaxImportMegabytesMax,
                Field: nameof(SystemSettingsUpdate.CalendarIcsMaxImportMegabytes),
                Load: (p, dto) => p.SetIntLoaded("calendarIcsMaxImportMegabytes", dto.CalendarIcsMaxImportMegabytes), Write: (p, req) => req.CalendarIcsMaxImportMegabytes = p.IntRequest("calendarIcsMaxImportMegabytes")),
            // ── issue #434 keys 8-10. These three bound the ALL-CALENDARS export/import path, not a
            // single calendar's, which is why they belong here rather than in "Calendar limits". Their
            // ceilings are twice the shipped default, derived from the concurrency actually permitted on
            // this surface (two import permits globally, two exports per user, four exports globally),
            // so worst-case concurrent materialisation stays within the same order as today's.
            new("calendarIcsMaxAggregateExportRows", "file_download", "Maximum rows per whole-calendar export",
                "Upper limit on the rows one export of every calendar at once may fetch before it refuses. "
                + "Bounds the intermediate work, not the output size — the export cap above is that.",
                SettingClaim.Count, SettingControl.Number, Min: 1, Max: 40000,
                Field: nameof(SystemSettingsUpdate.CalendarIcsMaxAggregateExportRows),
                Load: (p, dto) => p.SetIntLoaded(
                    "calendarIcsMaxAggregateExportRows", dto.CalendarIcsMaxAggregateExportRows),
                Write: (p, req) => req.CalendarIcsMaxAggregateExportRows =
                    p.IntRequest("calendarIcsMaxAggregateExportRows"),
                MaxFrom: dto => dto.CalendarIcsMaxAggregateExportRowsCeiling),
            new("calendarIcsMaxAggregateOccurrences", "autorenew", "Max generated occurrences per import",
                "How many events one calendar (.ics) import may create or regenerate in total, including every occurrence "
                + "of a recurring series. All of them are held in memory while the import runs.",
                SettingClaim.Count, SettingControl.Number, Min: 1, Max: 20000,
                Field: nameof(SystemSettingsUpdate.CalendarIcsMaxAggregateOccurrences),
                Load: (p, dto) => p.SetIntLoaded(
                    "calendarIcsMaxAggregateOccurrences", dto.CalendarIcsMaxAggregateOccurrences),
                Write: (p, req) => req.CalendarIcsMaxAggregateOccurrences =
                    p.IntRequest("calendarIcsMaxAggregateOccurrences"),
                MaxFrom: dto => dto.CalendarIcsMaxAggregateOccurrencesCeiling),
            new("calendarIcsMaxAggregateExportWindowDays", "event_available", "Whole-calendar export window",
                "How wide a date range one export of every calendar at once may cover.",
                SettingClaim.Count, SettingControl.Number, Min: 1, Max: 3650,
                Field: nameof(SystemSettingsUpdate.CalendarIcsMaxAggregateExportWindowDays),
                Load: (p, dto) => p.SetIntLoaded(
                    "calendarIcsMaxAggregateExportWindowDays", dto.CalendarIcsMaxAggregateExportWindowDays),
                Write: (p, req) => req.CalendarIcsMaxAggregateExportWindowDays =
                    p.IntRequest("calendarIcsMaxAggregateExportWindowDays"),
                Unit: "days"),
        ],
        RoundTrip: new RoundTripPair("calendarIcsMaxExportEvents", "calendarIcsMaxImportEvents")),
        new("Tasks import & export", Icons.Material.Filled.Checklist,
        [
            new("taskIcsMaxExportTasks", "file_download", "Maximum tasks per export",
                "Upper limit on the VTODOs an iCalendar (.ics) export may produce.",
                SettingClaim.Count, SettingControl.Capacity, Min: 1, Max: 1_000_000,
                Field: nameof(SystemSettingsUpdate.TaskIcsMaxExportTasks),
                Load: (p, dto) => p.SetCapacityLoaded("taskIcsMaxExportTasks", dto.TaskIcsMaxExportTasks), Write: (p, req) => req.TaskIcsMaxExportTasks = p.CapacityRequest("taskIcsMaxExportTasks")),
            new("taskIcsMaxImportTasks", "file_upload", "Maximum tasks per import",
                "Upper limit on the VTODOs accepted from an imported .ics file.",
                SettingClaim.Count, SettingControl.Capacity, Min: 1, Max: 1_000_000,
                Field: nameof(SystemSettingsUpdate.TaskIcsMaxImportTasks),
                Load: (p, dto) => p.SetCapacityLoaded("taskIcsMaxImportTasks", dto.TaskIcsMaxImportTasks), Write: (p, req) => req.TaskIcsMaxImportTasks = p.CapacityRequest("taskIcsMaxImportTasks")),
            new("taskIcsMaxExportMegabytes", "sd_storage", "Maximum export file size",
                "Largest iCalendar (.ics) file an export may produce, attached files included — they travel inside "
                + "it. An export over this is refused before the download starts.",
                SettingClaim.Security, SettingControl.Size,
                Min: SystemSettingsBounds.TaskIcsMaxExportMegabytesMin, Max: SystemSettingsBounds.TaskIcsMaxExportMegabytesMax,
                Field: nameof(SystemSettingsUpdate.TaskIcsMaxExportMegabytes),
                Load: (p, dto) => p.SetIntLoaded("taskIcsMaxExportMegabytes", dto.TaskIcsMaxExportMegabytes), Write: (p, req) => req.TaskIcsMaxExportMegabytes = p.IntRequest("taskIcsMaxExportMegabytes")),
            new("taskIcsMaxImportMegabytes", "sd_storage", "Maximum import file size",
                "Largest iCalendar (.ics) upload accepted, attached files included. It cannot usefully exceed the "
                + "reverse proxy's body-size limit, which ships at 10 GB.",
                SettingClaim.Security, SettingControl.Size,
                Min: SystemSettingsBounds.TaskIcsMaxImportMegabytesMin, Max: SystemSettingsBounds.TaskIcsMaxImportMegabytesMax,
                Field: nameof(SystemSettingsUpdate.TaskIcsMaxImportMegabytes),
                Load: (p, dto) => p.SetIntLoaded("taskIcsMaxImportMegabytes", dto.TaskIcsMaxImportMegabytes), Write: (p, req) => req.TaskIcsMaxImportMegabytes = p.IntRequest("taskIcsMaxImportMegabytes")),
        ],
        RoundTrip: new RoundTripPair("taskIcsMaxExportTasks", "taskIcsMaxImportTasks")),
        new("Journal import & export", Icons.Material.Filled.ImportContacts,
        [
            new("journalIcsMaxExportRows", "file_download", "Maximum entries per export",
                "Upper limit on the VJOURNALs an iCalendar (.ics) export may produce.",
                SettingClaim.Count, SettingControl.Capacity, Min: 1, Max: 1_000_000,
                Field: nameof(SystemSettingsUpdate.JournalIcsMaxExportRows),
                Load: (p, dto) => p.SetCapacityLoaded("journalIcsMaxExportRows", dto.JournalIcsMaxExportRows), Write: (p, req) => req.JournalIcsMaxExportRows = p.CapacityRequest("journalIcsMaxExportRows")),
            new("journalIcsMaxImportEntries", "file_upload", "Maximum entries per import",
                "Upper limit on the VJOURNALs accepted from an imported .ics file.",
                SettingClaim.Count, SettingControl.Capacity, Min: 1, Max: 1_000_000,
                Field: nameof(SystemSettingsUpdate.JournalIcsMaxImportEntries),
                Load: (p, dto) => p.SetCapacityLoaded("journalIcsMaxImportEntries", dto.JournalIcsMaxImportEntries), Write: (p, req) => req.JournalIcsMaxImportEntries = p.CapacityRequest("journalIcsMaxImportEntries")),
            new("journalIcsMaxExportMegabytes", "sd_storage", "Maximum export file size",
                "Largest iCalendar (.ics) file an export may produce, attached files and photos included — they "
                + "travel inside it. An export over this is refused before the download starts.",
                SettingClaim.Security, SettingControl.Size,
                Min: SystemSettingsBounds.JournalIcsMaxExportMegabytesMin, Max: SystemSettingsBounds.JournalIcsMaxExportMegabytesMax,
                Field: nameof(SystemSettingsUpdate.JournalIcsMaxExportMegabytes),
                Load: (p, dto) => p.SetIntLoaded("journalIcsMaxExportMegabytes", dto.JournalIcsMaxExportMegabytes), Write: (p, req) => req.JournalIcsMaxExportMegabytes = p.IntRequest("journalIcsMaxExportMegabytes")),
            new("journalIcsMaxImportMegabytes", "sd_storage", "Maximum import file size",
                "Largest iCalendar (.ics) upload accepted, attached files and photos included. It cannot usefully "
                + "exceed the reverse proxy's body-size limit, which ships at 10 GB.",
                SettingClaim.Security, SettingControl.Size,
                Min: SystemSettingsBounds.JournalIcsMaxImportMegabytesMin, Max: SystemSettingsBounds.JournalIcsMaxImportMegabytesMax,
                Field: nameof(SystemSettingsUpdate.JournalIcsMaxImportMegabytes),
                Load: (p, dto) => p.SetIntLoaded("journalIcsMaxImportMegabytes", dto.JournalIcsMaxImportMegabytes), Write: (p, req) => req.JournalIcsMaxImportMegabytes = p.IntRequest("journalIcsMaxImportMegabytes")),
        ],
        RoundTrip: new RoundTripPair("journalIcsMaxExportRows", "journalIcsMaxImportEntries")),
        // ── Calendar limits (issue #434 keys 6-10) ────────────────────────────────────────────────
        // A group of its own rather than rows on "Calendars import & export": three of the five govern
        // ordinary calendar reads and writes, not import or export, so filing them there would state
        // something untrue about their scope.
        new("Calendar limits", Icons.Material.Filled.EventNote,
        [
            new("calendarMaxWindowDays", "event_available", "Calendar view window",
                "How wide a date range one calendar request may ask for.",
                SettingClaim.Count, SettingControl.Number, Min: 1, Max: 3650,
                Field: nameof(SystemSettingsUpdate.CalendarMaxWindowDays),
                Load: (p, dto) => p.SetIntLoaded("calendarMaxWindowDays", dto.CalendarMaxWindowDays),
                Write: (p, req) => req.CalendarMaxWindowDays = p.IntRequest("calendarMaxWindowDays"),
                Unit: "days"),
            new("calendarMaxEventDurationDays", "schedule", "Maximum event duration",
                "How long a single event may run before it is rejected as a mistake.",
                SettingClaim.Count, SettingControl.Number, Min: 1, Max: 3650,
                Field: nameof(SystemSettingsUpdate.CalendarMaxEventDurationDays),
                Load: (p, dto) => p.SetIntLoaded("calendarMaxEventDurationDays", dto.CalendarMaxEventDurationDays),
                Write: (p, req) => req.CalendarMaxEventDurationDays = p.IntRequest("calendarMaxEventDurationDays"),
                Unit: "days"),
            // TIGHTEN-ONLY: one calendar row is written per generated occurrence, so raising this is a
            // write multiplier available to every user who can create a calendar entry, and the cost
            // survives lowering the setting back. The ceiling published by the server IS the shipped
            // default, and the static Max names the same shared constant so the two cannot drift.
            new("recurrenceMaxGeneratedOccurrences", "autorenew", "Maximum generated occurrences",
                "How many occurrences one recurrence rule may generate. Each one is saved as its own event, "
                + "so raising this multiplies what a single request writes — and lowering it back does not "
                + "undo rows already written.",
                SettingClaim.Count, SettingControl.Number,
                Min: 1, Max: SystemSettingsDefaults.RecurrenceMaxGeneratedOccurrences,
                Field: nameof(SystemSettingsUpdate.RecurrenceMaxGeneratedOccurrences),
                Load: (p, dto) => p.SetIntLoaded(
                    "recurrenceMaxGeneratedOccurrences", dto.RecurrenceMaxGeneratedOccurrences),
                Write: (p, req) => req.RecurrenceMaxGeneratedOccurrences =
                    p.IntRequest("recurrenceMaxGeneratedOccurrences"),
                MaxFrom: dto => dto.RecurrenceMaxGeneratedOccurrencesCeiling, LowerOnly: true),
        ]),
        // ── Import & export defaults (issue #434 key 13) ──────────────────────────────────────────
        // A one-row group on purpose: this bound applies to ALL FOUR importers at once, so filing it
        // under any single per-surface group would state, wrongly, that it is surface-scoped. A one-row
        // group is not a new precedent — Data and Storage are both one-row groups already.
        new("Import & export defaults", Icons.Material.Filled.ImportExport,
        [
            new("importMaxSamplesPerSkipReason", "file_upload", "Skipped-row samples per reason",
                "How many example rows an import summary keeps for each reason something was skipped. "
                + "Applies to every importer — contacts, calendars, tasks and journal.",
                SettingClaim.Count, SettingControl.Number, Min: 1, Max: 10000,
                Field: nameof(SystemSettingsUpdate.ImportMaxSamplesPerSkipReason),
                Load: (p, dto) => p.SetIntLoaded("importMaxSamplesPerSkipReason", dto.ImportMaxSamplesPerSkipReason),
                Write: (p, req) => req.ImportMaxSamplesPerSkipReason =
                    p.IntRequest("importMaxSamplesPerSkipReason")),
        ]),
        // ── Accounts (issue #434 key 15) ──────────────────────────────────────────────────────────
        new("Accounts", Icons.Material.Filled.AccountBalance,
        [
            // Min/Max name the SystemSettingsBounds pair rather than restating literals (issue #168):
            // the ceiling IS ListDefaults.MaxFilterArrayLength, the cap on the tagIds filter the
            // section resolves its tags through, so a literal here could drift from the constraint.
            new("accountMaxSmartTagsPerAccount", "sell", "Smart tags per account",
                "How many smart tags one account may carry. The Accounts page reads this value, so the limit "
                + "it shows a user always matches what the server enforces.",
                SettingClaim.Count, SettingControl.Number,
                Min: SystemSettingsBounds.AccountMaxSmartTagsPerAccountMin,
                Max: SystemSettingsBounds.AccountMaxSmartTagsPerAccountMax,
                Field: nameof(SystemSettingsUpdate.AccountMaxSmartTagsPerAccount),
                Load: (p, dto) => p.SetIntLoaded(
                    "accountMaxSmartTagsPerAccount", dto.AccountMaxSmartTagsPerAccount),
                Write: (p, req) => req.AccountMaxSmartTagsPerAccount =
                    p.IntRequest("accountMaxSmartTagsPerAccount")),
        ]),
        // Exchange rates: the two global sync settings save with the page, and
        // Manage currencies links to /currencies, where the sync status, Run now, which currencies are
        // synced and each one's own start date live. The run history is on /background-jobs (issue #410).
        new("Exchange rates", Icons.Material.Filled.CurrencyExchange,
        [
            new("exchangeRateHistoryStartDate", "history", "History start date",
                "Leave empty to start from 1 January of the current year. Rates already fetched are kept when "
                + "this moves later.",
                SettingClaim.Count, SettingControl.Date, Max: ExchangeRateHistoryStartDateRule.MaxLength,
                Field: nameof(SystemSettingsUpdate.ExchangeRateHistoryStartDate),
                Load: (p, dto) => p.SetTextLoaded("exchangeRateHistoryStartDate", dto.ExchangeRateHistoryStartDate),
                Write: (p, req) => req.ExchangeRateHistoryStartDate = p.TextRequest("exchangeRateHistoryStartDate"),
                Placeholder: "1 January this year",
                AllowEmpty: true,
                Rule: ExchangeRateHistoryStartDateRule.Validate),
            new("exchangeRateSyncIntervalHours", "schedule", "Sync interval",
                "How long the sync waits after a finished run before the next one, once every currency has "
                + "caught up. Norges Bank publishes once per business day.",
                SettingClaim.Count, SettingControl.Number,
                Min: SystemSettingsBounds.ExchangeRateSyncIntervalHoursMin,
                Max: SystemSettingsBounds.ExchangeRateSyncIntervalHoursMax,
                Field: nameof(SystemSettingsUpdate.ExchangeRateSyncIntervalHours),
                Load: (p, dto) => p.SetIntLoaded("exchangeRateSyncIntervalHours", dto.ExchangeRateSyncIntervalHours),
                Write: (p, req) => req.ExchangeRateSyncIntervalHours = p.IntRequest("exchangeRateSyncIntervalHours"),
                Unit: "hours"),
            new(ExchangeRateCurrenciesKey, "payments", "Currencies",
                "Choose which currencies are synced and from which date.",
                SettingClaim.None, SettingControl.Action),
        ]),
        // Instrument prices (issue #446). The per-instrument price sources live on /instruments.
        new("Instrument prices", Icons.Material.Filled.CandlestickChart,
        [
            new("instrumentPriceStaleAfterDays", "update", "Stale after",
                "A dashboard price older than this many days is marked stale.",
                SettingClaim.Count, SettingControl.Number,
                Min: SystemSettingsBounds.InstrumentPriceStaleAfterDaysMin,
                Max: SystemSettingsBounds.InstrumentPriceStaleAfterDaysMax,
                Field: nameof(SystemSettingsUpdate.InstrumentPriceStaleAfterDays),
                Load: (p, dto) => p.SetIntLoaded("instrumentPriceStaleAfterDays", dto.InstrumentPriceStaleAfterDays),
                Write: (p, req) => req.InstrumentPriceStaleAfterDays = p.IntRequest("instrumentPriceStaleAfterDays"),
                Unit: "days"),
            new("instrumentPriceSyncIntervalHours", "schedule", "Sync interval",
                "How long the price sync waits after a finished run before the next one, once every source has "
                + "caught up.",
                SettingClaim.Count, SettingControl.Number,
                Min: SystemSettingsBounds.InstrumentPriceSyncIntervalHoursMin,
                Max: SystemSettingsBounds.InstrumentPriceSyncIntervalHoursMax,
                Field: nameof(SystemSettingsUpdate.InstrumentPriceSyncIntervalHours),
                Load: (p, dto) => p.SetIntLoaded("instrumentPriceSyncIntervalHours", dto.InstrumentPriceSyncIntervalHours),
                Write: (p, req) => req.InstrumentPriceSyncIntervalHours = p.IntRequest("instrumentPriceSyncIntervalHours"),
                Unit: "hours"),
            new("instrumentPriceSyncHistoryDays", "history", "History depth",
                "How many days of history a new price source fetches, unless the source names its own start date.",
                SettingClaim.Count, SettingControl.Number,
                Min: SystemSettingsBounds.InstrumentPriceSyncHistoryDaysMin,
                Max: SystemSettingsBounds.InstrumentPriceSyncHistoryDaysMax,
                Field: nameof(SystemSettingsUpdate.InstrumentPriceSyncHistoryDays),
                Load: (p, dto) => p.SetIntLoaded("instrumentPriceSyncHistoryDays", dto.InstrumentPriceSyncHistoryDays),
                Write: (p, req) => req.InstrumentPriceSyncHistoryDays = p.IntRequest("instrumentPriceSyncHistoryDays"),
                Unit: "days"),
        ]),
        // ── News (issue #473 §3.6, §5.4; backend #470 §11.1), after Instrument prices, where the design system
        // orders it. Seven caps on the ordinary claim; the image size limit and the private-host allow-list on the security one — the list decides which addresses on the
        // household's own network the worker may fetch from, so it is not rendered at all for a caller
        // who cannot change it. Every bound is the shared SystemSettingsBounds pair; the image size is
        // also bounded by the server-published ceiling (the object store's file-size limit).
        new("News", Icons.Material.Filled.RssFeed,
        [
            new("newsDefaultFetchIntervalMinutes", "schedule", "Default check interval",
                "How often a feed with no interval of its own is checked for new articles.",
                SettingClaim.Count, SettingControl.Number,
                Min: SystemSettingsBounds.NewsDefaultFetchIntervalMinutesMin, Max: SystemSettingsBounds.NewsDefaultFetchIntervalMinutesMax,
                Field: nameof(SystemSettingsUpdate.NewsDefaultFetchIntervalMinutes),
                Load: (p, dto) => p.SetIntLoaded("newsDefaultFetchIntervalMinutes", dto.NewsDefaultFetchIntervalMinutes),
                Write: (p, req) => req.NewsDefaultFetchIntervalMinutes = p.IntRequest("newsDefaultFetchIntervalMinutes"),
                Unit: "min"),
            new("newsMaxFeeds", "rss_feed", "Maximum feeds",
                "How many feeds the household may follow, archived ones included.",
                SettingClaim.Count, SettingControl.Number,
                Min: SystemSettingsBounds.NewsMaxFeedsMin, Max: SystemSettingsBounds.NewsMaxFeedsMax,
                Field: nameof(SystemSettingsUpdate.NewsMaxFeeds),
                Load: (p, dto) => p.SetIntLoaded("newsMaxFeeds", dto.NewsMaxFeeds),
                Write: (p, req) => req.NewsMaxFeeds = p.IntRequest("newsMaxFeeds")),
            new("newsImageMaxPerArticle", "collections", "Images per article",
                "How many of an article’s images are downloaded and kept. The rest are skipped.",
                SettingClaim.Count, SettingControl.Number,
                Min: SystemSettingsBounds.NewsImageMaxPerArticleMin, Max: SystemSettingsBounds.NewsImageMaxPerArticleMax,
                Field: nameof(SystemSettingsUpdate.NewsImageMaxPerArticle),
                Load: (p, dto) => p.SetIntLoaded("newsImageMaxPerArticle", dto.NewsImageMaxPerArticle),
                Write: (p, req) => req.NewsImageMaxPerArticle = p.IntRequest("newsImageMaxPerArticle")),
            // The security claim (design system): the largest image the worker downloads from an untrusted
            // host is a decoding-surface bound, not a plain count.
            new("newsImageMaxSizeKb", "image", "Image size limit",
                "Largest single image kept. A larger one is skipped and the article keeps its text. Up to "
                + MaxToken + " KB, the most the server accepts.",
                SettingClaim.Security, SettingControl.Number,
                Min: SystemSettingsBounds.NewsImageMaxSizeKbMin, Max: SystemSettingsBounds.NewsImageMaxSizeKbMax,
                Field: nameof(SystemSettingsUpdate.NewsImageMaxSizeKb),
                Load: (p, dto) => p.SetIntLoaded("newsImageMaxSizeKb", dto.NewsImageMaxSizeKb),
                Write: (p, req) => req.NewsImageMaxSizeKb = p.IntRequest("newsImageMaxSizeKb"),
                MaxFrom: dto => Math.Min(SystemSettingsBounds.NewsImageMaxSizeKbMax, dto.NewsImageMaxSizeKbCeiling),
                Unit: "KB"),
            new("newsImageStoreMaxGiB", "photo_library", "Image storage quota",
                "Total space News images may take on the file volume. Once it is full, new articles arrive "
                + "without images.",
                SettingClaim.Count, SettingControl.Number,
                Min: SystemSettingsBounds.NewsImageStoreMaxGiBMin, Max: SystemSettingsBounds.NewsImageStoreMaxGiBMax,
                Field: nameof(SystemSettingsUpdate.NewsImageStoreMaxGiB),
                Load: (p, dto) => p.SetIntLoaded("newsImageStoreMaxGiB", dto.NewsImageStoreMaxGiB),
                Write: (p, req) => req.NewsImageStoreMaxGiB = p.IntRequest("newsImageStoreMaxGiB"),
                Unit: "GiB"),
            new("newsTextStoreMaxGiB", "description", "Text storage quota",
                "Total space the article text and each article’s fetched source may take on the file volume. "
                + "Once it is full, feeds stop storing new articles until it is raised or space is freed.",
                SettingClaim.Count, SettingControl.Number,
                Min: SystemSettingsBounds.NewsTextStoreMaxGiBMin, Max: SystemSettingsBounds.NewsTextStoreMaxGiBMax,
                Field: nameof(SystemSettingsUpdate.NewsTextStoreMaxGiB),
                Load: (p, dto) => p.SetIntLoaded("newsTextStoreMaxGiB", dto.NewsTextStoreMaxGiB),
                Write: (p, req) => req.NewsTextStoreMaxGiB = p.IntRequest("newsTextStoreMaxGiB"),
                Unit: "GiB"),
            new("newsFeedMaxArticlesPerDay", "article", "Articles per feed per day",
                "Upper limit on the new articles one feed may bring in a day (UTC). The feed resumes the next day.",
                SettingClaim.Count, SettingControl.Number,
                Min: SystemSettingsBounds.NewsFeedMaxArticlesPerDayMin, Max: SystemSettingsBounds.NewsFeedMaxArticlesPerDayMax,
                Field: nameof(SystemSettingsUpdate.NewsFeedMaxArticlesPerDay),
                Load: (p, dto) => p.SetIntLoaded("newsFeedMaxArticlesPerDay", dto.NewsFeedMaxArticlesPerDay),
                Write: (p, req) => req.NewsFeedMaxArticlesPerDay = p.IntRequest("newsFeedMaxArticlesPerDay")),
            new("newsFeedMaxImageMbPerDay", "data_usage", "Image MB per feed per day",
                "Upper limit on the image data one feed may download in a day (UTC).",
                SettingClaim.Count, SettingControl.Number,
                Min: SystemSettingsBounds.NewsFeedMaxImageMbPerDayMin, Max: SystemSettingsBounds.NewsFeedMaxImageMbPerDayMax,
                Field: nameof(SystemSettingsUpdate.NewsFeedMaxImageMbPerDay),
                Load: (p, dto) => p.SetIntLoaded("newsFeedMaxImageMbPerDay", dto.NewsFeedMaxImageMbPerDay),
                Write: (p, req) => req.NewsFeedMaxImageMbPerDay = p.IntRequest("newsFeedMaxImageMbPerDay"),
                Unit: "MB"),
            new(NewsAllowListKey, "lan", "Private hosts allowed",
                "Hosts on your own network the worker may fetch feeds from, separated by commas — for example "
                + "nas.lan or 192.168.1.20:8080. Empty allows public hosts only.",
                SettingClaim.Security, SettingControl.Text, Max: NewsPrivateHostAllowListRule.MaxLength,
                Field: nameof(SystemSettingsUpdate.NewsPrivateHostAllowList),
                Load: (p, dto) => p.SetTextLoaded(NewsAllowListKey, dto.NewsPrivateHostAllowList),
                Write: (p, req) => req.NewsPrivateHostAllowList = p.TextRequest(NewsAllowListKey),
                Placeholder: "nas.lan, 192.168.1.20:8080",
                Rule: NewsPrivateHostAllowListRule.Validate,
                AllowEmpty: true,
                StandingAdvisory: NewsAllowListAdvisory,
                HideWithoutClaim: true),
            // The OPML import caps close the group (issue #509 §4.2, AC 20): the size cap under the security claim like
            // the other import size caps, the entry cap a count cap whose ceiling is its shipped default.
            new("newsOpmlMaxImportMegabytes", "sd_storage", "Maximum OPML import file size",
                "Largest OPML file the Feeds page accepts for import. Above ~64 MB, also raise the reverse proxy's body-size limit.",
                SettingClaim.Security, SettingControl.Size,
                Min: SystemSettingsBounds.NewsOpmlMaxImportMegabytesMin, Max: SystemSettingsBounds.NewsOpmlMaxImportMegabytesMax,
                Field: nameof(SystemSettingsUpdate.NewsOpmlMaxImportMegabytes),
                Load: (p, dto) => p.SetIntLoaded("newsOpmlMaxImportMegabytes", dto.NewsOpmlMaxImportMegabytes),
                Write: (p, req) => req.NewsOpmlMaxImportMegabytes = p.IntRequest("newsOpmlMaxImportMegabytes")),
            new("newsOpmlMaxImportEntries", "file_upload", "Maximum feeds per OPML import",
                "Most distinct feeds one OPML file may list. Feeds beyond the feed limit are reported, not added.",
                SettingClaim.Count, SettingControl.Number,
                Min: SystemSettingsBounds.NewsOpmlMaxImportEntriesMin, Max: SystemSettingsBounds.NewsOpmlMaxImportEntriesMax,
                Field: nameof(SystemSettingsUpdate.NewsOpmlMaxImportEntries),
                Load: (p, dto) => p.SetIntLoaded("newsOpmlMaxImportEntries", dto.NewsOpmlMaxImportEntries),
                Write: (p, req) => req.NewsOpmlMaxImportEntries = p.IntRequest("newsOpmlMaxImportEntries")),
        ]),
        // Podcasts (issue #556 §8.2, #559 §3.6): their own group after News. All four are count caps.
        new("Podcasts", Icons.Material.Filled.Podcasts,
        [
            new("podcastAudioStoreMaxGiB", "sd_storage", "Audio storage limit",
                "Total space podcast audio may take on the file volume, including audio removed but not yet deleted. "
                + "Once it is full, downloads wait; nothing is deleted.",
                SettingClaim.Count, SettingControl.Number,
                Min: SystemSettingsBounds.PodcastAudioStoreMaxGiBMin, Max: SystemSettingsBounds.PodcastAudioStoreMaxGiBMax,
                Field: nameof(SystemSettingsUpdate.PodcastAudioStoreMaxGiB),
                Load: (p, dto) => p.SetIntLoaded("podcastAudioStoreMaxGiB", dto.PodcastAudioStoreMaxGiB),
                Write: (p, req) => req.PodcastAudioStoreMaxGiB = p.IntRequest("podcastAudioStoreMaxGiB"),
                Unit: "GiB"),
            new("podcastAudioMaxEpisodeMb", "audio_file", "Largest episode",
                "Largest single episode downloaded to the server. A larger one fails and is not stored. Up to "
                + MaxToken + " MB, the most the server accepts.",
                SettingClaim.Count, SettingControl.Number,
                Min: SystemSettingsBounds.PodcastAudioMaxEpisodeMbMin, Max: SystemSettingsBounds.PodcastAudioMaxEpisodeMbMax,
                Field: nameof(SystemSettingsUpdate.PodcastAudioMaxEpisodeMb),
                Load: (p, dto) => p.SetIntLoaded("podcastAudioMaxEpisodeMb", dto.PodcastAudioMaxEpisodeMb),
                Write: (p, req) => req.PodcastAudioMaxEpisodeMb = p.IntRequest("podcastAudioMaxEpisodeMb"),
                MaxFrom: dto => Math.Min(SystemSettingsBounds.PodcastAudioMaxEpisodeMbMax, dto.PodcastAudioMaxEpisodeMbCeiling),
                Unit: "MB"),
            new("podcastAutoDownloadsPerFeedPerDay", "event_repeat", "Automatic downloads per podcast per day",
                "Upper limit on the new episodes one podcast may download automatically in a day (UTC).",
                SettingClaim.Count, SettingControl.Number,
                Min: SystemSettingsBounds.PodcastAutoDownloadsPerFeedPerDayMin, Max: SystemSettingsBounds.PodcastAutoDownloadsPerFeedPerDayMax,
                Field: nameof(SystemSettingsUpdate.PodcastAutoDownloadsPerFeedPerDay),
                Load: (p, dto) => p.SetIntLoaded("podcastAutoDownloadsPerFeedPerDay", dto.PodcastAutoDownloadsPerFeedPerDay),
                Write: (p, req) => req.PodcastAutoDownloadsPerFeedPerDay = p.IntRequest("podcastAutoDownloadsPerFeedPerDay")),
            new("podcastAutoDownloadMaxUnfetchedPerFeed", "library_music", "Unsaved automatic downloads per podcast",
                "Automatic downloads pause for a podcast while it holds this many automatically downloaded episodes "
                + "that no device has saved yet.",
                SettingClaim.Count, SettingControl.Number,
                Min: SystemSettingsBounds.PodcastAutoDownloadMaxUnfetchedPerFeedMin, Max: SystemSettingsBounds.PodcastAutoDownloadMaxUnfetchedPerFeedMax,
                Field: nameof(SystemSettingsUpdate.PodcastAutoDownloadMaxUnfetchedPerFeed),
                Load: (p, dto) => p.SetIntLoaded("podcastAutoDownloadMaxUnfetchedPerFeed", dto.PodcastAutoDownloadMaxUnfetchedPerFeed),
                Write: (p, req) => req.PodcastAutoDownloadMaxUnfetchedPerFeed = p.IntRequest("podcastAutoDownloadMaxUnfetchedPerFeed")),
        ]),
        // Its own group, as the design system lays it out (system-settings-data.js): a property is not a
        // kind of contract, and an administrator looking for the property cap looks under Properties.
        new("Properties", Icons.Material.Filled.HomeWork,
        [
            new("propertyMaxSmartTagsPerProperty", "sell", "Smart tags per property",
                $"How many smart tags one property may carry. Capped at {SystemSettingsBounds.PropertyMaxSmartTagsPerPropertyMax}, the most tags one transactions "
                + "query can filter on. The Properties page reads this value, so the limit it shows always "
                + "matches what the server enforces.",
                SettingClaim.Count, SettingControl.Number,
                Min: SystemSettingsBounds.PropertyMaxSmartTagsPerPropertyMin,
                Max: SystemSettingsBounds.PropertyMaxSmartTagsPerPropertyMax,
                Field: nameof(SystemSettingsUpdate.PropertyMaxSmartTagsPerProperty),
                Load: (p, dto) => p.SetIntLoaded(
                    "propertyMaxSmartTagsPerProperty", dto.PropertyMaxSmartTagsPerProperty),
                Write: (p, req) => req.PropertyMaxSmartTagsPerProperty =
                    p.IntRequest("propertyMaxSmartTagsPerProperty")),
        ]),
    ];

    private bool Editable(SettingClaim claim) => claim switch
    {
        SettingClaim.Security => _hasSecurityUpdate,
        SettingClaim.Count => _hasCountUpdate,
        _ => true,
    };

    internal const string ExchangeRateCurrenciesKey = "exchangeRateCurrencies";

    /// <summary>Replaced in the help line by the saved processor name (issue #434 §8).</summary>
    internal const string ProcessorToken = "{processor}";

    /// <summary>Replaced in a row's description by its effective maximum (<see cref="MaxFor"/>).</summary>
    internal const string MaxToken = "{max}";

    internal const string PromptPlaceholder = "Optional. For example: Lines starting with VIPPS* are Vipps payments.";

    private bool IsRowDisabled(SettingItem item) =>
        !Editable(item.Claim) || _phase != Phase.Ready || _isSaving;

    private bool Matches(SettingItem item, string group) => Matches(item, group, _search);

    /// <summary>A <see cref="SettingItem.HideWithoutClaim"/> row exists only for a caller who can edit it.</summary>
    private bool Shown(SettingItem item) => !item.HideWithoutClaim || Editable(item.Claim);

    /// <summary>
    /// The search predicate, as a pure function so <see cref="FaultSummaryText"/> can count the rows a
    /// filter hides without a rendered page. Advisory text is deliberately NOT matched: adding it would
    /// only help an administrator who happened to search words appearing in it, and the fault surfaces
    /// below are filter-aware by construction instead.
    /// </summary>
    internal static bool Matches(SettingItem item, string group, string search) =>
        string.IsNullOrWhiteSpace(search) ||
        item.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        item.Description.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        group.Contains(search, StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<SettingSection> VisibleSections =>
        Sections
            .Select(s => s with { Items = s.Items.Where(i => Shown(i) && Matches(i, s.Group)).ToList() })
            // A section survives on its CREDENTIALS alone (issue #445). Searching "SMTP password"
            // filters away every plaintext row in Email, and dropping the section there would hide the
            // one row that matched — the search would answer "no settings match" for a term naming a
            // field that is on the page.
            .Where(s => s.Items.Count > 0 || SecretsIn(s).Count > 0)
            .ToList();
}
