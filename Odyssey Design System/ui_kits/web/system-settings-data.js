/* =============================================================
   System settings — the runtime-configurable catalogue.
   Split out of SystemSettings.jsx once the catalogue reached 42 rows across
   twelve sections; the page component owns states and rendering, this file
   owns the declaration of what exists.

   One row = one declaration. `claim` is the write claim the row needs:
     'security' → system-settings.security.update  (perimeter toggles, SIZE
                  caps, sender identity, mail throttle, the disclosure strings)
     'count'    → system-settings.update            (volume caps, windows)
     null       → not a system-settings write (the data export action)

   `type` picks the control:
     switch   → OdsSwitch
     number   → OdsNumberField
     size     → OdsNumberField with unit="MB"
     percent  → OdsNumberField, unit="%", stored as a 0.0–1.0 FRACTION and
                entered as a whole percent (see the page's controlFor)
     capacity → OdsCapacityField (a finite number OR "No limit")
     text     → OdsTextInputField, in the row's FOOTER well
     export   → a Button

   `ceiling` marks a tighten-only row: the value can be lowered but never
   raised, because a request-DTO annotation or a transport limit enforces the
   same number earlier in the pipeline. The description says why; the field's
   helper line carries the resulting range.

   `roundTrip` on a section: export cap must not exceed import cap.
   `extra` on a row: a second sentence on its helper line, for obligation or
   provenance that belongs to that value (what it discloses, which Article
   applies, whether Odyssey can verify it).
   `floor` marks a raise-only row: the value can be raised but never lowered,
   because the thing it bounds FAILS OPEN when it runs out — a smaller number
   weakens the control instead of tightening it.
   `advise` = { above, cost }: above that value the row carries a non-blocking
   ADVISORY naming what the raise costs (memory, payload, third-party spend).
   It never blocks Save — the value is legal, it just is not free.
   `adviseWhenOn`: a switch whose ON state carries an advisory naming what turning
   it on lets happen (the file-analysis kill switch).
   `adviseOffDefault`: the shipped default a text value is compared against — any
   other value carries an advisory saying what the change does and does not
   affect (records already written keep what they ran under).
   `checkBaseUrl`: the provider base URL — blocking shape validation (absolute
   https, no userinfo, query, fragment or path) plus a host-only advisory when the
   destination is not the shipped default.
   ============================================================= */

// The base URL's shipped default. The base-URL row's advisory fires when the
// stored host differs from this one, and the processor row is checked against
// the host of the STORED value (`fileAnalysisBaseUrl`) rather than a compiled
// constant — advisory only: a strict match would reject legitimate gateway
// deployments, a loose one would pass evil-anthropic.example.com. It defends
// against a careless edit, not a determined one, so a mismatch is a warning
// and never blocks Save.
const SS_DEFAULT_BASE_URL = 'https://api.anthropic.com';
const SS_DEFAULT_MODEL = 'claude-sonnet-5';
// Kept for back-compat with anything still reading the old constant name.
const SS_PROCESSOR_HOST = 'api.anthropic.com';

