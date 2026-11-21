using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.Dtos.Application;
using Odyssey.Client.Components;
using Odyssey.Dtos;

namespace Odyssey.Client.Pages;

public partial class Settings
{
    // ── The credential fields (issue #444 §3, regrouped by the design update in 2f61476b) ────────
    //
    // A SEPARATE collection from SettingItem, not new entries in `Sections`. A secret has no Field, no
    // Load and no Write — it commits through its own per-key endpoint rather than the page's
    // whole-resource PUT — so folding it into the catalogue would force
    // SettingsCatalogueTests.Every_stored_row_declares_a_field_a_load_and_a_write to grow an
    // exemption beyond the tile-only SettingControl.Action. Keeping it separate means that test never sees these
    // rows and no existing guard is weakened.
    //
    // They still RENDER inside the section their Group names, joined at render time. That is the point
    // of the split: the two collections have different lifecycles, but one page layout.

    /// <summary>
    /// One credential row's client-authored presentation. The status endpoint carries only key, state
    /// and attribution — no title, description or icon — so those must be authored here, which is why
    /// this catalogue is necessarily non-empty even in Production.
    /// </summary>
    /// <param name="Consequence">
    /// What is not working while the row is <em>not set</em>. Every one of these rows starts unset
    /// after the upgrade that introduces it — a secret is deliberately never adopted from configuration
    /// — so the gap is a designed state, not an edge case, and the page has to say what it costs.
    /// </param>
    /// <param name="Affects">The same sentence for the <em>unreadable</em> case, where the value is
    /// present and the consumer is failing right now.</param>
    /// <param name="Group">
    /// The section this credential renders in. Secrets live in their SUBJECT cards, not in a
    /// Credentials group of their own: the API key beside the destination it is sent to and the
    /// switch that decides whether anything is sent, the relay pair beside the from address it
    /// authenticates, the pseudonymisation secret beside the export carrying the same records. It
    /// must name an existing <see cref="Sections"/> group — a guard test asserts that, because a typo
    /// would silently drop the row off the page rather than misplace it.
    /// </param>
    internal sealed record SecretItem(
        string Key,
        string Group,
        string Icon,
        string Title,
        string Description,
        bool IsDerivationKey,
        string Consequence,
        string Affects);

    internal static readonly IReadOnlyList<SecretItem> SecretCatalogue =
    [
        new(SecretSettingKeys.DiagnosticsSelfTest, "Security", "science", "Diagnostics self-test credential",
            "A test-only credential that proves encrypted secret storage works on this deployment. "
            + "No feature reads it, and it is not registered in Production.",
            IsDerivationKey: false,
            Consequence:
            "Nothing. No feature reads this credential — it exists so encrypted storage can be "
            + "exercised on a deployment without touching a real one.",
            Affects: "Nothing is affected; no feature reads this credential."),

        // ── Issue #445, in shipping order ──────────────────────────────────────────────────────
        //
        // Titles, descriptions and icons are authored HERE and nowhere else: the status endpoint
        // deliberately carries no presentation fields, so there is nothing to join them to server-side.
        //
        // So is the GROUP. Each row sits in the card that answers questions about it: the API key
        // where an administrator asks "is analysis on, where does it go, what authenticates it"; the
        // relay pair and the hash key beside the from address and the send limits they authenticate
        // and count; the pseudonymisation secret beside the export carrying the records it
        // pseudonymises, because account deletion is a data-lifecycle act with no feature card of its
        // own. The server neither knows nor needs to: the key's TYPE is what marks a secret, so
        // grouping stays a presentation choice.

        new(SecretSettingKeys.FileAnalysisApiKey, "File analysis", "vpn_key", "File analysis API key",
            "Sent as x-api-key on every analysis request, to the host set as the provider base URL. "
            + "A replacement takes effect on the next request without a restart. If the row cannot be "
            + "read, analysis fails and records a credential problem — it never falls back to a "
            + "configured value.",
            IsDerivationKey: false,
            Consequence:
            "Every document analysis fails and is recorded as a failed job. Nothing is transferred "
            + "and nothing is lost; the feature is unavailable until a key is entered.",
            Affects: "Document analysis is failing on every job."),

        new(SecretSettingKeys.EmailUsername, "Email", "person", "SMTP username",
            "Authenticates the relay connection, together with the SMTP password. The pair is used or "
            + "not used together — a stored username beside an unset password is a half-configured "
            + "credential, and the send is skipped rather than attempted unauthenticated.",
            IsDerivationKey: false,
            Consequence:
            "The relay connection is made without authenticating. That is a legitimate configuration "
            + "for a relay that accepts unauthenticated mail on a trusted network, and a silent "
            + "failure for every other kind.",
            Affects: "Transactional mail is not sending."),

        new(SecretSettingKeys.EmailPassword, "Email", "password", "SMTP password",
            "Authenticates the relay connection. A human-chosen password at a third-party provider. "
            + "Only printable ASCII can be stored. A relay password outside that range is rejected "
            + "before it is sent, with the constraint named — not returned as a bare 400.",
            IsDerivationKey: false,
            Consequence:
            "Password resets, email confirmations and every other transactional mail are attempted "
            + "unauthenticated and will be rejected by any relay that requires a login.",
            Affects: "Transactional mail is not sending — every send is logged and skipped."),

        new(SecretSettingKeys.EmailRecipientHashKey, "Email", "fingerprint", "Recipient hash key",
            "Derives the digests the send throttle counts per recipient, so a log never carries an "
            + "address. Replacing it breaks nothing already recorded, but digests written before the "
            + "change stop correlating with the ones after it.",
            IsDerivationKey: true,
            Consequence:
            "Unset is a supported configuration, not a fault: a random key is generated per process, "
            + "so throttle digests correlate within one process's lifetime and not across a restart.",
            Affects:
            "Throttle digests have fallen back to a per-process key, so log correlation is broken "
            + "across restarts."),

        new(SecretSettingKeys.WeatherFrostClientId, "Weather", "vpn_key", "Frost client ID",
            "Identifies this instance to MET Norway's Frost service, which serves measured weather "
            + "history. Sent to frost.met.no only. Register one for free at frost.met.no. Forecasts do "
            + "not need it.",
            IsDerivationKey: false,
            Consequence:
            "Measured history is not collected. That is a supported configuration: forecasts are "
            + "unaffected, and history starts the next time a client ID is set.",
            Affects:
            "Measured history is not being collected — every Frost fetch is skipped and an error is "
            + "logged hourly."),

        new(SecretSettingKeys.LegalPseudonymizationSecret, "Data", "gavel", "Pseudonymisation secret",
            "HMACs the subject of a consent record when an account is deleted, so acceptance stays "
            + "attributable without holding an identity. There is no provider to re-issue this from: "
            + "lose it and every row already pseudonymised with it is permanently un-re-derivable — "
            + "the property GDPR Art. 7(1) consent attribution depends on. Export the value before "
            + "replacing or clearing it.",
            IsDerivationKey: true,
            Consequence:
            // Leads with the environment-independent fact, because the Production and non-Production
            // outcomes differ and stating one of them first reads as a flat claim the next sentence
            // then contradicts.
            "Account deletion cannot pseudonymise consent records. Outside Production a fixed "
            + "development value is substituted; in Production the value is required.",
            Affects: "Account deletion cannot pseudonymise consent records."),
    ];

    /// <summary>
    /// The rows to render: the INTERSECTION of the static catalogue with the keys the status endpoint
    /// actually returned, then the page's search filter.
    ///
    /// <para>
    /// The intersection is what keeps a non-Production key off a Production page. The client cannot
    /// decide that itself — <c>Odyssey.Client</c> never reads <c>HostEnvironment.Environment</c>,
    /// nothing sets the <c>blazor-environment</c> header, and a WASM-side environment check would be
    /// wrong in dev <em>and</em> Production. The server filters its registry by environment; the page
    /// simply renders what came back, so no environment logic is duplicated client-side and the group
    /// self-hides when the intersection is empty.
    /// </para>
    /// </summary>
    private IReadOnlyList<SecretItem> VisibleSecrets =>
        !_hasSecurityUpdate
            ? []
            : SecretCatalogue
                .Where(item => _secretStatuses.ContainsKey(item.Key))
                .Where(item => SecretMatches(item, item.Group, _search))
                .ToList();