const SS_GROUPS = [
  {
    group: 'Security', icon: 'shield',
    rows: [
      { key: 'requireTwoFactor', type: 'switch', claim: 'security', icon: 'verified_user',
        title: 'Require two-factor authentication',
        desc: 'Every user must set up an authenticator app to sign in. Stored only — not enforced yet.',
        meta: { by: 'Priya Anand', on: '6 Aug 2026, 14:22' } },
      { key: 'registrationRequireAdminApproval', type: 'switch', claim: 'security', icon: 'how_to_reg',
        title: 'Require admin approval for new registrations',
        desc: 'New sign-ups stay disabled until an administrator approves the account.',
        meta: { by: 'Marcus Reyes', on: '2 Aug 2026, 09:10' } },
      { key: 'emailRequireConfirmation', type: 'switch', claim: 'security', icon: 'mark_email_read',
        title: 'Require email confirmation before sign-in',
        desc: 'Users must confirm their email address before their first sign-in is allowed.',
        meta: { by: 'Priya Anand', on: '19 Jul 2026, 16:47' } },
    ],
  },
  {
    group: 'File analysis', icon: 'smart_toy',
    // The switch and the destination come FIRST — they frame everything below
    // them: whether any document leaves at all, which model reads it, and where
    // it is sent. All three moved out of appsettings.json into this store
    // (issue #439), so an operator can stop transfers, repoint a deployment or
    // change model without a redeploy.
    // Then the four processor-disclosure rows. Each carries its own share of the
    // legal weight in `extra` — what is shown at the consent gate, what is
    // recorded on the job, which Article applies, whether Odyssey can verify it
    // — rather than a shared note above the set, so a reader looking at one
    // value gets that value's obligation on its own helper line.
    rows: [
      // Icon note: the natural choice is `power_settings_new`, which is not yet
      // proven against the frozen self-hosted Material Icons snapshot — an
      // unresolved name ligates its longest known prefix and renders the rest as
      // literal text. Until it is verified once as a single glyph, this row uses
      // `verified_user` from the proven list.
      { key: 'fileAnalysisEnabled', type: 'switch', claim: 'security', icon: 'verified_user',
        title: 'AI document analysis',
        desc: 'When off, no document is sent for analysis and every analysis endpoint answers 503. Turning it on does not by itself transfer anything: each analysis still requires the user\u2019s per-document consent.',
        extra: 'Read live on every request rather than from the settings cache, so turning it off stops the next transfer instead of the next one after a 30-second window. Every change is written to the audit log.',
        adviseWhenOn: true, meta: { by: 'Priya Anand', on: '18 Aug 2026, 08:41' } },
      { key: 'fileAnalysisModel', type: 'text', claim: 'security', icon: 'badge',
        title: 'Model',
        desc: 'The model each analysis is sent to, and the model recorded against it. Analyses already completed keep the model they ran under.',
        extra: 'A stored value that cannot be used makes analysis refuse rather than fall back to the shipped default — a job stamp must never name a model that did not run.',
        maxLength: 128, adviseOffDefault: SS_DEFAULT_MODEL,
        meta: { by: 'Dana Whitfield', on: '19 Aug 2026, 11:26' } },
      { key: 'fileAnalysisBaseUrl', type: 'text', claim: 'security', icon: 'send',
        title: 'Provider base URL',
        desc: 'Where analysis requests are sent. Must be an absolute https:// address with no path — the provider appends /v1/messages itself — and the stored API key is sent to this host, so change it only to a host you control or trust.',
        extra: 'Redirects are not followed, so the API key and the document reach only this host. The host is recorded on each job — GDPR Art. 30(1)(e). Every change is written to the audit log.',
        maxLength: 256, checkBaseUrl: true, meta: null },
      { key: 'aiProcessor', type: 'text', claim: 'security', icon: 'corporate_fare',
        title: 'Data processor',
        desc: 'The organisation that receives uploaded documents. Named at the consent gate and in the sentence each user affirms.',
        extra: 'Recorded on each analysis job — GDPR Art. 13(1)(e). Odyssey can only advise on the name, not verify it. Every change is written to the audit log.',
        maxLength: 128, checkHost: true, meta: { by: 'Priya Anand', on: '12 Aug 2026, 10:04' } },
      { key: 'aiProcessorRegion', type: 'text', claim: 'security', icon: 'public',
        title: 'Processor region',
        desc: 'Where processing happens. Nothing the server can see reveals the real region, so this value is trusted as entered — a wrong one is not detectable.',
        extra: 'Shown at the consent gate and recorded on each job — GDPR Chapter V. Every change is written to the audit log.',
        maxLength: 128, meta: null },
      { key: 'aiLawfulBasis', type: 'text', claim: 'security', icon: 'gavel',
        title: 'Lawful basis',
        desc: 'Recorded on every job and shown under the affirmation. Change it when the legal analysis changes, not to relabel past jobs.',
        extra: 'Shown at the consent gate under the sentence each user affirms. Every change is written to the audit log.',
        maxLength: 128, meta: null },
      { key: 'aiPrivacyNoticeUrl', type: 'text', claim: 'security', icon: 'link',
        title: 'Privacy notice URL',
        desc: 'Linked from the consent gate. Absolute https:// only — the value is rendered as a live link.',
        extra: 'Linked at the consent gate — GDPR Art. 13(1)(e). Odyssey checks the URL. Every change is written to the audit log.',
        maxLength: 2048, meta: { by: 'Dana Whitfield', on: '11 Aug 2026, 09:52' } },
      { key: 'aiMaxFutureTransactionDays', type: 'number', claim: 'count', icon: 'update',
        title: 'Future-dated transaction window',
        desc: 'How far ahead of today an extracted transaction date may fall before it is rejected as a misread.',
        min: 1, max: 3650, unit: 'days', meta: null },
      { key: 'aiMatchAutoLinkThreshold', type: 'percent', claim: 'count', icon: 'join_inner',
        title: 'Auto-link confidence threshold',
        desc: 'At or above this, an extracted match is linked automatically; below it, suggested for review. Higher links less. Each job records the threshold it ran under.',
        meta: { by: 'Dana Whitfield', on: '14 Aug 2026, 15:38' } },
      { key: 'aiMaxTokens', type: 'number', claim: 'security', icon: 'format_list_numbered',
        title: 'Maximum response tokens',
        desc: 'The output ceiling on each extraction call. A lower value truncates long statements; a higher one costs more per document.',
        extra: 'Each job records the value it ran under, so a truncated extraction stays distinguishable from a model failure.',
        min: 1024, max: 64000,
        advise: { above: 8096, cost: 'Above the shipped default of 8,096. Every extraction is billed on the tokens it returns, so this raises per-document spend on the processor named above.' },
        meta: null },
      { key: 'aiMatchMaxVocabulary', type: 'number', claim: 'count', icon: 'list_alt',
        title: 'Match vocabulary size',
        desc: 'How many known payees and categories are offered to the match call. Over the cap the match is skipped, not truncated.',
        min: 1, max: 5000,
        advise: { above: 500, cost: 'Above the shipped default of 500. The whole vocabulary is sent with every match call, so this raises the tokens billed on each one.' },
        meta: null },
      { key: 'aiMatchTimeoutSeconds', type: 'number', claim: 'count', icon: 'history_toggle_off',
        title: 'Match timeout',
        desc: 'How long the match call may run before the job is marked failed and falls back to manual review.',
        min: 5, max: 600, unit: 'sec', meta: null },
      // The API key sits with the destination it is sent to and the switch that
      // decides whether anything is sent at all — one card answers "is this on,
      // where does it go, what authenticates it". Encrypted and write-only, so it
      // commits on its own request and never joins the page’s Save.
      { key: 'secretFileAnalysisApiKey', type: 'secret', claim: 'security', icon: 'vpn_key',
        secretKey: 'FileAnalysis:ApiKey', kind: 'credential', state: 'found',
        title: 'File analysis API key',
        desc: 'Sent as x-api-key on every analysis request, to the host set as the provider base URL.',
        extra: 'A replacement takes effect on the next request without a restart. If the row cannot be read, analysis fails and records a credential problem — it never falls back to a configured value.',
        consequence: 'Every document analysis fails and is recorded as a failed job. Nothing is transferred and nothing is lost; the feature is unavailable until a key is entered.',
        affects: 'Document analysis is failing on every job.',
        meta: { by: 'Priya Anand', on: '18 Aug 2026, 08:41' } },
    ],
  },
  {
    group: 'Email', icon: 'mark_email_read',
    rows: [
      { key: 'emailFromAddress', type: 'text', claim: 'security', icon: 'alternate_email',
        title: 'From address',
        desc: 'The sender on every transactional mail. Must stay an address the relay is authorised to send as, or SPF/DKIM will fail and mail will be dropped silently.',
        maxLength: 256, meta: null },
      { key: 'emailFromName', type: 'text', claim: 'security', icon: 'badge',
        title: 'From name',
        desc: 'The display name beside the sender address.',
        maxLength: 128, meta: null },
      { key: 'emailPerRecipientLimit', type: 'number', claim: 'security', icon: 'filter_list',
        title: 'Messages per recipient',
        desc: 'How many mails one address may receive per window. Bounds password-reset flooding.',
        min: 1, max: 1000, meta: null },
      { key: 'emailPerRecipientWindowMinutes', type: 'number', claim: 'security', icon: 'timelapse',
        title: 'Recipient window',
        desc: 'The window the per-recipient limit is counted over. A longer window is a tighter throttle.',
        min: 1, max: 1440, unit: 'min', meta: null },
      { key: 'emailMaxTrackedRecipients', type: 'number', claim: 'security', icon: 'send',
        title: 'Tracked recipients',
        desc: 'How many addresses the throttle holds at once. At capacity it stops throttling, so a smaller table weakens the limit above rather than tightening it.',
        extra: 'Raising it also speeds the sweep that brings the table back under capacity. Existing entries age out over up to a full window, so a change is not instant in either direction.',
        min: 20000, max: 200000, floor: 20000, meta: null },
      // The relay credentials and the throttle’s hash key belong with the from
      // address and the send limits they authenticate and count. Username and
      // password are used or not used together; the hash key is a derivation key,
      // marked as such because losing it cannot be undone by re-issuing it.
      { key: 'secretEmailUsername', type: 'secret', claim: 'security', icon: 'person',
        secretKey: 'Email:Username', kind: 'credential', state: 'found',
        title: 'SMTP username',
        desc: 'Authenticates the relay connection, together with the SMTP password.',
        extra: 'The pair is used or not used together — a stored username beside an unset password is a half-configured credential, and the send is skipped rather than attempted unauthenticated.',
        consequence: 'The relay connection is made without authenticating. That is a legitimate configuration for a relay that accepts unauthenticated mail on a trusted network, and a silent failure for every other kind.',
        affects: 'Transactional mail is not sending.',
        meta: { by: 'Marcus Reyes', on: '2 Aug 2026, 09:10' } },
      { key: 'secretEmailPassword', type: 'secret', claim: 'security', icon: 'password',
        secretKey: 'Email:Password', kind: 'credential', state: 'unreadable',
        title: 'SMTP password',
        desc: 'Authenticates the relay connection. A human-chosen password at a third-party provider.',
        extra: 'Only printable ASCII can be stored. A relay password outside that range is rejected before it is sent, with the constraint named — not returned as a bare 400.',
        consequence: 'Password resets, email confirmations and every other transactional mail are attempted unauthenticated and will be rejected by any relay that requires a login.',
        affects: 'Transactional mail is not sending — every send is logged and skipped.',
        meta: { by: 'Marcus Reyes', on: '2 Aug 2026, 09:10' } },
      { key: 'secretEmailRecipientHashKey', type: 'secret', claim: 'security', icon: 'fingerprint',
        secretKey: 'Email:RecipientHashKey', kind: 'derivation', state: 'not-set',
        title: 'Recipient hash key',
        desc: 'Derives the digests the send throttle counts per recipient, so a log never carries an address.',
        extra: 'Replacing it breaks nothing already recorded, but digests written before the change stop correlating with the ones after it.',
        consequence: 'Unset is a supported configuration, not a fault: a random key is generated per process, so throttle digests correlate within one process\u2019s lifetime and not across a restart.',
        affects: 'Throttle digests have fallen back to a per-process key, so log correlation is broken across restarts.',
        meta: null },
    ],
  },
  {
    group: 'Data', icon: 'storage',
    rows: [
      { key: 'fileStorageMaxUploadMegabytes', type: 'size', claim: 'security', icon: 'sd_storage',
        title: 'Maximum upload file size',
        desc: 'Largest single file accepted by any upload surface. Cannot be raised above 64 MB, which is the request-size ceiling the server starts with.',
        min: 1, max: 1024, ceiling: 64, meta: null },
      { key: 'dataExport', type: 'export', claim: null, icon: 'download_for_offline',
        title: 'Export database JSON',
        desc: 'Download finance records as JSON for audit or migration. Excludes uploaded file contents, file analysis, Identity data, and preferences.' },
      // The pseudonymisation secret has no feature card of its own — its consumer
      // is account deletion, a data-lifecycle act — so it sits with the other
      // data-retention controls, beside the export that carries the same records.
      { key: 'secretLegalPseudonymizationSecret', type: 'secret', claim: 'security', icon: 'gavel',
        secretKey: 'Legal:PseudonymizationSecret', kind: 'derivation', state: 'not-set',
        title: 'Pseudonymisation secret',
        desc: 'HMACs the subject of a consent record when an account is deleted, so acceptance stays attributable without holding an identity.',
        extra: 'There is no provider to re-issue this from. Lose it and every row already pseudonymised with it is permanently un-re-derivable \u2014 the property GDPR Art. 7(1) consent attribution depends on. Export the value before replacing or clearing it.',
        consequence: 'Account deletion cannot pseudonymise consent records. Outside Production a fixed development value is substituted; in Production the value is required.',
        affects: 'Account deletion cannot pseudonymise consent records.',
        meta: null },
    ],
  },
  {
    group: 'Insurance', icon: 'health_and_safety',
    rows: [
      { key: 'insuranceExpiringSoonWindowDays', type: 'number', claim: 'count', icon: 'schedule',
        title: '"Expiring soon" window',
        desc: 'How many days ahead of expiry a policy is flagged as expiring soon.',
        min: 1, max: 365, unit: 'days', meta: { by: 'Dana Whitfield', on: '5 Aug 2026, 11:30' } },
      { key: 'insuranceMaxSummaryPolicies', type: 'number', claim: 'count', icon: 'format_list_numbered',
        title: 'Max policies shown in summary',
        desc: 'Upper limit on the policies listed in the summary roll-up.',
        min: 1, max: 100000, meta: null },
      { key: 'insuranceMaxRenewalsPerPolicy', type: 'number', claim: 'count', icon: 'autorenew',
        title: 'Max renewals per policy',
        desc: 'Upper limit on the renewal records one policy may carry.',
        min: 1, max: 100000, meta: null },
      { key: 'insuranceMaxFilesPerParent', type: 'number', claim: 'count', icon: 'attach_file',
        title: 'Max files per policy or renewal',
        desc: 'Upper limit on the documents attached to a single policy or renewal.',
        min: 1, max: 100000, meta: null },
    ],
  },
  {
    group: 'Contracts', icon: 'description',
    rows: [
      { key: 'contractMaxPartiesPerContract', type: 'number', claim: 'count', icon: 'groups',
        title: 'Max parties per contract',
        desc: 'Upper limit on the counterparties one contract may name.',
        min: 1, max: 100000, meta: null },
      { key: 'contractMaxFilesPerContract', type: 'number', claim: 'count', icon: 'attach_file',
        title: 'Max files per contract',
        desc: 'Upper limit on the documents attached to a single contract.',
        min: 1, max: 100000, meta: null },
      { key: 'contractMaxSummaryContracts', type: 'number', claim: 'count', icon: 'format_list_numbered',
        title: 'Max contracts in summary',
        desc: 'Upper limit on the contracts listed in the summary roll-up.',
        min: 1, max: 100000, meta: null },
    ],
  },
  {
    // Round 4: the three subscriptions-summary limits. The first two were
    // `private const` on SubscriptionService (45 / 6) and are seeded at exactly
    // those values, so a default install is behaviourally identical. The third
    // did not exist — the summary's fetch was unbounded — and seeds at 1000 to
    // match insuranceMaxSummaryPolicies and contractMaxSummaryContracts.
    // No advisory on any of the three: lowering is what reduces work here, so an
    // "above the shipped default" band would fire on the value CLOSEST to
    // today's unbounded read. All three take system-settings.update.
    group: 'Subscriptions', icon: 'subscriptions',
    rows: [
      { key: 'subscriptionRenewalWindowDays', type: 'number', claim: 'count', icon: 'schedule',
        title: 'Upcoming renewals window',
        desc: 'How many days ahead a subscription’s next billing date is surfaced as an upcoming renewal.',
        min: 1, max: 365, unit: 'days', meta: null },
      { key: 'subscriptionMaxSummaryRenewals', type: 'number', claim: 'count', icon: 'format_list_numbered',
        title: 'Max renewals shown in summary',
        desc: 'Upper limit on the renewal rows listed in the summary roll-up.',
        min: 1, max: 1000, meta: null },
      { key: 'subscriptionMaxSummarySubscriptions', type: 'number', claim: 'count', icon: 'subscriptions',
        title: 'Max subscriptions read for summary',
        desc: 'Upper limit on the subscriptions read to compute the roll-up. Beyond it the counts and run-rate cover the most recent subscriptions only.',
        min: 1, max: 100000, meta: null },
    ],
  },
  {
    group: 'Photos', icon: 'photo_library',
    rows: [
      { key: 'photoMaxLinksPerKind', type: 'number', claim: 'count', icon: 'sell',
        title: 'Max links per photo',
        desc: 'Upper limit on the contacts, tags or accounts one photo may link to. Cannot be raised above 50 — the request format rejects a longer list before this setting is consulted.',
        min: 1, max: 100000, ceiling: 50, meta: null },
      { key: 'photoMetadataReadMegabytes', type: 'size', claim: 'security', icon: 'sd_storage',
        title: 'Metadata read size',
        desc: 'How much of an accepted image is read to pull EXIF. Beyond this, extraction is skipped and the photo is still stored.',
        min: 1, max: 16,
        advise: { above: 8, cost: 'Above the shipped default of 8 MB. The prefix is held as one buffer per extraction, and 16 MB is the database default packet size \u2014 past that the read is skipped rather than served.' },
        meta: null },
      { key: 'photoMetadataExtractionTimeoutSeconds', type: 'number', claim: 'count', icon: 'schedule',
        title: 'Metadata extraction timeout',
        desc: 'How long EXIF extraction may run per photo. On timeout the photo is stored without metadata.',
        min: 1, max: 120, unit: 'sec', meta: null },
      { key: 'photoMaxAlbumMembers', type: 'number', claim: 'count', icon: 'collections',
        title: 'Max photos per album',
        desc: 'Upper limit on the photos one album may hold. Cannot be raised above 1,000 — the request format rejects a longer list before this setting is consulted.',
        min: 1, max: 100000, ceiling: 1000, meta: null },
    ],
  },
  {
    group: 'Journal limits', icon: 'menu_book',
    rows: [
      { key: 'journalEntryMaxLinksPerKind', type: 'number', claim: 'count', icon: 'link',
        title: 'Max links per journal entry',
        desc: 'Upper limit on the records one journal entry may link to, per kind.',
        min: 1, max: 100000, meta: null },
      { key: 'journalTaskMaxLinksPerKind', type: 'number', claim: 'count', icon: 'link',
        title: 'Max links per task',
        desc: 'Upper limit on the records one task may link to, per kind.',
        min: 1, max: 100000, meta: null },
    ],
  },
  {
    group: 'Contacts import & export', icon: 'contacts',
    roundTrip: { exportKey: 'contactVCardMaxExportRows', importKey: 'contactVCardMaxImportEntries' },
    rows: [
      { key: 'contactVCardMaxExportRows', type: 'capacity', claim: 'count', icon: 'file_download',
        title: 'Maximum contacts per export',
        desc: 'Upper limit on the rows a vCard (.vcf) export may produce. "No limit" keeps exports unbounded.',
        min: 1, max: 1000000, meta: null },
      { key: 'contactVCardMaxImportEntries', type: 'capacity', claim: 'count', icon: 'file_upload',
        title: 'Maximum contacts per import',
        desc: 'Upper limit on the entries accepted from an imported vCard file.',
        min: 1, max: 1000000, meta: null },
      { key: 'contactVCardMaxRepeatablePropertiesPerEntry', type: 'number', claim: 'count', icon: 'list_alt',
        title: 'Repeatable properties per contact',
        desc: 'How many emails, phones or addresses one imported contact may carry. Each one is saved individually, and that cost multiplies by the import cap above — which ships unbounded.',
        min: 1, max: 200, ceiling: 200, meta: null },
      { key: 'contactVCardMaxExportMegabytes', type: 'size', claim: 'security', icon: 'sd_storage',
        title: 'Maximum export file size',
        desc: 'Largest vCard (.vcf) file an export may produce before the request is rejected.',
        min: 1, max: 512, meta: null },
      { key: 'contactVCardMaxImportMegabytes', type: 'size', claim: 'security', icon: 'sd_storage',
        title: 'Maximum import file size',
        desc: 'Largest vCard (.vcf) upload accepted. Above ~64 MB, also raise the reverse proxy’s body-size limit.',
        min: 1, max: 512, meta: { by: 'Marcus Reyes', on: '7 Aug 2026, 08:05' } },
    ],
  },
  {
    group: 'Calendars import & export', icon: 'calendar_month',
    roundTrip: { exportKey: 'calendarIcsMaxExportEvents', importKey: 'calendarIcsMaxImportEvents' },
    rows: [
      { key: 'calendarIcsMaxExportEvents', type: 'capacity', claim: 'count', icon: 'file_download',
        title: 'Maximum events per export',
        desc: 'Upper limit on the VEVENTs an iCalendar (.ics) export may produce.',
        min: 1, max: 1000000, meta: null },
      { key: 'calendarIcsMaxImportEvents', type: 'capacity', claim: 'count', icon: 'file_upload',
        title: 'Maximum events per import',
        desc: 'Upper limit on the VEVENTs accepted from an imported .ics file.',
        min: 1, max: 1000000, meta: null },
      { key: 'calendarIcsMaxExportMegabytes', type: 'size', claim: 'security', icon: 'sd_storage',
        title: 'Maximum export file size',
        desc: 'Largest iCalendar (.ics) file an export may produce before the request is rejected.',
        min: 1, max: 512, meta: null },
      { key: 'calendarIcsMaxImportMegabytes', type: 'size', claim: 'security', icon: 'sd_storage',
        title: 'Maximum import file size',
        desc: 'Largest iCalendar (.ics) upload accepted. Above ~64 MB, also raise the reverse proxy’s body-size limit.',
        min: 1, max: 512, meta: null },
      { key: 'calendarIcsMaxAggregateExportRows', type: 'number', claim: 'count', icon: 'file_download',
        title: 'Maximum rows per whole-calendar export',
        desc: 'Upper limit on the rows one export of every calendar at once may produce.',
        min: 1, max: 40000,
        advise: { above: 20000, cost: 'Above the shipped default of 20,000. Every row is held in memory while the file is built, and two of these exports can run at once.' },
        meta: null },
      { key: 'calendarIcsMaxAggregateOccurrences', type: 'number', claim: 'count', icon: 'autorenew',
        title: 'Maximum occurrences per whole-calendar export',
        desc: 'Upper limit on the recurring occurrences expanded across one whole-calendar export.',
        min: 1, max: 20000,
        advise: { above: 5000, cost: 'Above the shipped default of 5,000. Occurrences are expanded in memory before anything is written, and two of these exports can run at once.' },
        meta: null },
      { key: 'calendarIcsMaxAggregateExportWindowDays', type: 'number', claim: 'count', icon: 'event_available',
        title: 'Whole-calendar export window',
        desc: 'How wide a date range one export of every calendar at once may cover.',
        min: 1, max: 3650, unit: 'days', meta: null },
    ],
  },
  {
    group: 'Tasks import & export', icon: 'checklist',
    roundTrip: { exportKey: 'taskIcsMaxExportTasks', importKey: 'taskIcsMaxImportTasks' },
    rows: [
      { key: 'taskIcsMaxExportTasks', type: 'capacity', claim: 'count', icon: 'file_download',
        title: 'Maximum tasks per export',
        desc: 'Upper limit on the VTODOs an iCalendar (.ics) export may produce.',
        min: 1, max: 1000000, meta: null },
      { key: 'taskIcsMaxImportTasks', type: 'capacity', claim: 'count', icon: 'file_upload',
        title: 'Maximum tasks per import',
        desc: 'Upper limit on the VTODOs accepted from an imported .ics file.',
        min: 1, max: 1000000, meta: null },
      { key: 'taskIcsMaxExportMegabytes', type: 'size', claim: 'security', icon: 'sd_storage',
        title: 'Maximum export file size',
        desc: 'Largest iCalendar (.ics) file an export may produce before the request is rejected.',
        min: 1, max: 512, meta: null },
      { key: 'taskIcsMaxImportMegabytes', type: 'size', claim: 'security', icon: 'sd_storage',
        title: 'Maximum import file size',
        desc: 'Largest iCalendar (.ics) upload accepted. Above ~64 MB, also raise the reverse proxy’s body-size limit.',
        min: 1, max: 512, meta: null },
    ],
  },
  {
    group: 'Journal import & export', icon: 'import_contacts',
    roundTrip: { exportKey: 'journalIcsMaxExportRows', importKey: 'journalIcsMaxImportEntries' },
    rows: [
      { key: 'journalIcsMaxExportRows', type: 'capacity', claim: 'count', icon: 'file_download',
        title: 'Maximum entries per export',
        desc: 'Upper limit on the VJOURNALs an iCalendar (.ics) export may produce.',
        min: 1, max: 1000000, meta: null },
      { key: 'journalIcsMaxImportEntries', type: 'capacity', claim: 'count', icon: 'file_upload',
        title: 'Maximum entries per import',
        desc: 'Upper limit on the VJOURNALs accepted from an imported .ics file.',
        min: 1, max: 1000000, meta: null },
      { key: 'journalIcsMaxExportMegabytes', type: 'size', claim: 'security', icon: 'sd_storage',
        title: 'Maximum export file size',
        desc: 'Largest iCalendar (.ics) file an export may produce before the request is rejected.',
        min: 1, max: 512, meta: null },
      { key: 'journalIcsMaxImportMegabytes', type: 'size', claim: 'security', icon: 'sd_storage',
        title: 'Maximum import file size',
        desc: 'Largest iCalendar (.ics) upload accepted. Above ~64 MB, also raise the reverse proxy’s body-size limit.',
        min: 1, max: 512, meta: null },
    ],
  },
  {
    group: 'Calendar limits', icon: 'event_note',
    rows: [
      { key: 'calendarMaxWindowDays', type: 'number', claim: 'count', icon: 'event_available',
        title: 'Calendar view window',
        desc: 'How wide a date range one calendar request may ask for.',
        min: 1, max: 3650, unit: 'days', meta: null },
      { key: 'calendarMaxEventDurationDays', type: 'number', claim: 'count', icon: 'schedule',
        title: 'Maximum event duration',
        desc: 'How long a single event may run before it is rejected as a mistake.',
        min: 1, max: 3650, unit: 'days', meta: null },
      { key: 'recurrenceMaxGeneratedOccurrences', type: 'number', claim: 'count', icon: 'autorenew',
        title: 'Maximum generated occurrences',
        desc: 'How many occurrences one recurrence rule may generate. Each one is saved as its own event, so raising this multiplies what a single request writes — and lowering it back does not undo rows already written.',
        min: 1, max: 1000, ceiling: 1000, meta: null },
    ],
  },
  {
    group: 'Import & export defaults', icon: 'import_export',
    rows: [
      { key: 'importMaxSamplesPerSkipReason', type: 'number', claim: 'count', icon: 'file_upload',
        title: 'Skipped-row samples per reason',
        desc: 'How many example rows an import summary keeps for each reason something was skipped. Applies to every importer — contacts, calendars, tasks and journal.',
        min: 1, max: 10000,
        advise: { above: 100, cost: 'Above the shipped default of 100. Samples are returned in the import response, so this grows the payload every importer sends back.' },
        meta: null },
    ],
  },
  {
    group: 'Accounts', icon: 'account_balance',
    rows: [
      { key: 'accountMaxSmartTagsPerAccount', type: 'number', claim: 'count', icon: 'sell',
        title: 'Smart tags per account',
        desc: 'How many smart tags one account may carry. The Accounts page reads this value, so the limit it shows a user always matches what the server enforces.',
        min: 1, max: 1000, meta: null },
    ],
  },
];

// Saved state as it'd come back from GET /api/system-settings. Booleans and
// plain numbers as scalars; capacity caps as {unlimited, value} with the value
// RETAINED while unlimited, so a toggle-and-back is not dirty; size caps as
// megabyte integers; the confidence threshold as a 0.0–1.0 fraction.
const ssCap = (unlimited, value) => ({ unlimited, value });
const SS_SAVED = {
  requireTwoFactor: false,
  registrationRequireAdminApproval: true,
  emailRequireConfirmation: true,

  fileAnalysisEnabled: true,
  fileAnalysisModel: 'claude-opus-4-7',
  fileAnalysisBaseUrl: 'https://api.anthropic.com',

  aiProcessor: 'Anthropic',
  aiProcessorRegion: 'United States',
  aiLawfulBasis: 'Consent · GDPR Art. 6(1)(a)',
  aiPrivacyNoticeUrl: 'https://www.anthropic.com/legal/privacy',
  aiMaxFutureTransactionDays: 90,
  aiMatchAutoLinkThreshold: 0.6,
  aiMaxTokens: 8096,
  aiMatchMaxVocabulary: 500,
  aiMatchTimeoutSeconds: 60,

  emailFromAddress: 'no-reply@odyssey.local',
  emailFromName: 'Odyssey',
  emailPerRecipientLimit: 3,
  emailPerRecipientWindowMinutes: 60,
  emailMaxTrackedRecipients: 20000,

  fileStorageMaxUploadMegabytes: 64,

  insuranceExpiringSoonWindowDays: 30,
  insuranceMaxSummaryPolicies: 1000,
  insuranceMaxRenewalsPerPolicy: 100,
  insuranceMaxFilesPerParent: 50,

  contractMaxPartiesPerContract: 25,
  contractMaxFilesPerContract: 50,
  contractMaxSummaryContracts: 1000,

  subscriptionRenewalWindowDays: 45,
  subscriptionMaxSummaryRenewals: 6,
  subscriptionMaxSummarySubscriptions: 1000,

  photoMaxLinksPerKind: 50,
  photoMaxAlbumMembers: 1000,
  photoMetadataReadMegabytes: 8,
  photoMetadataExtractionTimeoutSeconds: 5,

  journalEntryMaxLinksPerKind: 50,
  journalTaskMaxLinksPerKind: 50,

  contactVCardMaxExportRows: ssCap(true, 50000),
  contactVCardMaxImportEntries: ssCap(true, 50000),
  contactVCardMaxExportMegabytes: 500,
  contactVCardMaxImportMegabytes: 500,

  calendarIcsMaxExportEvents: ssCap(false, 2000),
  calendarIcsMaxImportEvents: ssCap(false, 2000),
  calendarIcsMaxExportMegabytes: 5,
  calendarIcsMaxImportMegabytes: 5,

  taskIcsMaxExportTasks: ssCap(false, 2000),
  taskIcsMaxImportTasks: ssCap(false, 2000),
  taskIcsMaxExportMegabytes: 5,
  taskIcsMaxImportMegabytes: 5,

  journalIcsMaxExportRows: ssCap(false, 2000),
  journalIcsMaxImportEntries: ssCap(false, 2000),
  journalIcsMaxExportMegabytes: 5,
  journalIcsMaxImportMegabytes: 5,

  calendarIcsMaxAggregateExportRows: 20000,
  calendarIcsMaxAggregateOccurrences: 5000,
  calendarIcsMaxAggregateExportWindowDays: 92,

  contactVCardMaxRepeatablePropertiesPerEntry: 200,

  calendarMaxWindowDays: 92,
  calendarMaxEventDurationDays: 366,
  recurrenceMaxGeneratedOccurrences: 1000,

  importMaxSamplesPerSkipReason: 100,

  accountMaxSmartTagsPerAccount: 20,
};

Object.assign(window, { SS_GROUPS, SS_SAVED, SS_PROCESSOR_HOST, SS_DEFAULT_BASE_URL, SS_DEFAULT_MODEL });