    /// <summary>
    /// The credentials that render inside one section's grid, after every gate above. Empty for most
    /// sections, which is the point: a secret sits in the card that answers questions about it rather
    /// than in a group of its own.
    /// </summary>
    private IReadOnlyList<SecretItem> SecretsIn(SettingSection section) =>
        VisibleSecrets.Where(item => item.Group == section.Group).ToList();

    /// <summary>Mirrors <see cref="Matches(SettingItem, string, string)"/> for the secret catalogue.</summary>
    internal static bool SecretMatches(SecretItem item, string group, string search) =>
        string.IsNullOrWhiteSpace(search) ||
        item.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        item.Description.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        group.Contains(search, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A STABLE id for one credential row's title element, so the header signal can move focus to the
    /// row it names. The component generates its own per-instance id otherwise, which the page cannot
    /// predict.
    /// </summary>
    internal static string SecretAnchorId(string key) => $"ss-cred-{key}";

    /// <summary>
    /// The header's severity rollup, in the design system's order: credentials this server cannot
    /// decrypt (issue #445, at <c>Error</c>), then storage health (issue #346), then mail this
    /// deployment has never configured (issue #8, at <c>Information</c>) — outages first, an
    /// unfinished setup last.
    ///
    /// <para>
    /// The credential half is built from the INTERSECTION, like the rows themselves: a key the server
    /// reports but the catalogue does not describe has no title to name and no row to jump to. The
    /// credential and mail halves are gated on the write claim for the same reason the group is — there
    /// is nothing a read-only caller could do about either. Storage is gated on file-storage.read
    /// instead: its fix is never a Save, and an administrator reading the page needs to see that
    /// erasure has stopped.
    /// </para>
    ///
    /// <para>
    /// Deliberately separate from <see cref="BlockingProblems"/>. That list explains a disabled
    /// <b>Save changes</b>; this one reports conditions a Save cannot fix, and folding the two together
    /// would misattribute both.
    /// </para>
    /// </summary>
    private IReadOnlyCollection<PageHeaderProblem>? SetupProblems
    {
        get
        {
            if (_phase != Phase.Ready)
            {
                return null;
            }

            var problems = new List<PageHeaderProblem>();

            problems.AddRange(UnreadableSecrets.Select(item => new PageHeaderProblem
            {
                Severity = PageHeaderSeverity.Error,
                Lead = $"{item.Title} cannot be decrypted",
                // The rollup NAMES THE CARD (issue #445, design update 2f61476b). The rows are
                // scattered across the page by subject now, so "SMTP password" alone does not tell
                // a reader where to look, and the jump target may be far below the fold. The Fix
                // action still goes there directly; this is for everyone reading rather than
                // clicking.
                Message =
                    $"{item.Affects} The value is stored but this instance's encryption key ring "
                    + "cannot open it, and nothing falls back to a configured value. Clear the row "
                    + "and enter the credential again.",
                Where = $"In {item.Group}.",
                ViewLabel = "Fix",
                OnView = EventCallback.Factory.Create(this, () => JumpToElementAsync(SecretAnchorId(item.Key))),
            }));

            if (StorageProblem is { } storage)
            {
                problems.Add(storage);
            }

            // Mail unconfigured (issue #8 G8), at INFORMATION rather than Error: a deployment that has
            // not configured mail yet is incomplete, not broken, and this rollup's other entries are
            // real outages. Gated on the write claim, so only an administrator who can actually fix the
            // state sees it.
            //
            // It reports what an administrator LOOKING AT THIS PAGE cannot otherwise see. It does not
            // cover the case that actually strands a deployment — an operator who deploys, never
            // visits /settings, and later loses the bootstrap password — which is documentation's job
            // (docs/deployment.md) and was the reason a startup warning was considered and declined.
            if (_hasSecurityUpdate && MailUnconfigured)
            {
                problems.Add(new PageHeaderProblem
                {
                    Severity = PageHeaderSeverity.Information,
                    Lead = "Transactional mail is not configured",
                    Message =
                        "Confirmation and password-reset messages are logged and skipped, so no account "
                        + "can be confirmed or recovered until an SMTP host is set.",
                    // Computed from the catalogue rather than a literal: the group a row sits in is a
                    // presentation choice that has moved once already.
                    Where = $"In {GroupOf(SmtpHostKey)}.",
                    ViewLabel = "Fix",
                    OnView = EventCallback.Factory.Create(this, () => JumpToElementAsync(TitleId(SmtpHostKey))),
                });
            }

            return problems.Count > 0 ? problems : null;
        }
    }

    /// <summary>
    /// The credentials the rollup reports as undecryptable — the intersection with the catalogue, and
    /// only for a caller holding the write claim.
    /// </summary>
    private IEnumerable<SecretItem> UnreadableSecrets =>
        !_hasSecurityUpdate
            ? []
            : SecretCatalogue.Where(item => _secretStatuses.TryGetValue(item.Key, out var status)
                && status.State == SecretSettingState.Unreadable);

    /// <summary>
    /// The rollup's button label, named for its most serious kind of entry rather than a generic
    /// "Attention" (Odyssey Design System · SystemSettings): Credentials when a credential cannot be
    /// decrypted, Storage when storage health has a condition, otherwise Email — the only entry left
    /// is unconfigured mail. Its order is the rollup's own order, so the label always names the first
    /// row of the panel it opens.
    /// </summary>
    internal static string SetupProblemsLabelFor(bool unreadableSecret, bool storageProblem) =>
        unreadableSecret ? "Credentials" : storageProblem ? "Storage" : GroupOf(SmtpHostKey);

    private string SetupProblemsLabel =>
        SetupProblemsLabelFor(UnreadableSecrets.Any(), StorageProblem is not null);

    // ── The destructive save gate (issue #8 §3, G4/G7) ───────────────────────────────────────────
    //
    // Two rows on this page have a consequence beyond their own value: changing the SMTP host, or
    // turning STARTTLS off, CLEARS the stored SMTP username and password on the server, in the same
    // transaction as the settings write. That is the security control, not a side effect to be
    // designed away — a credential entered for one relay must never be presented to another, and one
    // entered for an encrypted transport must never be replayed over a cleartext one.
    //
    // It attaches to the page's BATCH Save, because there is no per-field save to hang it on: every
    // catalogue row's Write delegate composes one SystemSettingsUpdate and one PUT. So Confirm
    // submits the whole batch exactly as an unguarded Save would, and CANCEL DISCARDS NOTHING —
    // splitting the batch would create partial saves this page has never had, and a Cancel that
    // silently dropped every pending edit would be a worse surprise than the one being guarded.

    /// <summary>The setting keys whose stored credential a page save is about to clear.</summary>
    internal const string SmtpHostKey = "emailSmtpHost";

    internal const string SmtpPortKey = "emailSmtpPort";

    internal const string StartTlsKey = "emailUseStartTls";

    /// <summary>
    /// The credentials this save clears, named as the Credentials rows name them. Resolved from
    /// <see cref="SecretCatalogue"/> rather than written twice, so the dialog cannot name a row by a
    /// title the page does not use.
    /// </summary>
    private static IReadOnlyList<string> RelayCredentialTitles =>
    [
        .. SecretCatalogue
            .Where(item => item.Key is SecretSettingKeys.EmailUsername or SecretSettingKeys.EmailPassword)
            .Select(item => item.Title),
    ];

    private bool _clearConfirmed;

    private bool _clearDialogOpen;

    private OdsCredentialClearReason _clearReason = OdsCredentialClearReason.Host;

    /// <summary>
    /// The control to return focus to when the confirmation closes — by Confirm, Cancel or Escape.
    /// Neither <c>OdsModal</c> nor <c>OdsFormDialog</c> restores focus, so without this the reader is
    /// returned to the top of the document after a dialog they did not open deliberately (AC 20).
    /// </summary>
    private string? _clearReturnFocusId;

    /// <summary>
    /// Which of the two triggers this save trips, or null when it trips neither.
    ///
    /// <para>
    /// <strong>Both are directional, and both directions matter.</strong> The host warns only when it
    /// moves to a different NON-EMPTY value — clearing the host turns mail off, so there is no new
    /// relay for the credential to reach, and re-saving the same host is not a change at all. STARTTLS
    /// warns only on true → false; turning it back on is a strengthening. These mirror
    /// <c>SystemSettingsService.RelayCredentialClearReason</c> exactly, and a drift in either
    /// direction is a defect: warning without clearing teaches the reader to dismiss the dialog, and
    /// clearing without warning destroys a credential unannounced.
    /// </para>
    ///
    /// <para>
    /// The host comparison is against the SAVED value the server last returned, which is already
    /// canonical (trimmed, lowercased, trailing dot stripped) — the same normalisation
    /// <c>EmailSmtpHostRule</c> applies on the way in. <c>IsDirty</c> compares the trimmed draft
    /// against it, so a case-only edit does not read as a change here or on the server.
    /// </para>
    /// </summary>
    private OdsCredentialClearReason? CredentialClearTrigger
    {
        get
        {
            // Host and port share the Host variant: both move the ENDPOINT the credential is
            // presented to, which is the same threat and the same sentence. STARTTLS is its own
            // variant because the threat is different — the credential going over the wire in clear.
            if (IsClearingRow(SmtpHostKey, () => GetText(SmtpHostKey).Trim().Length > 0)
                || IsClearingRow(SmtpPortKey, () => true))
            {
                return OdsCredentialClearReason.Host;
            }

            if (IsClearingRow(StartTlsKey, () => !GetBool(StartTlsKey)))
            {
                return OdsCredentialClearReason.StartTls;
            }

            return null;
        }
    }

    /// <summary>
    /// Whether one row is dirty IN THE CLEARING DIRECTION and the caller can actually edit it.
    ///
    /// <para>
    /// The <c>Editable</c> check is not belt-and-braces: a row the caller cannot edit sends
    /// <see langword="null"/> on save and so cannot change anything server-side, and warning about a
    /// consequence that will not happen teaches the reader to dismiss the dialog.
    /// </para>
    /// </summary>
    private bool IsClearingRow(string key, Func<bool> inClearingDirection) =>
        AllItems.FirstOrDefault(item => item.Key == key) is { } row
        && Editable(row.Claim)
        && IsDirty(row)
        && inClearingDirection();

    /// <summary>
    /// Whether ANY of the three rows is dirty in its clearing direction — the condition each of those
    /// rows shows its own on-row advisory under, so the consequence is visible while editing rather
    /// than only in the dialog at save time (the design system's own rationale for the row advisory).
    /// </summary>
    private bool WillClearRelayCredential => CredentialClearTrigger is not null;

    /// <summary>
    /// The relay ENDPOINT, as the server last returned it and as the draft would leave it — what the
    /// dialog names as "through X instead of Y".
    ///
    /// <para>
    /// The port is appended only when it is the thing that changed. Showing <c>host:587</c> on an
    /// ordinary host change would be noise, and showing a bare host on a port-only change would name
    /// the same value on both sides of "instead of" — which reads as a bug in the dialog rather than
    /// as the change it is describing.
    /// </para>
    /// </summary>
    private string SavedSmtpEndpoint => Endpoint(SavedSmtpHost, SavedSmtpPort);

    private string DraftSmtpEndpoint => Endpoint(GetText(SmtpHostKey).Trim(), GetInt(SmtpPortKey));

    private string SavedSmtpHost =>
        _texts.TryGetValue(SmtpHostKey, out var state) ? state.Saved : string.Empty;

    private int? SavedSmtpPort => _ints.TryGetValue(SmtpPortKey, out var state) ? state.Saved : null;

    private string Endpoint(string host, int? port)
    {
        if (host.Length == 0)
        {
            return string.Empty;
        }

        var portChanged = AllItems.FirstOrDefault(item => item.Key == SmtpPortKey) is { } row && IsDirty(row);
        return portChanged && port is { } value ? $"{host}:{value}" : host;
    }

    private void OpenClearConfirmation(OdsCredentialClearReason reason)
    {
        _clearReason = reason;
        _clearReturnFocusId = TitleId(TriggeringRowKey(reason));
        _clearDialogOpen = true;

        // Pinned by a test, not left to the implementer (AC 19). It routes through the page's existing
        // OdsLiveAnnouncer rather than giving the dialog a live region of its own: a region inserted
        // into the DOM at the same moment as its content is frequently not announced at all.
        Announce(ClearConfirmationAnnouncement(reason));
    }

    /// <summary>
    /// What is announced when the confirmation opens. It names the consequence, not the dialog: a
    /// reader who hears "a dialog opened" has been told nothing they cannot see, and this dialog exists
    /// entirely to report something they did not ask for.
    /// </summary>
    /// <summary>
    /// The row focus returns to. For the Host variant that is whichever of host/port is actually
    /// dirty — returning focus to the host field after a port-only change would land the reader on a
    /// value they did not touch.
    /// </summary>
    private string TriggeringRowKey(OdsCredentialClearReason reason)
    {
        if (reason == OdsCredentialClearReason.StartTls)
        {
            return StartTlsKey;
        }

        return IsClearingRow(SmtpHostKey, () => GetText(SmtpHostKey).Trim().Length > 0)
            ? SmtpHostKey
            : SmtpPortKey;
    }

    internal static string ClearConfirmationAnnouncement(OdsCredentialClearReason reason) =>
        reason == OdsCredentialClearReason.StartTls
            ? "Confirmation required: turning STARTTLS off clears the stored SMTP username and password."
            : "Confirmation required: changing the SMTP host clears the stored SMTP username and password.";

    /// <summary>
    /// Confirm. Marks the batch approved and re-enters <see cref="Save"/>, which then runs exactly as
    /// an unguarded save would — same request, same rows, same everything.
    /// </summary>
    private async Task<bool> ConfirmClearAsync()
    {
        _clearConfirmed = true;
        _clearDialogOpen = false;

        // Focus first, then save: Save() awaits a network round trip and a confirmation delay, and a
        // reader left with focus on a removed dialog for that whole window has effectively lost it.
        await RestoreFocusAfterClearDialogAsync();
        await Save();

        // Always true — OdsFormDialog closes on true, and the dialog is already closed above. A false
        // here would reopen the gate on a save that has already been approved.
        return true;
    }

    /// <summary>
    /// Cancel and Escape, which are the same thing. Nothing is discarded: every pending edit stays,
    /// the page stays dirty, and the reader can put the offending value back by hand and save the rest.
    /// </summary>
    private async Task OnClearDialogOpenChanged(bool open)
    {
        _clearDialogOpen = open;
        if (!open)
        {
            _clearConfirmed = false;
            await RestoreFocusAfterClearDialogAsync();
        }
    }

    private async Task RestoreFocusAfterClearDialogAsync()
    {
        if (_clearReturnFocusId is not { } targetId)
        {
            return;
        }

        _clearReturnFocusId = null;
        StateHasChanged();

        try
        {
            await JS.InvokeVoidAsync("odsFocusById", targetId);
        }
        catch
        {
            // The row is no longer rendered (a filter changed under the dialog) — non-fatal. The page
            // is still usable; only the focus courtesy is lost.
        }
    }

    // ── The origin-mismatch hint (issue #8 §3.3, §10.2) ──────────────────────────────────────────

    internal const string ClientBaseUrlKey = "emailClientBaseUrl";

    /// <summary>
    /// A hint when the saved client base URL points somewhere other than the origin this browser is
    /// on. <see langword="null"/> the rest of the time.
    ///
    /// <para>
    /// <strong>A hint, never a block.</strong> It does not set <c>aria-invalid</c>, does not appear in
    /// <c>ErrorFor</c>, does not count toward <c>BlockingSummary</c> and does not disable Save. An
    /// operator may legitimately configure a public URL from an internal hostname, or set it ahead of
    /// a DNS cutover, and refusing either would be wrong.
    /// </para>
    ///
    /// <para>
    /// <strong>What it is actually for.</strong> This field is the weakest point in the mail feature
    /// and the one thing clearing a credential cannot protect: no credential is involved, so anyone who
    /// can change it receives a password-reset token for any address they know — another
    /// administrator's included. This is the only control that narrows that at zero cost to legitimate
    /// use. An attacker working through a hijacked session is browsing the REAL origin, so pointing
    /// the value elsewhere trips the hint and lands in the audit log.
    /// </para>
    ///
    /// <para>
    /// Compared as ORIGINS — scheme, host and non-default port — not as hosts: a scheme or port that
    /// differs on the same host is worth flagging too. The saved value is used rather than the draft,
    /// so it reports what is configured rather than commenting on every keystroke.
    /// </para>
    /// </summary>
    private string? OriginMismatchHint
    {
        get
        {
            if (_dto is null || !_hasSecurityUpdate)
            {
                return null;
            }

            if (EmailClientBaseUrlRule.Origin(_dto.EmailClientBaseUrl) is not { } saved)
            {
                // Empty or unparseable. Empty is the healthy unconfigured state and has its own
                // signal; unparseable is a projection fault, which owns this slot instead.
                return null;
            }

            if (EmailClientBaseUrlRule.Origin(Nav.BaseUri) is not { } browsing
                || string.Equals(saved, browsing, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return $"This differs from the address you are using now ({browsing}). Confirmation and "
                + "password-reset links will point at the value saved here.";
        }
    }

    // ── The unconfigured-mail signal (issue #8 G8) ───────────────────────────────────────────────

    /// <summary>
    /// Whether this deployment has no relay configured, so nothing transactional is being sent.
    ///
    /// <para>
    /// Read off the SAVED value, never the draft: an administrator halfway through typing a host has
    /// not fixed anything yet, and a signal that vanished as the first character landed would be
    /// reporting the edit rather than the state.
    /// </para>
    /// </summary>
    private bool MailUnconfigured =>
        _dto is not null && string.IsNullOrWhiteSpace(_dto.EmailSmtpHost);

    /// <summary>The section a catalogue row renders in. Falls back to the row's own key, which is a
    /// visible oddity rather than a silent mispointing if a row is ever removed.</summary>
    private static string GroupOf(string key) =>
        Sections.FirstOrDefault(section => section.Items.Any(item => item.Key == key))?.Group ?? key;

    private SecretSettingStatusDto StatusFor(SecretItem item) =>
        _secretStatuses.TryGetValue(item.Key, out var status)
            ? status
            : new SecretSettingStatusDto { Key = item.Key, State = SecretSettingState.NotSet };

    /// <summary>
    /// Fetches the statuses. A failure is not a page failure: the group simply does not render, which
    /// is the same outcome a Production deployment gets, and the plaintext settings the administrator
    /// came for stay usable.
    /// </summary>
    private async Task LoadSecretStatusesAsync()
    {
        _secretStatuses.Clear();

        if (!_hasSecurityUpdate)
        {
            return;
        }

        var result = await SecretSettings.GetAsync();
        if (!result.IsSuccess || result.Value is not { } statuses)
        {
            return;
        }

        foreach (var status in statuses)
        {
            _secretStatuses[status.Key] = status;
        }
    }

    /// <summary>
    /// Refreshes the statuses after a row commits. The row's own write already returned <c>204</c>, so
    /// this is only about picking up the new attribution — a failure here leaves the previous status
    /// showing rather than blanking the group.
    /// </summary>
    private async Task OnSecretChangedAsync()
    {
        await LoadSecretStatusesAsync();
        StateHasChanged();
    }
}
