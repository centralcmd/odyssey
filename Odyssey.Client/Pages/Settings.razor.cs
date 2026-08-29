using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Application;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;

namespace Odyssey.Client.Pages;

public partial class Settings
{
    private const string PageStateKey = "settings-page";
    private bool _searchOpen = true;
    private string _search = string.Empty;
    private string? _announce;

    // A live-region message assigned the SAME text twice in a row (e.g. two quick "No limit"
    // toggles that land on the same wording) produces no second DOM mutation, so most screen
    // readers announce it only once (issue #343 fe carried nit). Flipping an invisible
    // zero-width-space suffix on every call guarantees the text actually changes each time.
    private bool _announceParity;

    private void Announce(string message) =>
        _announce = (_announceParity = !_announceParity) ? message : message + "​";

    private enum Phase { Loading, Ready, Error }
    private Phase _phase = Phase.Loading;

    // Claim possession — computed once from the loaded AuthenticationState in OnInitializedAsync,
    // NOT from any control's momentary Disabled state (spec §3): a save-in-flight or still-loading
    // row also renders Disabled, but that must never null out a field the caller actually holds the
    // claim for. Axis split (issue #343 §10 item 4): Security → system-settings.security.update
    // (the three original Security toggles AND the four size caps); Count → system-settings.update
    // (the two Insurance counts AND the seven count caps).
    private bool _hasSecurityUpdate;
    private bool _hasCountUpdate;
    private bool CanSave => _hasSecurityUpdate || _hasCountUpdate;

    private SystemSettingsDto? _dto;
    private bool _isSaving;
    private bool _justSaved;

    // ── Encrypted credentials (issue #444) ────────────────────────────────────────────────────
    // The server's statuses, keyed by registry key. The page renders the INTERSECTION of the static
    // client catalogue with these, so the server stays the single authority on which secrets exist.
    private readonly Dictionary<string, SecretSettingStatusDto> _secretStatuses = new(StringComparer.Ordinal);

    // ── Export controls (unchanged behaviour, moved under the Data group) ─────
    private bool _exportAvailable;
    private bool _isExporting;

    /// <summary>
    /// The interactivity check, as a seam (issue #444 §13 step 4a). <c>OnInitializedAsync</c> has to
    /// early-return on the prerender pass, but that check is <c>false</c> in a bUnit host too, so a
    /// render of this page would stop at the loading skeleton with no rows to assert against — and the
    /// secret row would ship with no test that can render it inside the page it lives on.
    ///
    /// <para>
    /// Following the precedent <c>PageStateService</c>'s <c>internal</c> constructor already sets for
    /// exactly this problem. It is <c>internal</c>, so only <c>Odyssey.Client.Tests</c> can move it; a
    /// test that does must restore it.
    /// </para>
    /// </summary>
    internal static Func<bool> InteractiveCheck { get; set; } = static () => OperatingSystem.IsBrowser();

    protected override async Task OnInitializedAsync()
    {
        if (!InteractiveCheck())
            return;

        await PageState.RestoreOrSeedAsync<SettingsPageState>(PageStateKey, ApplyPageState, BuildPageState);

        var user = await Auth.GetUserAsync();
        _hasSecurityUpdate = user.HasPermission(PermissionClaims.SystemSettingsSecurityUpdate);
        _hasCountUpdate = user.HasPermission(PermissionClaims.SystemSettingsUpdate);

        // Gate the Data export control on the data.export permission alone — the feature is always
        // available to holders of that claim.
        _exportAvailable = user.HasPermission(PermissionClaims.DataExport);

        await LoadSettingsAsync();
    }

    private async Task LoadSettingsAsync()
    {
        _phase = Phase.Loading;
        StateHasChanged();

        var result = await SystemSettings.GetAsync();
        if (result.IsSuccess && result.Value is { } dto)
        {
            ApplyLoaded(dto);
            await LoadSecretStatusesAsync();
            _phase = Phase.Ready;
            Announce(AdvisorySuffixed("Settings loaded."));
        }
        else
        {
            _phase = Phase.Error;
            Announce("Couldn't load settings.");
        }

        StateHasChanged();
    }

    /// <summary>Every row in the catalogue, flattened — the loop source for load and save.</summary>
    internal static IEnumerable<SettingItem> AllItems => Sections.SelectMany(section => section.Items);

    private void ApplyLoaded(SystemSettingsDto dto)
    {
        _dto = dto;

        // A fresh load supersedes whatever the server last rejected.
        _serverErrors.Clear();

        foreach (var item in AllItems)
        {
            item.Load?.Invoke(this, dto);
        }







        _justSaved = false;
    }

    // ── Page-state persistence (the search SECTION only — see the note below) ─────────────────
    private void ApplyPageState(SettingsPageState state)
    {
        _searchOpen = state.SearchOpen;
    }

    /// <summary>
    /// The persisted payload. Whatever this method returns is written, through the API, into the
    /// <c>UserPreferences</c> table on <c>OdysseyContext</c> — durable server storage, under a
    /// <c>UserId</c> with a real FK to <c>AspNetUsers</c>, replicated into every backup. That is the
    /// very database the encrypted-secrets feature exists to keep plaintext credentials out of, which
    /// is why a source-lint pins this method's shape.
    ///
    /// <para>
    /// <strong><c>Search</c> was removed from it deliberately</strong> (issue #444 §10). The lint pins
    /// the payload's shape but cannot cover a value the <em>user types</em> into a field that is
    /// legitimately persisted — and pasting a credential into a search box named after that credential
    /// is a plausible slip on this page in particular. The cost is losing the restoration of a filter
    /// string; the alternative is a plaintext credential in the preferences table.
    /// </para>
    /// </summary>
    private SettingsPageState BuildPageState() => new()
    {
        SearchOpen = _searchOpen,
    };

    private void PersistPageState() => PageState.QueueSave(PageStateKey, BuildPageState());

    private void OnSearchToggled(bool open) { _searchOpen = open; PersistPageState(); }

    // Not persisted — see BuildPageState.
    private void OnSearchChanged(string value) => _search = value ?? string.Empty;

    private sealed class SettingsPageState
    {
        public bool SearchOpen { get; set; } = true;
    }

    // ── Settings catalog (single source of truth — drives render AND search) ──────────────────
    //
    // Count caps → system-settings.update; SIZE caps → system-settings.security.update (issue #343
    // §10 item 4) — the availability knob (size) sits behind the stricter claim. A group carries
    // RoundTrip when it has both an export- and an import-count cap that must satisfy export ≤
    // import (§9) — all four groups do now that Tasks gained an export row cap as a follow-up
    // (previously "Non-Goal 2": no export cap at all).
    internal enum SettingClaim { None, Security, Count }
    internal enum SettingControl { Toggle, Number, Size, Capacity, Export, Text, Decimal, Percent }

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
        string? Placeholder = null);

    internal sealed record RoundTripPair(string ExportKey, string ImportKey);

    internal sealed record SettingSection(
        string Group, string Icon, IReadOnlyList<SettingItem> Items, RoundTripPair? RoundTrip = null);

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
        new("Data", Icons.Material.Filled.Storage,
        [
            new("data-export", "download_for_offline", "Export database JSON",
                "Download finance records as JSON for audit or migration. Excludes uploaded file contents, file analysis, Identity data, and preferences.",
                SettingClaim.None, SettingControl.Export),
        ]),
        new("Insurance", Icons.Material.Filled.HealthAndSafety,
        [
            new("insurance-window", "schedule", "\"Expiring soon\" window",
                "How many days ahead of expiry a policy is flagged as expiring soon.",
                SettingClaim.Count, SettingControl.Number, Min: SystemSettingsBounds.InsuranceExpiringSoonWindowDaysMin, Max: SystemSettingsBounds.InsuranceExpiringSoonWindowDaysMax,
                Field: nameof(SystemSettingsUpdate.InsuranceExpiringSoonWindowDays),
                Load: (p, dto) => p.SetIntLoaded("insurance-window", dto.InsuranceExpiringSoonWindowDays), Write: (p, req) => req.InsuranceExpiringSoonWindowDays = p.IntRequest("insurance-window"),
                Unit: "days"),
            new("insuranceMaxRenewalsPerPolicy", "autorenew", "Max renewals per policy",
                "Upper limit on renewals recorded against one policy. A request over the cap is rejected.",
                SettingClaim.Count, SettingControl.Number, Min: SystemSettingsBounds.InsuranceMaxRenewalsPerPolicyMin, Max: SystemSettingsBounds.InsuranceMaxRenewalsPerPolicyMax,
                Field: nameof(SystemSettingsUpdate.InsuranceMaxRenewalsPerPolicy),
                Load: (p, dto) => p.SetIntLoaded("insuranceMaxRenewalsPerPolicy", dto.InsuranceMaxRenewalsPerPolicy),
                Write: (p, req) => req.InsuranceMaxRenewalsPerPolicy = p.IntRequest("insuranceMaxRenewalsPerPolicy")),
            new("insuranceMaxFilesPerParent", "attach_file", "Max files per policy or renewal",
                "Upper limit on files attached to a single policy or renewal.",
                SettingClaim.Count, SettingControl.Number, Min: SystemSettingsBounds.InsuranceMaxFilesPerParentMin, Max: SystemSettingsBounds.InsuranceMaxFilesPerParentMax,
                Field: nameof(SystemSettingsUpdate.InsuranceMaxFilesPerParent),
                Load: (p, dto) => p.SetIntLoaded("insuranceMaxFilesPerParent", dto.InsuranceMaxFilesPerParent),
                Write: (p, req) => req.InsuranceMaxFilesPerParent = p.IntRequest("insuranceMaxFilesPerParent")),
            new("insurance-max", "format_list_numbered", "Max policies shown in summary",
                "Upper limit on the policies listed in the summary roll-up.",
                SettingClaim.Count, SettingControl.Number, Min: SystemSettingsBounds.InsuranceMaxSummaryPoliciesMin, Max: SystemSettingsBounds.InsuranceMaxSummaryPoliciesMax,
                Field: nameof(SystemSettingsUpdate.InsuranceMaxSummaryPolicies),
                Load: (p, dto) => p.SetIntLoaded("insurance-max", dto.InsuranceMaxSummaryPolicies), Write: (p, req) => req.InsuranceMaxSummaryPolicies = p.IntRequest("insurance-max")),
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
            new("contactVCardMaxImportMegabytes", "sd_storage", "Maximum import file size",
                "Largest vCard (.vcf) upload accepted. Above ~64 MB, also raise the reverse proxy's body-size limit.",
                SettingClaim.Security, SettingControl.Size, Min: 1, Max: 1024,
                Field: nameof(SystemSettingsUpdate.ContactVCardMaxImportMegabytes),
                Load: (p, dto) => p.SetIntLoaded("contactVCardMaxImportMegabytes", dto.ContactVCardMaxImportMegabytes), Write: (p, req) => req.ContactVCardMaxImportMegabytes = p.IntRequest("contactVCardMaxImportMegabytes")),
            new("contactVCardMaxExportMegabytes", "sd_storage", "Maximum export file size",
                "Largest vCard (.vcf) file an export may produce. A too-large export is truncated rather "
                + "than exceeding this, so the download fails cleanly instead of silently.",
                SettingClaim.Security, SettingControl.Size, Min: 1, Max: 1024,
                Field: nameof(SystemSettingsUpdate.ContactVCardMaxExportMegabytes),
                Load: (p, dto) => p.SetIntLoaded("contactVCardMaxExportMegabytes", dto.ContactVCardMaxExportMegabytes), Write: (p, req) => req.ContactVCardMaxExportMegabytes = p.IntRequest("contactVCardMaxExportMegabytes")),
            // ── issue #434 key 12. TIGHTEN-ONLY: each repeatable property costs a sibling query AND its
            // own save, multiplied by an import entry cap that ships unlimited, so any number above the
            // shipped 200 would be a guess about a product of three unbounded terms. The static Max
            // names the same shared constant the server's [Range] maximum does.
            new("contactVCardMaxRepeatablePropertiesPerEntry", "list_alt", "Max repeated fields per contact",
                "How many addresses, email addresses or phone numbers are imported from one vCard entry. "
                + "Each one costs a separate write.",
                SettingClaim.Count, SettingControl.Number,
                Min: 1, Max: SystemSettingsDefaults.ContactVCardMaxRepeatablePropertiesPerEntry,
                Field: nameof(SystemSettingsUpdate.ContactVCardMaxRepeatablePropertiesPerEntry),
                Load: (p, dto) => p.SetIntLoaded(
                    "contactVCardMaxRepeatablePropertiesPerEntry",
                    dto.ContactVCardMaxRepeatablePropertiesPerEntry),
                Write: (p, req) => req.ContactVCardMaxRepeatablePropertiesPerEntry =
                    p.IntRequest("contactVCardMaxRepeatablePropertiesPerEntry"),
                MaxFrom: dto => dto.ContactVCardMaxRepeatablePropertiesPerEntryCeiling),
        ],
        RoundTrip: new RoundTripPair("contactVCardMaxExportRows", "contactVCardMaxImportEntries")),
        new("Calendars import & export", Icons.Material.Filled.CalendarMonth,
        [
            new("calendarIcsMaxExportEvents", "file_download", "Maximum events per export",
                "Upper limit on the VEVENTs an iCalendar (.ics) export may produce. \"No limit\" keeps "
                + "exports unbounded — the separate whole-calendar download keeps its own fixed ceiling "
                + "regardless of this setting.",
                SettingClaim.Count, SettingControl.Capacity, Min: 1, Max: 1_000_000,
                Field: nameof(SystemSettingsUpdate.CalendarIcsMaxExportEvents),
                Load: (p, dto) => p.SetCapacityLoaded("calendarIcsMaxExportEvents", dto.CalendarIcsMaxExportEvents), Write: (p, req) => req.CalendarIcsMaxExportEvents = p.CapacityRequest("calendarIcsMaxExportEvents")),
            new("calendarIcsMaxImportEvents", "file_upload", "Maximum events per import",
                "Upper limit on the VEVENTs accepted from an imported .ics file.",
                SettingClaim.Count, SettingControl.Capacity, Min: 1, Max: 1_000_000,
                Field: nameof(SystemSettingsUpdate.CalendarIcsMaxImportEvents),
                Load: (p, dto) => p.SetCapacityLoaded("calendarIcsMaxImportEvents", dto.CalendarIcsMaxImportEvents), Write: (p, req) => req.CalendarIcsMaxImportEvents = p.CapacityRequest("calendarIcsMaxImportEvents")),
            new("calendarIcsMaxImportMegabytes", "sd_storage", "Maximum import file size",
                "Largest iCalendar (.ics) upload accepted. Above ~64 MB, also raise the reverse proxy's body-size limit.",
                SettingClaim.Security, SettingControl.Size, Min: 1, Max: 1024,
                Field: nameof(SystemSettingsUpdate.CalendarIcsMaxImportMegabytes),
                Load: (p, dto) => p.SetIntLoaded("calendarIcsMaxImportMegabytes", dto.CalendarIcsMaxImportMegabytes), Write: (p, req) => req.CalendarIcsMaxImportMegabytes = p.IntRequest("calendarIcsMaxImportMegabytes")),
            new("calendarIcsMaxExportMegabytes", "sd_storage", "Maximum export file size",
                "Largest iCalendar (.ics) file an export may produce. A too-large export is truncated "
                + "rather than exceeding this, so the download fails cleanly instead of silently.",
                SettingClaim.Security, SettingControl.Size, Min: 1, Max: 1024,
                Field: nameof(SystemSettingsUpdate.CalendarIcsMaxExportMegabytes),
                Load: (p, dto) => p.SetIntLoaded("calendarIcsMaxExportMegabytes", dto.CalendarIcsMaxExportMegabytes), Write: (p, req) => req.CalendarIcsMaxExportMegabytes = p.IntRequest("calendarIcsMaxExportMegabytes")),
            // ── issue #434 keys 8-10. These three bound the ALL-CALENDARS export/import path, not a
            // single calendar's, which is why they belong here rather than in "Calendar limits". Their
            // ceilings are twice the shipped default, derived from the concurrency actually permitted on
            // this surface (two import permits globally, two exports per user, four exports globally),
            // so worst-case concurrent materialisation stays within the same order as today's.
            new("calendarIcsMaxAggregateExportRows", "file_download", "Aggregate export fetch guard",
                "How many events an unfiltered all-calendars export may fetch before it refuses. Bounds "
                + "the intermediate work, not the output size — the export row cap above is that.",
                SettingClaim.Count, SettingControl.Number, Min: 1, Max: 40000,
                Field: nameof(SystemSettingsUpdate.CalendarIcsMaxAggregateExportRows),
                Load: (p, dto) => p.SetIntLoaded(
                    "calendarIcsMaxAggregateExportRows", dto.CalendarIcsMaxAggregateExportRows),
                Write: (p, req) => req.CalendarIcsMaxAggregateExportRows =
                    p.IntRequest("calendarIcsMaxAggregateExportRows"),
                MaxFrom: dto => dto.CalendarIcsMaxAggregateExportRowsCeiling),
            new("calendarIcsMaxAggregateOccurrences", "autorenew", "Max generated occurrences per import",
                "How many events one import may create or regenerate in total, including every occurrence "
                + "of a recurring series. All of them are held in memory while the import runs.",
                SettingClaim.Count, SettingControl.Number, Min: 1, Max: 20000,
                Field: nameof(SystemSettingsUpdate.CalendarIcsMaxAggregateOccurrences),
                Load: (p, dto) => p.SetIntLoaded(
                    "calendarIcsMaxAggregateOccurrences", dto.CalendarIcsMaxAggregateOccurrences),
                Write: (p, req) => req.CalendarIcsMaxAggregateOccurrences =
                    p.IntRequest("calendarIcsMaxAggregateOccurrences"),
                MaxFrom: dto => dto.CalendarIcsMaxAggregateOccurrencesCeiling),
            new("calendarIcsMaxAggregateExportWindowDays", "event_available", "Aggregate export window",
                "The widest date range an all-calendars export may span in one request.",
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
                "Upper limit on the VTODOs an iCalendar (.ics) export may produce. \"No limit\" keeps exports unbounded.",
                SettingClaim.Count, SettingControl.Capacity, Min: 1, Max: 1_000_000,
                Field: nameof(SystemSettingsUpdate.TaskIcsMaxExportTasks),
                Load: (p, dto) => p.SetCapacityLoaded("taskIcsMaxExportTasks", dto.TaskIcsMaxExportTasks), Write: (p, req) => req.TaskIcsMaxExportTasks = p.CapacityRequest("taskIcsMaxExportTasks")),
            new("taskIcsMaxImportTasks", "file_upload", "Maximum tasks per import",
                "Upper limit on the VTODOs accepted from an imported .ics file.",
                SettingClaim.Count, SettingControl.Capacity, Min: 1, Max: 1_000_000,
                Field: nameof(SystemSettingsUpdate.TaskIcsMaxImportTasks),
                Load: (p, dto) => p.SetCapacityLoaded("taskIcsMaxImportTasks", dto.TaskIcsMaxImportTasks), Write: (p, req) => req.TaskIcsMaxImportTasks = p.CapacityRequest("taskIcsMaxImportTasks")),
            new("taskIcsMaxImportMegabytes", "sd_storage", "Maximum import file size",
                "Largest iCalendar (.ics) upload accepted. Above ~64 MB, also raise the reverse proxy's body-size limit.",
                SettingClaim.Security, SettingControl.Size, Min: 1, Max: 1024,
                Field: nameof(SystemSettingsUpdate.TaskIcsMaxImportMegabytes),
                Load: (p, dto) => p.SetIntLoaded("taskIcsMaxImportMegabytes", dto.TaskIcsMaxImportMegabytes), Write: (p, req) => req.TaskIcsMaxImportMegabytes = p.IntRequest("taskIcsMaxImportMegabytes")),
            new("taskIcsMaxExportMegabytes", "sd_storage", "Maximum export file size",
                "Largest iCalendar (.ics) file an export may produce. A too-large export is truncated "
                + "rather than exceeding this, so the download fails cleanly instead of silently.",
                SettingClaim.Security, SettingControl.Size, Min: 1, Max: 1024,
                Field: nameof(SystemSettingsUpdate.TaskIcsMaxExportMegabytes),
                Load: (p, dto) => p.SetIntLoaded("taskIcsMaxExportMegabytes", dto.TaskIcsMaxExportMegabytes), Write: (p, req) => req.TaskIcsMaxExportMegabytes = p.IntRequest("taskIcsMaxExportMegabytes")),
        ],
        RoundTrip: new RoundTripPair("taskIcsMaxExportTasks", "taskIcsMaxImportTasks")),
        new("Journal import & export", Icons.Material.Filled.MenuBook,
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
            new("journalIcsMaxImportMegabytes", "sd_storage", "Maximum import file size",
                "Largest iCalendar (.ics) upload accepted. Above ~64 MB, also raise the reverse proxy's body-size limit.",
                SettingClaim.Security, SettingControl.Size, Min: 1, Max: 1024,
                Field: nameof(SystemSettingsUpdate.JournalIcsMaxImportMegabytes),
                Load: (p, dto) => p.SetIntLoaded("journalIcsMaxImportMegabytes", dto.JournalIcsMaxImportMegabytes), Write: (p, req) => req.JournalIcsMaxImportMegabytes = p.IntRequest("journalIcsMaxImportMegabytes")),
            new("journalIcsMaxExportMegabytes", "sd_storage", "Maximum export file size",
                "Largest iCalendar (.ics) file an export may produce. A too-large export is truncated "
                + "rather than exceeding this, so the download fails cleanly instead of silently.",
                SettingClaim.Security, SettingControl.Size, Min: 1, Max: 1024,
                Field: nameof(SystemSettingsUpdate.JournalIcsMaxExportMegabytes),
                Load: (p, dto) => p.SetIntLoaded("journalIcsMaxExportMegabytes", dto.JournalIcsMaxExportMegabytes), Write: (p, req) => req.JournalIcsMaxExportMegabytes = p.IntRequest("journalIcsMaxExportMegabytes")),
        ],
        RoundTrip: new RoundTripPair("journalIcsMaxExportRows", "journalIcsMaxImportEntries")),
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
            new("fileAnalysisEnabled", "power_settings_new", "AI document analysis",
                "When off, no document is sent for analysis and every analysis endpoint answers 503. "
                + "Turning it on does not by itself transfer anything: each analysis still requires the "
                + "user's per-document consent.",
                SettingClaim.Security,
                Field: nameof(SystemSettingsUpdate.FileAnalysisEnabled),
                Load: (p, dto) => p.SetBoolLoaded("fileAnalysisEnabled", dto.FileAnalysisEnabled),
                Write: (p, req) => req.FileAnalysisEnabled = p.BoolRequest("fileAnalysisEnabled")),
            new("fileAnalysisModel", "badge", "Model",
                "The model each analysis is sent to, and the model recorded against it. Analyses already "
                + "completed keep the model they ran under.",
                SettingClaim.Security, SettingControl.Text, Max: 128,
                Field: nameof(SystemSettingsUpdate.FileAnalysisModel),
                Load: (p, dto) => p.SetTextLoaded("fileAnalysisModel", dto.FileAnalysisModel),
                Write: (p, req) => req.FileAnalysisModel = p.TextRequest("fileAnalysisModel")),
            new("fileAnalysisBaseUrl", "send", "Provider base URL",
                "Where analysis requests are sent. Must be an absolute https:// address with no path — "
                + "the provider appends /v1/messages itself. The configured API key is sent to this "
                + "host, so change it only to a host you control or trust.",
                SettingClaim.Security, SettingControl.Text, Max: 256,
                Field: nameof(SystemSettingsUpdate.FileAnalysisBaseUrl),
                Load: (p, dto) => p.SetTextLoaded("fileAnalysisBaseUrl", dto.FileAnalysisBaseUrl),
                Write: (p, req) => req.FileAnalysisBaseUrl = p.TextRequest("fileAnalysisBaseUrl"),
                InputMode: "url", Placeholder: "https://…"),
            new("fileAnalysisProcessor", "apartment", "Data processor",
                "The third party uploaded documents are sent to for analysis. Shown in the consent gate before every transfer.",
                SettingClaim.Security, SettingControl.Text, Max: 128,
                Field: nameof(SystemSettingsUpdate.FileAnalysisProcessor),
                Load: (p, dto) => p.SetTextLoaded("fileAnalysisProcessor", dto.FileAnalysisProcessor),
                Write: (p, req) => req.FileAnalysisProcessor = p.TextRequest("fileAnalysisProcessor")),
            new("fileAnalysisProcessorRegion", "location_on", "Processor region",
                "Where that processing happens. This is the disclosure that decides adequacy versus standard contractual clauses under GDPR Chapter V — and it cannot be verified automatically, so check it against your processor's terms.",
                SettingClaim.Security, SettingControl.Text, Max: 128,
                Field: nameof(SystemSettingsUpdate.FileAnalysisProcessorRegion),
                Load: (p, dto) => p.SetTextLoaded("fileAnalysisProcessorRegion", dto.FileAnalysisProcessorRegion),
                Write: (p, req) => req.FileAnalysisProcessorRegion = p.TextRequest("fileAnalysisProcessorRegion")),
            new("fileAnalysisLawfulBasis", "gavel", "Lawful basis",
                "Recorded verbatim against every analysis, so each record keeps the basis asserted at the time of that transfer.",
                SettingClaim.Security, SettingControl.Text, Max: 128,
                Field: nameof(SystemSettingsUpdate.FileAnalysisLawfulBasis),
                Load: (p, dto) => p.SetTextLoaded("fileAnalysisLawfulBasis", dto.FileAnalysisLawfulBasis),
                Write: (p, req) => req.FileAnalysisLawfulBasis = p.TextRequest("fileAnalysisLawfulBasis")),
            new("fileAnalysisPrivacyNoticeUrl", "link", "Privacy notice URL",
                "Linked from the consent gate. Must be an absolute https:// address — it is rendered as a link, so other schemes are rejected.",
                SettingClaim.Security, SettingControl.Text, Max: 256,
                Field: nameof(SystemSettingsUpdate.FileAnalysisPrivacyNoticeUrl),
                Load: (p, dto) => p.SetTextLoaded("fileAnalysisPrivacyNoticeUrl", dto.FileAnalysisPrivacyNoticeUrl),
                Write: (p, req) => req.FileAnalysisPrivacyNoticeUrl = p.TextRequest("fileAnalysisPrivacyNoticeUrl"),
                InputMode: "url", Placeholder: "https://…"),
            new("fileAnalysisMaxFutureTransactionDays", "event_available", "Future-dated transaction window",
                "How many days ahead of today an extracted transaction date may fall before it is discarded as a misread.",
                SettingClaim.Count, SettingControl.Number, Min: 1, Max: 3650,
                Field: nameof(SystemSettingsUpdate.FileAnalysisMaxFutureTransactionDays),
                Load: (p, dto) => p.SetIntLoaded("fileAnalysisMaxFutureTransactionDays", dto.FileAnalysisMaxFutureTransactionDays),
                Write: (p, req) => req.FileAnalysisMaxFutureTransactionDays = p.IntRequest("fileAnalysisMaxFutureTransactionDays"),
                Unit: "days"),
            new("fileAnalysisMatchAutoLinkThreshold", "percent", "Auto-link confidence threshold",
                "Matches at or above this confidence are linked automatically; below it they are suggestions only. Raising it links less and asks more. Analyses already completed keep the threshold they ran under.",
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
                "Caps the model's output on each analysis. Raising it allows more complete extraction from "
                + "long statements and costs proportionally more per document.",
                SettingClaim.Security, SettingControl.Number, Min: 1024, Max: 64000,
                Field: nameof(SystemSettingsUpdate.FileAnalysisMaxTokens),
                Load: (p, dto) => p.SetIntLoaded("fileAnalysisMaxTokens", dto.FileAnalysisMaxTokens),
                Write: (p, req) => req.FileAnalysisMaxTokens = p.IntRequest("fileAnalysisMaxTokens")),
            new("fileAnalysisMatchMaxVocabulary", "list_alt", "Match vocabulary limit",
                "How many contact and tag names may be sent for matching. Over the limit the matching step "
                + "is skipped entirely rather than run on a subset.",
                SettingClaim.Count, SettingControl.Number, Min: 1, Max: 5000,
                Field: nameof(SystemSettingsUpdate.FileAnalysisMatchMaxVocabulary),
                Load: (p, dto) => p.SetIntLoaded("fileAnalysisMatchMaxVocabulary", dto.FileAnalysisMatchMaxVocabulary),
                Write: (p, req) => req.FileAnalysisMatchMaxVocabulary = p.IntRequest("fileAnalysisMatchMaxVocabulary")),
            new("fileAnalysisMatchTimeoutSeconds", "history_toggle_off", "Match timeout",
                "How long one matching call may take before it is abandoned. On timeout the extracted "
                + "transactions stay importable by hand.",
                SettingClaim.Count, SettingControl.Number, Min: 5, Max: 600,
                Field: nameof(SystemSettingsUpdate.FileAnalysisMatchTimeoutSeconds),
                Load: (p, dto) => p.SetIntLoaded("fileAnalysisMatchTimeoutSeconds", dto.FileAnalysisMatchTimeoutSeconds),
                Write: (p, req) => req.FileAnalysisMatchTimeoutSeconds = p.IntRequest("fileAnalysisMatchTimeoutSeconds"),
                Unit: "sec"),
        ]),
        // ── Transactional email (issue #421 Wave 2) ───────────────────────────────────────────────
        // Row icons are ligatures the client already renders elsewhere — see the note above the File
        // analysis section for why an unproven name is not a safe guess here.
        //
        // The SMTP host, port and TLS mode are deliberately absent: they stay in deploy-time config,
        // because the sender connects to the host and THEN authenticates, so a writable host would
        // harvest the relay credential along with every reset token (issue #421 Non-Goal 2).
        new("Email", Icons.Material.Filled.MarkEmailRead,
        [
            new("emailFromAddress", "mail", "From address",
                "The envelope sender on every confirmation and password-reset email. Must remain an address your mail relay is authorised to send as, or delivery fails silently.",
                SettingClaim.Security, SettingControl.Text, Max: 256,
                Field: nameof(SystemSettingsUpdate.EmailFromAddress),
                Load: (p, dto) => p.SetTextLoaded("emailFromAddress", dto.EmailFromAddress),
                Write: (p, req) => req.EmailFromAddress = p.TextRequest("emailFromAddress"),
                InputMode: "email"),
            new("emailFromName", "badge", "From name",
                "The display name shown beside the sender address in a recipient's inbox.",
                SettingClaim.Security, SettingControl.Text, Max: 128,
                Field: nameof(SystemSettingsUpdate.EmailFromName),
                Load: (p, dto) => p.SetTextLoaded("emailFromName", dto.EmailFromName),
                Write: (p, req) => req.EmailFromName = p.TextRequest("emailFromName")),
            new("emailPerRecipientLimit", "send", "Messages per recipient",
                "How many transactional emails one address may receive per window. This is what stops a rotating-IP source mailbombing a single mailbox, so lowering it takes effect on the very next send.",
                SettingClaim.Security, SettingControl.Number, Min: 1, Max: 1000,
                Field: nameof(SystemSettingsUpdate.EmailPerRecipientLimit),
                Load: (p, dto) => p.SetIntLoaded("emailPerRecipientLimit", dto.EmailPerRecipientLimit),
                Write: (p, req) => req.EmailPerRecipientLimit = p.IntRequest("emailPerRecipientLimit")),
            new("emailPerRecipientWindowMinutes", "history_toggle_off", "Recipient window",
                "The length of that window, in minutes. A longer window is a tighter limit.",
                SettingClaim.Security, SettingControl.Number, Min: 1, Max: 1440,
                Field: nameof(SystemSettingsUpdate.EmailPerRecipientWindowMinutes),
                Load: (p, dto) => p.SetIntLoaded("emailPerRecipientWindowMinutes", dto.EmailPerRecipientWindowMinutes),
                Write: (p, req) => req.EmailPerRecipientWindowMinutes = p.IntRequest("emailPerRecipientWindowMinutes"),
                Unit: "min"),
            // ── issue #434 key 14. RAISE-ONLY, and the first row on this page with a server-published
            // FLOOR — the reason SettingItem needed MinFrom to mirror MaxFrom. The static Min names the
            // same shared constant the server's [Range] minimum does, so the load-phase fallback and
            // the published floor are the same number rather than two literals that could drift.
            new("emailMaxTrackedRecipients", "send", "Tracked recipient addresses",
                "How many distinct addresses the per-recipient limit tracks at once. Once full, mail to an "
                + "untracked address is allowed through. A change is not instant in either direction: "
                + "existing entries age out over up to a full window.",
                SettingClaim.Security, SettingControl.Number,
                Min: SystemSettingsDefaults.EmailMaxTrackedRecipients, Max: 200000,
                Field: nameof(SystemSettingsUpdate.EmailMaxTrackedRecipients),
                Load: (p, dto) => p.SetIntLoaded("emailMaxTrackedRecipients", dto.EmailMaxTrackedRecipients),
                Write: (p, req) => req.EmailMaxTrackedRecipients = p.IntRequest("emailMaxTrackedRecipients"),
                MinFrom: dto => dto.EmailMaxTrackedRecipientsFloor),
        ]),
        // ── Per-request caps (issue #421 Wave 3) ──────────────────────────────────────────────────
        // These were invisible before: the Contracts and PhotoLibrary config sections had no
        // appsettings entry at all, and the two journal caps were `private const` in their services.
        new("Contracts", Icons.Material.Filled.Description,
        [
            new("contractMaxPartiesPerContract", "group_add", "Max parties per contract",
                "Upper limit on parties linked to one contract. A request over the cap is rejected.",
                SettingClaim.Count, SettingControl.Number, Min: SystemSettingsBounds.ContractMaxPartiesPerContractMin, Max: SystemSettingsBounds.ContractMaxPartiesPerContractMax,
                Field: nameof(SystemSettingsUpdate.ContractMaxPartiesPerContract),
                Load: (p, dto) => p.SetIntLoaded("contractMaxPartiesPerContract", dto.ContractMaxPartiesPerContract),
                Write: (p, req) => req.ContractMaxPartiesPerContract = p.IntRequest("contractMaxPartiesPerContract")),
            new("contractMaxFilesPerContract", "attach_file", "Max files per contract",
                "Upper limit on files attached to one contract.",
                SettingClaim.Count, SettingControl.Number, Min: SystemSettingsBounds.ContractMaxFilesPerContractMin, Max: SystemSettingsBounds.ContractMaxFilesPerContractMax,
                Field: nameof(SystemSettingsUpdate.ContractMaxFilesPerContract),
                Load: (p, dto) => p.SetIntLoaded("contractMaxFilesPerContract", dto.ContractMaxFilesPerContract),
                Write: (p, req) => req.ContractMaxFilesPerContract = p.IntRequest("contractMaxFilesPerContract")),
            new("contractMaxSummaryContracts", "list_alt", "Max contracts in summary",
                "Safety ceiling on how many contracts the dashboard summary aggregates over.",
                SettingClaim.Count, SettingControl.Number, Min: SystemSettingsBounds.ContractMaxSummaryContractsMin, Max: SystemSettingsBounds.ContractMaxSummaryContractsMax,
                Field: nameof(SystemSettingsUpdate.ContractMaxSummaryContracts),
                Load: (p, dto) => p.SetIntLoaded("contractMaxSummaryContracts", dto.ContractMaxSummaryContracts),
                Write: (p, req) => req.ContractMaxSummaryContracts = p.IntRequest("contractMaxSummaryContracts")),
        ]),
        // The two photo caps are TIGHTEN-ONLY, and this is the first use of MaxFrom: their ceiling is
        // a compile-time constant that also drives [MaxLength] on the photo request DTOs, so model
        // validation would reject an over-cap request before the setting was ever consulted. The
        // control bounds itself from the server-supplied ceiling rather than offering 100,000 and
        // letting the save fail.
        new("Photos", Icons.Material.Filled.PhotoLibrary,
        [
            new("photoMaxLinksPerKind", "sell", "Max links per photo",
                "Upper limit on tags, people or albums linked to one photo. The same limit is enforced by request validation.",
                SettingClaim.Count, SettingControl.Number, Min: 1, Max: 50,
                Field: nameof(SystemSettingsUpdate.PhotoMaxLinksPerKind),
                Load: (p, dto) => p.SetIntLoaded("photoMaxLinksPerKind", dto.PhotoMaxLinksPerKind),
                Write: (p, req) => req.PhotoMaxLinksPerKind = p.IntRequest("photoMaxLinksPerKind"),
                MaxFrom: dto => dto.PhotoMaxLinksPerKindCeiling),
            new("photoMaxAlbumMembers", "photo_album", "Max photos per album",
                "Upper limit on photos in one album. The same limit is enforced by request validation.",
                SettingClaim.Count, SettingControl.Number, Min: 1, Max: 1000,
                Field: nameof(SystemSettingsUpdate.PhotoMaxAlbumMembers),
                Load: (p, dto) => p.SetIntLoaded("photoMaxAlbumMembers", dto.PhotoMaxAlbumMembers),
                Write: (p, req) => req.PhotoMaxAlbumMembers = p.IntRequest("photoMaxAlbumMembers"),
                MaxFrom: dto => dto.PhotoMaxAlbumMembersCeiling),
            // ── issue #434 keys 4-5. The read size takes the SECURITY claim: all nine existing
            // megabyte rows do, every one of them is also a resource bound, and on the ordinary claim
            // this would be the only unaudited megabyte cap in a ten-row set — governing a per-upload
            // byte-array multiplier reachable by every uploader.
            new("photoMetadataReadMegabytes", "sd_storage", "Metadata read size",
                "How much of an uploaded image is read to extract its metadata. A whole block of this size "
                + "is held in memory per photo, and 16 MB is the practical ceiling because that is MariaDB's "
                + "default maximum packet size.",
                SettingClaim.Security, SettingControl.Size, Min: 1, Max: 16,
                Field: nameof(SystemSettingsUpdate.PhotoMetadataReadMegabytes),
                Load: (p, dto) => p.SetIntLoaded("photoMetadataReadMegabytes", dto.PhotoMetadataReadMegabytes),
                Write: (p, req) => req.PhotoMetadataReadMegabytes = p.IntRequest("photoMetadataReadMegabytes"),
                MaxFrom: dto => dto.PhotoMetadataReadMegabytesCeiling),
            new("photoMetadataExtractionTimeoutSeconds", "schedule", "Metadata extraction timeout",
                "How long one extraction may take. On timeout the photo is still stored — only its "
                + "extracted metadata is missing.",
                SettingClaim.Count, SettingControl.Number, Min: 1, Max: 120,
                Field: nameof(SystemSettingsUpdate.PhotoMetadataExtractionTimeoutSeconds),
                Load: (p, dto) => p.SetIntLoaded(
                    "photoMetadataExtractionTimeoutSeconds", dto.PhotoMetadataExtractionTimeoutSeconds),
                Write: (p, req) => req.PhotoMetadataExtractionTimeoutSeconds =
                    p.IntRequest("photoMetadataExtractionTimeoutSeconds"),
                Unit: "sec"),
        ]),
        // The upload cap is ceiling-bounded like the two photo caps, but its ceiling is startup
        // configuration rather than a compile-time constant: raising it past the transport limit would
        // be refused by Kestrel before the setting was ever read.
        new("Storage", Icons.Material.Filled.Storage,
        [
            new("fileStorageMaxUploadMegabytes", "cloud_upload", "Maximum upload size",
                "Largest file accepted by any upload surface. Can be lowered freely; raising it needs the server's transport limit raised first.",
                SettingClaim.Security, SettingControl.Size, Min: 1, Max: 1024,
                Field: nameof(SystemSettingsUpdate.FileStorageMaxUploadMegabytes),
                Load: (p, dto) => p.SetIntLoaded("fileStorageMaxUploadMegabytes", dto.FileStorageMaxUploadMegabytes),
                Write: (p, req) => req.FileStorageMaxUploadMegabytes = p.IntRequest("fileStorageMaxUploadMegabytes"),
                MaxFrom: dto => dto.UploadMegabytesCeiling),
        ]),
        new("Journal limits", Icons.Material.Filled.MenuBook,
        [
            new("journalEntryMaxLinksPerKind", "link", "Max links per journal entry",
                "Upper limit on tags, contacts, photos or attachments linked to one entry.",
                SettingClaim.Count, SettingControl.Number, Min: 1, Max: 100000,
                Field: nameof(SystemSettingsUpdate.JournalEntryMaxLinksPerKind),
                Load: (p, dto) => p.SetIntLoaded("journalEntryMaxLinksPerKind", dto.JournalEntryMaxLinksPerKind),
                Write: (p, req) => req.JournalEntryMaxLinksPerKind = p.IntRequest("journalEntryMaxLinksPerKind")),
            new("journalTaskMaxLinksPerKind", "checklist", "Max links per task",
                "Upper limit on tags or attachments linked to one task.",
                SettingClaim.Count, SettingControl.Number, Min: 1, Max: 100000,
                Field: nameof(SystemSettingsUpdate.JournalTaskMaxLinksPerKind),
                Load: (p, dto) => p.SetIntLoaded("journalTaskMaxLinksPerKind", dto.JournalTaskMaxLinksPerKind),
                Write: (p, req) => req.JournalTaskMaxLinksPerKind = p.IntRequest("journalTaskMaxLinksPerKind")),
        ]),
        // ── Calendar limits (issue #434 keys 6-10) ────────────────────────────────────────────────
        // A group of its own rather than rows on "Calendars import & export": three of the five govern
        // ordinary calendar reads and writes, not import or export, so filing them there would state
        // something untrue about their scope.
        new("Calendar limits", Icons.Material.Filled.EventNote,
        [
            new("calendarMaxWindowDays", "event_available", "Maximum calendar window",
                "The widest date range one calendar view may request at a time.",
                SettingClaim.Count, SettingControl.Number, Min: 1, Max: 3650,
                Field: nameof(SystemSettingsUpdate.CalendarMaxWindowDays),
                Load: (p, dto) => p.SetIntLoaded("calendarMaxWindowDays", dto.CalendarMaxWindowDays),
                Write: (p, req) => req.CalendarMaxWindowDays = p.IntRequest("calendarMaxWindowDays"),
                Unit: "days"),
            new("calendarMaxEventDurationDays", "schedule", "Maximum event duration",
                "How long a single event may span. Applies to events created in the app and to events "
                + "brought in by an import.",
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
                "How many events one recurrence rule may create. Each occurrence is stored as its own "
                + "calendar entry.",
                SettingClaim.Count, SettingControl.Number,
                Min: 1, Max: SystemSettingsDefaults.RecurrenceMaxGeneratedOccurrences,
                Field: nameof(SystemSettingsUpdate.RecurrenceMaxGeneratedOccurrences),
                Load: (p, dto) => p.SetIntLoaded(
                    "recurrenceMaxGeneratedOccurrences", dto.RecurrenceMaxGeneratedOccurrences),
                Write: (p, req) => req.RecurrenceMaxGeneratedOccurrences =
                    p.IntRequest("recurrenceMaxGeneratedOccurrences"),
                MaxFrom: dto => dto.RecurrenceMaxGeneratedOccurrencesCeiling),
        ]),
        // ── Import & export defaults (issue #434 key 13) ──────────────────────────────────────────
        // A one-row group on purpose: this bound applies to ALL FOUR importers at once, so filing it
        // under any single per-surface group would state, wrongly, that it is surface-scoped. A one-row
        // group is not a new precedent — Data and Storage are both one-row groups already.
        new("Import & export defaults", Icons.Material.Filled.ImportExport,
        [
            new("importMaxSamplesPerSkipReason", "file_upload", "Samples per skip reason",
                "How many example titles an import summary keeps for each reason something was skipped. "
                + "The counts are always exact; only the examples are capped.",
                SettingClaim.Count, SettingControl.Number, Min: 1, Max: 10000,
                Field: nameof(SystemSettingsUpdate.ImportMaxSamplesPerSkipReason),
                Load: (p, dto) => p.SetIntLoaded("importMaxSamplesPerSkipReason", dto.ImportMaxSamplesPerSkipReason),
                Write: (p, req) => req.ImportMaxSamplesPerSkipReason =
                    p.IntRequest("importMaxSamplesPerSkipReason")),
        ]),
        // ── Accounts (issue #434 key 15) ──────────────────────────────────────────────────────────
        new("Accounts", Icons.Material.Filled.AccountBalance,
        [
            new("accountMaxSmartTagsPerAccount", "sell", "Max smart tags per account",
                "Upper limit on the saved tag filters one account may carry. The Accounts page reads this "
                + "value directly, so a change takes effect there without a reload.",
                SettingClaim.Count, SettingControl.Number, Min: 1, Max: 1000,
                Field: nameof(SystemSettingsUpdate.AccountMaxSmartTagsPerAccount),
                Load: (p, dto) => p.SetIntLoaded(
                    "accountMaxSmartTagsPerAccount", dto.AccountMaxSmartTagsPerAccount),
                Write: (p, req) => req.AccountMaxSmartTagsPerAccount =
                    p.IntRequest("accountMaxSmartTagsPerAccount")),
        ]),
        // ── Subscriptions (issue #437) ────────────────────────────────────────────────────────────
        // APPENDED, not filed beside the other two finance groups: this catalogue is ordered
        // wave-chronologically (Insurance is group 3, Contracts group 10, with six unrelated groups
        // between), so appending is the convention rather than a compromise.
        //
        // Min/Max name their SystemSettingsBounds pair rather than restating literals. A shared bound
        // has FOUR ends, not two: the [Range], the descriptor, the read-path clamp, and the control's
        // rendered max — the last resolving from item.Max via MaxFor, which is also what the page's own
        // range check uses.
        new("Subscriptions", Icons.Material.Filled.Subscriptions,
        [
            new("subscriptionRenewalWindowDays", "schedule", "Upcoming renewals window",
                "How many days ahead a subscription's next billing date is surfaced as an upcoming renewal.",
                SettingClaim.Count, SettingControl.Number,
                Min: SystemSettingsBounds.SubscriptionRenewalWindowDaysMin,
                Max: SystemSettingsBounds.SubscriptionRenewalWindowDaysMax,
                Field: nameof(SystemSettingsUpdate.SubscriptionRenewalWindowDays),
                Load: (p, dto) => p.SetIntLoaded("subscriptionRenewalWindowDays", dto.SubscriptionRenewalWindowDays),
                Write: (p, req) => req.SubscriptionRenewalWindowDays = p.IntRequest("subscriptionRenewalWindowDays"),
                Unit: "days"),
            // The description says "a separate rendered block above the list" rather than "announced by
            // screen readers" deliberately: the screen-reader phrasing goes false the moment the
            // roll-up's per-row live regions become one, while the payload/render justification for the
            // cap stays true either way.
            new("subscriptionMaxSummaryRenewals", "format_list_numbered", "Max renewals shown in summary",
                "Upper limit on the renewal rows listed in the page-header roll-up. Each row is a separate "
                + "rendered block above the list, so this is deliberately bounded well below the other summary caps.",
                SettingClaim.Count, SettingControl.Number,
                Min: SystemSettingsBounds.SubscriptionMaxSummaryRenewalsMin,
                Max: SystemSettingsBounds.SubscriptionMaxSummaryRenewalsMax,
                Field: nameof(SystemSettingsUpdate.SubscriptionMaxSummaryRenewals),
                Load: (p, dto) => p.SetIntLoaded("subscriptionMaxSummaryRenewals", dto.SubscriptionMaxSummaryRenewals),
                Write: (p, req) => req.SubscriptionMaxSummaryRenewals = p.IntRequest("subscriptionMaxSummaryRenewals")),
            // "inventory", not "subscriptions", which would duplicate the group icon — and it does not
            // collide with the Subscriptions page's own Archived glyph, "inventory_2".
            new("subscriptionMaxSummarySubscriptions", "inventory", "Max subscriptions read for summary",
                "Upper limit on the subscriptions read to compute the roll-up. Beyond it the counts, run-rate "
                + "AND the upcoming-renewals list cover the most recent subscriptions only.",
                SettingClaim.Count, SettingControl.Number,
                Min: SystemSettingsBounds.SubscriptionMaxSummarySubscriptionsMin,
                Max: SystemSettingsBounds.SubscriptionMaxSummarySubscriptionsMax,
                Field: nameof(SystemSettingsUpdate.SubscriptionMaxSummarySubscriptions),
                Load: (p, dto) => p.SetIntLoaded("subscriptionMaxSummarySubscriptions", dto.SubscriptionMaxSummarySubscriptions),
                Write: (p, req) => req.SubscriptionMaxSummarySubscriptions = p.IntRequest("subscriptionMaxSummarySubscriptions")),
        ]),
    ];

    private bool Editable(SettingClaim claim) => claim switch
    {
        SettingClaim.Security => _hasSecurityUpdate,
        SettingClaim.Count => _hasCountUpdate,
        _ => true,
    };

    private bool IsRowDisabled(SettingItem item) => item.Control switch
    {
        SettingControl.Export => false,
        _ => !Editable(item.Claim) || _phase != Phase.Ready || _isSaving,
    };

    private bool Matches(SettingItem item, string group) => Matches(item, group, _search);

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

    // Export controls only earn a place once the server confirms each is available (permission);
    // every other setting is always present.
    private bool IsAvailable(SettingItem item) => item.Control switch
    {
        SettingControl.Export => _exportAvailable,
        _ => true,
    };

    private IReadOnlyList<SettingSection> VisibleSections =>
        Sections
            .Select(s => s with { Items = s.Items.Where(IsAvailable).Where(i => Matches(i, s.Group)).ToList() })
            // A section survives on its CREDENTIALS alone (issue #445). Searching "SMTP password"
            // filters away every plaintext row in Email, and dropping the section there would hide the
            // one row that matched — the search would answer "no settings match" for a term naming a
            // field that is on the page.
            .Where(s => s.Items.Count > 0 || SecretsIn(s).Count > 0)
            .ToList();

    // ── Draft state — a dictionary of setting-key → draft record per control shape, so adding a
    //    setting is a catalog entry (above) plus one ApplyLoaded line, rather than five new
    //    per-key switch arms (issue #343 — five key-switch methods growing to sixteen arms is the
    //    wrong shape one commit after #399 split the god components). The original Security/
    //    Insurance settings moved onto the same structure. ─────────────────────────────────────
    private sealed class BoolState
    {
        public bool Draft;
        public bool Saved;
    }

    private sealed class IntState
    {
        public int? Draft;
        public int? Saved;
    }

    private sealed class CapacityState
    {
        public bool Unlimited;
        // Retained even while Unlimited is true — never cleared by a toggle (issue #343 fe R6), so
        // toggling to "No limit" and back restores the previously entered number.
        public int? Value;
        public bool SavedUnlimited;
        public int? SavedValue;
    }

    private sealed class TextState
    {
        public string Draft = string.Empty;
        public string Saved = string.Empty;
    }

    private sealed class DecimalState
    {
        public decimal? Draft;
        public decimal? Saved;
    }

    private readonly Dictionary<string, BoolState> _bools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IntState> _ints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CapacityState> _capacities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextState> _texts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DecimalState> _decimals = new(StringComparer.Ordinal);

    // Per-field messages the SERVER rejected, keyed by catalogue key — merged into ErrorFor so a 400
    // renders on the offending row rather than only in a toast (issue #421 Wave 0b, frontend B2).
    //
    // Both clearing rules matter: HasErrors disables Save AND Save() early-returns on it, so without
    // them the first server 400 would disable the button permanently, with no way back.
    private readonly Dictionary<string, string> _serverErrors = new(StringComparer.Ordinal);

    private void ClearServerError(string key) => _serverErrors.Remove(key);

    private void SetBoolLoaded(string key, bool value) =>
        _bools[key] = new BoolState { Draft = value, Saved = value };

    private void SetIntLoaded(string key, int value) =>
        _ints[key] = new IntState { Draft = value, Saved = value };

    private void SetCapacityLoaded(string key, int? value) =>
        _capacities[key] = new CapacityState
        {
            Unlimited = value is null,
            Value = value,
            SavedUnlimited = value is null,
            SavedValue = value,
        };

    private bool GetBool(string key) => _bools.TryGetValue(key, out var state) && state.Draft;

    private void SetBool(string key, bool value)
    {
        if (_bools.TryGetValue(key, out var state))
        {
            state.Draft = value;
        }

        ClearServerError(key);
        _justSaved = false;
    }

    internal void SetTextLoaded(string key, string value) =>
        _texts[key] = new TextState { Draft = value ?? string.Empty, Saved = value ?? string.Empty };

    internal void SetDecimalLoaded(string key, decimal value) =>
        _decimals[key] = new DecimalState { Draft = value, Saved = value };

    private string GetText(string key) => _texts.TryGetValue(key, out var state) ? state.Draft : string.Empty;

    private void SetText(string key, string? value)
    {
        if (_texts.TryGetValue(key, out var state))
        {
            state.Draft = value ?? string.Empty;
        }

        ClearServerError(key);
        _justSaved = false;
    }

    private decimal? GetDecimal(string key) => _decimals.TryGetValue(key, out var state) ? state.Draft : null;

    private void SetDecimal(string key, decimal? value)
    {
        if (_decimals.TryGetValue(key, out var state))
        {
            state.Draft = value;
        }

        ClearServerError(key);
        _justSaved = false;
    }

    private int? GetInt(string key) => _ints.TryGetValue(key, out var state) ? state.Draft : null;

    // decimal → int is a CHECKED cast in C# regardless of context, so a bare `(int)value.Value`
    // throws OverflowException the instant a typed number exceeds int range — synchronously, from
    // inside the oninput handler, with no ErrorBoundary anywhere in the client to catch it (issue
    // #403 fe finding). None of this page's Max values exceed 1,000,000, so clamping to the int
    // bounds rather than truncating is safe: an out-of-range value still lands on the wrong side of
    // ErrorFor's Min/Max check and surfaces as the normal "Must be between …" message instead of a
    // crashed page.
    private static int? ClampToInt(decimal? value) => value switch
    {
        null => null,
        > int.MaxValue => int.MaxValue,
        < int.MinValue => int.MinValue,
        { } v => (int)v,
    };

    private void SetInt(string key, decimal? value)
    {
        if (_ints.TryGetValue(key, out var state))
        {
            state.Draft = ClampToInt(value);
        }

        ClearServerError(key);
        _justSaved = false;
    }

    // TryGetValue, never an indexer. ErrorFor -> Capacity(key) runs on the RENDER path, and there is
    // no ErrorBoundary anywhere in the client, so a catalogue key whose Load never ran used to blank
    // the whole page with a KeyNotFoundException. The catalogue registry makes that unreachable and
    // a test asserts it; this is the belt to that braces, and it costs nothing.
    private CapacityState Capacity(string key) =>
        _capacities.TryGetValue(key, out var state) ? state : new CapacityState();

    private void SetCapacityValue(string key, decimal? value)
    {
        if (_capacities.TryGetValue(key, out var state))
        {
            state.Value = ClampToInt(value);
        }

        ClearServerError(key);
        _justSaved = false;
    }

    private void SetCapacityUnlimited(string key, bool unlimited, string title)
    {
        if (_capacities.TryGetValue(key, out var state))
        {
            // The number is NEVER cleared here (fe R6) — toggling back off restores it.
            state.Unlimited = unlimited;
        }

        _justSaved = false;
        Announce($"{title} — {(unlimited ? "no limit" : "limited")}");
    }

    private bool IsDirty(SettingItem item) => item.Control switch
    {
        SettingControl.Export => false,
        SettingControl.Capacity => _capacities.TryGetValue(item.Key, out var cap) &&
            (cap.Unlimited != cap.SavedUnlimited || (!cap.Unlimited && cap.Value != cap.SavedValue)),
        SettingControl.Toggle => _bools.TryGetValue(item.Key, out var b) && b.Draft != b.Saved,
        SettingControl.Text => _texts.TryGetValue(item.Key, out var s)
            && !string.Equals(s.Draft.Trim(), s.Saved, StringComparison.Ordinal),
        SettingControl.Decimal or SettingControl.Percent =>
            _decimals.TryGetValue(item.Key, out var d) && d.Draft != d.Saved,
        _ => _ints.TryGetValue(item.Key, out var i) && i.Draft != i.Saved,
    };

    // Client-side validation — only meaningful while the row is actually editable; a
    // permission-disabled row always sends null on save, never a stand-in for "cleared". A
    // capacity row is valid whenever Unlimited is true, regardless of Value (issue #343 fe 3-2):
    // the retained draft number is never itself an error.
    /// <summary>
    /// The effective upper bound for a row: a server-supplied ceiling when the catalogue declares one
    /// (issue #421 Waves 3/4 put the photo caps and the upload cap behind a DTO-carried ceiling),
    /// otherwise the static <see cref="SettingItem.Max"/>.
    ///
    /// <para>
    /// Both the rendered control's bound and this page's own range check resolve through here, so they
    /// cannot disagree. Binding the number control to the static <c>Max</c> instead would go unnoticed
    /// today — the catalogue's literal happens to equal the current compile-time ceiling — and would
    /// silently start refusing valid values the moment that ceiling was raised.
    /// </para>
    ///
    /// <para>
    /// The <c>_dto is null</c> fallback is load-bearing, not defensive: the Save button evaluates
    /// <c>HasErrors</c> inside its own <c>Disabled</c> expression, which renders during the Loading
    /// phase before any DTO exists. Without the fallback a ceiling-bounded row would resolve
    /// <c>Max = 0</c> and report "Must be between 1 and 0" on every page load.
    /// </para>
    /// </summary>
    private int MaxFor(SettingItem item) =>
        item.MaxFrom is { } resolve && _dto is { } dto ? resolve(dto) : item.Max;

    /// <summary>
    /// The effective lower bound for a row — the mirror of <see cref="MaxFor"/>, added for issue #434's
    /// one raise-only key (<c>EmailMaxTrackedRecipients</c>, whose floor is the shipped default because
    /// the mail throttle fails open once its table is full).
    ///
    /// <para>
    /// The <c>_dto is null</c> fallback is load-bearing for the same reason <see cref="MaxFor"/>'s is:
    /// the Save button evaluates <c>HasErrors</c> during the Loading phase, before any DTO exists.
    /// Unlike the ceiling case, the static <c>Min</c> in the catalogue is not a hopeful literal — it
    /// names the same <c>SystemSettingsDefaults</c> constant the server's <c>[Range]</c> does, so the
    /// fallback resolves to exactly the same number.
    /// </para>
    /// </summary>
    private int MinFor(SettingItem item) =>
        item.MinFrom is { } resolve && _dto is { } dto ? resolve(dto) : item.Min;

    private string? ErrorFor(SettingItem item)
    {
        if (!Editable(item.Claim))
            return null;

        // A server rejection outranks the client's own view: it is newer, and it is the one the user
        // just failed to save against.
        if (_serverErrors.TryGetValue(item.Key, out var fromServer))
            return fromServer;

        if (item.Control == SettingControl.Text)
        {
            var text = GetText(item.Key).Trim();
            if (text.Length == 0)
                return "Enter a value";
            if (item.Max > 0 && text.Length > item.Max)
                return $"Must be {item.Max:N0} characters or fewer";
            return null;
        }

        if (item.Control is SettingControl.Decimal or SettingControl.Percent)
        {
            var value = GetDecimal(item.Key);
            if (value is null)
                return "Enter a value";
            if (value < item.DecimalMin || value > item.DecimalMax)
            {
                // Phrased in the unit the field actually shows. A percent row displays 1-100, so
                // reporting its stored 0.01-1 bounds would name numbers the control cannot even
                // accept.
                return item.Control == SettingControl.Percent
                    ? $"Must be between {PercentOf(item.DecimalMin)} and {PercentOf(item.DecimalMax)}%"
                    : $"Must be between {item.DecimalMin:0.##} and {item.DecimalMax:0.##}";
            }

            return null;
        }

        if (item.Control == SettingControl.Capacity)
        {
            var cap = Capacity(item.Key);
            if (cap.Unlimited)
                return null;
            if (cap.Value is null)
                return "Enter a value";
            var capMin = MinFor(item);
            var capMax = MaxFor(item);
            if (cap.Value < capMin || cap.Value > capMax)
                return $"Must be between {capMin:N0} and {capMax:N0}";
            return null;
        }

        if (item.Control is SettingControl.Number or SettingControl.Size)
        {
            var value = GetInt(item.Key);
            if (value is null)
                return "Enter a value";
            var numMin = MinFor(item);
            var numMax = MaxFor(item);
            if (value < numMin || value > numMax)
                return $"Must be between {numMin:N0} and {numMax:N0}";
            return null;
        }

        return null;
    }

    // Effective count for a capacity draft — null represents "no limit" (+∞), matching the
    // server-side convention (issue #343 §9).
    private int? EffectiveCount(string key) => Capacity(key) is { Unlimited: true } ? null : Capacity(key).Value;

    // Round-trip: the export cap must not exceed the import cap (unlimited = +∞ on both sides).
    // Rendered as a GROUP-level alert, never on the export row (fe A5) — in the documented flow
    // the export row is disabled (unlimited is on), so an error placed there would be unfocusable.
    private string? RoundTripError(SettingSection section)
    {
        if (section.RoundTrip is not { } rt)
            return null;

        var export = EffectiveCount(rt.ExportKey);
        var import = EffectiveCount(rt.ImportKey);
        if (import is null) // unlimited import never violates, regardless of export
            return null;
        if (export is not null && export <= import)
            return null;

        var exportLabel = export is null ? "no limit" : export.Value.ToString("N0");
        var importLabel = import.Value.ToString("N0");
        return $"Export limit ({exportLabel}) must not exceed the import limit ({importLabel}), or an exported file could not be imported back.";
    }

    // Whether a rendered group mixes editable rows with claim-locked rows — the
    // partially-privileged admin who'd otherwise see greyed controls with no explanation (the
    // page-level read-only note below only covers the holds-NEITHER case) (issue #343 fe R8).
    private bool GroupPartial(SettingSection section)
    {
        if (!CanSave)
            return false;

        var claims = section.Items.Where(i => i.Claim != SettingClaim.None).ToList();
        return claims.Any(i => Editable(i.Claim)) && claims.Any(i => !Editable(i.Claim));
    }

    // Save is disabled while any CLIENT-DETECTABLE error is present, evaluated over rendered
    // (visible/search-filtered) sections only — field-level errors AND group round-trip conflicts.
    // Today's original page spanned all sections while only VisibleSections render, so an active
    // search could hide the row responsible for a disabled Save button (issue #343 fe B3).
    private bool HasErrors =>
        VisibleSections.Any(s => s.Items.Any(i => ErrorFor(i) is not null) || RoundTripError(s) is not null);

    private RenderFragment RenderControl(SettingItem item) => item.Control switch
    {
        SettingControl.Export => ExportControl(),
        SettingControl.Number => NumberControl(item),
        SettingControl.Size => SizeControl(item),
        SettingControl.Capacity => CapacityControl(item),
        SettingControl.Text => TextControl(item),
        SettingControl.Decimal => DecimalControl(item),
        SettingControl.Percent => PercentControl(item),
        _ => ToggleControl(item),
    };

    // ── The ids an OdsSettingField and its control share ──────────────────────────────────────────
    //
    // Four elements per row, each addressable: the label (also the jump target for both the problems
    // rollup and the fault summary), the control, the helper line and the error. Stated here once so
    // the field and the control cannot disagree about what describes what.
    private static string TitleId(string key) => $"ss-ttl-{key}";

    private static string ControlId(string key) => $"ss-in-{key}";

    private static string DescId(string key) => $"ss-desc-{key}";

    private static string ErrorId(string key) => $"ss-err-{key}";

    /// <summary>
    /// Whether the field's legend can be a real <c>&lt;label for&gt;</c>. A capacity row flips between
    /// a number input and static "No limit" text, so there is no id that is reliably focusable — it
    /// names itself through <c>aria-labelledby</c> on the title instead.
    /// </summary>
    private static bool HasFocusableControl(SettingItem item) =>
        item.Control is not (SettingControl.Capacity or SettingControl.Toggle or SettingControl.Export);

    /// <summary>
    /// The always-visible helper line: what the setting does, then the bound that qualifies it, then
    /// the permission that is missing. Never behind a disclosure — at a full page of settings a "?"
    /// per row is a page of buttons nobody presses.
    /// </summary>
    private string HelpLine(SettingItem item)
    {
        var parts = new List<string>(3) { item.Description };

        // The marker in the outline says WHICH WAY; the range belongs here, where there is room for
        // it. Resolved through MinFor/MaxFor so a server-published bound is the one quoted.
        if (item.MaxFrom is not null)
        {
            parts.Add($"Can be lowered but not raised: {MinFor(item):N0}–{MaxFor(item):N0}.");
        }
        else if (item.MinFrom is not null)
        {
            parts.Add($"Can be raised but not lowered: {MinFor(item):N0}–{MaxFor(item):N0}.");
        }

        if (!Editable(item.Claim))
        {
            parts.Add("Changing this needs an additional permission.");
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// The direction marker in the field's outline. A server-published ceiling means the value may
    /// only be lowered; a published floor means it may only be raised. Both are refusals, not
    /// discouragements, which is what earns the marker rather than a sentence.
    /// </summary>
    private static OdsSettingBound BoundFor(SettingItem item) =>
        item.MaxFrom is not null ? OdsSettingBound.LowerOnly
        : item.MinFrom is not null ? OdsSettingBound.RaiseOnly
        : OdsSettingBound.None;

    /// <summary>
    /// What is handed to the CONTROL as its error, as opposed to what the field renders.
    ///
    /// <para>
    /// Inside an <c>OdsSettingField</c> frame the sheet hides the control's own help/error node — the
    /// field owns the message, so it renders once, below the frame. The control still needs to know it
    /// is invalid, for <c>aria-invalid</c> and the error tint on its unit adornment, so it is handed a
    /// non-empty placeholder rather than the text (which would render twice or not at all).
    /// </para>
    /// </summary>
    private string? FieldStateFor(SettingItem item) => ErrorFor(item) is null ? null : " ";

    // ── Percent rows ──────────────────────────────────────────────────────────────────────────────
    //
    // Stored as a 0.0-1.0 fraction, entered as a whole percent. decimal division is exact for these
    // values (62m / 100m == 0.62m), so the round trip is lossless and a percent row is dirty on the
    // same terms as any other decimal row.
    private static int PercentOf(decimal fraction) => (int)Math.Round(fraction * 100m);

    private decimal? GetPercent(string key) =>
        GetDecimal(key) is { } fraction ? Math.Round(fraction * 100m) : null;

    private void SetPercent(string key, decimal? percent) =>
        SetDecimal(key, percent is null ? null : percent.Value / 100m);

    // ── Non-blocking advisories (issue #434 §3) ───────────────────────────────────────────────────
    //
    // Server-authored, keyed by the SystemSettingsUpdate property name — the SAME join key
    // ApiProblem.Errors uses, so the field→row lookup below is the one _serverErrors already needs and
    // not a second mapping table.
    //
    // An advisory is not an error. It does not set aria-invalid, does not appear in ErrorFor, does not
    // count toward BlockingSummary and does not disable Save. It renders in OdsSettingRow's own
    // Advisory slot — never Footer, which is already occupied on fileAnalysisProcessor, the one row the
    // correspondence heuristic targets.

    private static string AdvisoryId(string key) => $"ss-advisory-{key}";

    /// <summary>
    /// Appends an advisory count to a load/save announcement.
    ///
    /// <para>
    /// Routed through the page's existing <c>OdsLiveAnnouncer</c> rather than giving the advisory
    /// element <c>role="status"</c>: a live region inserted into the DOM at the same time as its content
    /// is frequently not announced at all, and <c>role="alert"</c> stays reserved for the field-error
    /// path — an advisory must not interrupt. The message names the count and points at the rows, and
    /// each advisory's full text is reachable from its own field's <c>aria-describedby</c>.
    /// </para>
    /// </summary>
    private string AdvisorySuffixed(string message)
    {
        var faults = _dto?.ProjectionFaults ?? EmptyFaults;
        var count = CostAdvisoryCount(_dto?.Warnings ?? EmptyWarnings, faults);

        var suffixed = count == 0
            ? message
            : $"{message} {count} {(count == 1 ? "advisory" : "advisories")} on this page — "
              + "each is shown under its own setting and does not block saving.";

        // A separate clause, never folded into the count above: "does not block saving" describes a
        // value the administrator chose, and is the wrong thing to say about a fault they did not cause.
        return FaultAnnouncement(faults, AllItems) is { } faultClause
            ? $"{suffixed} {faultClause}"
            : suffixed;
    }

    private static readonly IReadOnlyDictionary<string, SettingFaultKind> EmptyFaults =
        new Dictionary<string, SettingFaultKind>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, string> EmptyWarnings =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// The advisories that describe a value the administrator <em>chose</em> — the ones the "does not
    /// block saving" wording is true of.
    ///
    /// <para>
    /// Precedence puts each projection advisory INTO <c>Warnings</c> (the channel holds one string per
    /// field), so counting <c>Warnings.Count</c> unconditionally would count every fault twice and then
    /// tell the administrator it does not block saving. On a page with three cost advisories and two
    /// faults they would hear "5 advisories …", then a fault sentence naming two of those five, while
    /// the page's own summary rendered "2".
    /// </para>
    /// </summary>
    internal static int CostAdvisoryCount(
        IReadOnlyDictionary<string, string> warnings,
        IReadOnlyDictionary<string, SettingFaultKind> faults) =>
        warnings.Count - warnings.Keys.Count(faults.ContainsKey);

    /// <summary>
    /// The load/save announcement's fault clause, or null when nothing is faulted.
    ///
    /// <para>
    /// <strong>Filter-blind, deliberately</strong> — it is computed over the DTO's faults, never over
    /// the rendered sections. The page's own precedent pulls the other way (<c>BlockingSummary</c> and
    /// <c>JumpToFirstError</c> both scope to what is visible), and that is right for a validation error
    /// the administrator just created and wrong for a fault they did not cause.
    /// </para>
    ///
    /// <para>
    /// <strong>Capped at three named settings.</strong> <c>OdsLiveAnnouncer</c> is
    /// <c>aria-live="polite"</c> with <c>aria-atomic="true"</c>, so the whole message is re-spoken as
    /// one utterance on every change — and this page deliberately defeats the usual "unchanged text is
    /// not re-spoken" mitigation with a zero-width-space parity suffix, so the cap is load-bearing
    /// rather than belt-and-braces. It re-fires on load AND after every successful save, so an
    /// administrator repairing rows one at a time would otherwise hear the entire remaining list
    /// re-read after each save with no way to skip it.
    /// </para>
    ///
    /// <para>
    /// It carries titles and kind-tags, never the server's predicates: with the range interpolated,
    /// three clamped rows run about eighty words. The terms of the violation stay on the row, which is
    /// where they are acted on. The closing sentence points at the CONTROL rather than at the rows,
    /// because the control is the thing that works under a filter — it clears the search term itself.
    /// </para>
    /// </summary>
    internal static string? FaultAnnouncement(
        IReadOnlyDictionary<string, SettingFaultKind> faults,
        IEnumerable<SettingItem> items)
    {
        var named = FaultedItems(faults, items);
        if (named.Count == 0)
        {
            return null;
        }

        // "is outside its allowed range" rather than "outside its allowed range": both tags must
        // concatenate onto the title as a SENTENCE, because parentheses are silent at default screen
        // reader punctuation levels. A verb-less prepositional phrase degrades to a fragment.
        static string Tag(SettingFaultKind kind) =>
            kind == SettingFaultKind.Unreadable ? "couldn't be read" : "is outside its allowed range";

        var parts = named.Take(3).Select(entry => $"{entry.Item.Title} ({Tag(entry.Kind)})").ToList();
        var count = named.Count;

        if (count == 1)
        {
            return $"1 setting isn't using its stored value: {parts[0]}. Use Go to first fault to reach it.";
        }

        var listed = count > 3
            ? $"{string.Join(", ", parts)}, and {count - 3} more"
            : string.Join(", ", parts);

        return $"{count} settings aren't using their stored value: {listed}. Use Go to first fault to reach them.";
    }

    /// <summary>
    /// The faulted rows, unreadable before clamped and in catalogue order within each. The order is
    /// stated rather than incidental: the announcement names at most three, so <em>which</em> three is
    /// a user-facing choice, and it matches the log levels — unreadable is an error, clamped a warning.
    /// </summary>
    private static List<(SettingItem Item, SettingFaultKind Kind)> FaultedItems(
        IReadOnlyDictionary<string, SettingFaultKind> faults, IEnumerable<SettingItem> items)
    {
        if (faults.Count == 0)
        {
            return [];
        }

        var matched = items
            .Where(item => item.Field is not null)
            .Select(item => (Item: item, Found: faults.TryGetValue(item.Field!, out var kind), Kind: kind))
            .Where(entry => entry.Found)
            .Select(entry => (entry.Item, entry.Kind))
            .ToList();

        return
        [
            .. matched.Where(entry => entry.Kind == SettingFaultKind.Unreadable),
            .. matched.Where(entry => entry.Kind != SettingFaultKind.Unreadable),
        ];
    }

    /// <summary>
    /// The persistent, non-live record of the faulted rows, or null when there are none.
    ///
    /// <para>
    /// <strong>Filter-AWARE, and it counts the hidden rows rather than testing whether any is
    /// visible.</strong> A boolean "is any faulted row rendered" reports the total with no hidden
    /// clause whenever a filter matches even one of them — with six faults and a filter matching one it
    /// says "6 settings" while five are unreachable, which is the exact harm this exists to close. A
    /// sighted user might reconcile "6" against the screen; a screen-reader user would have to traverse
    /// every row counting advisory nodes.
    /// </para>
    ///
    /// <para>
    /// <strong>Two sentences, no dash, every form written out.</strong> NVDA and JAWS speak neither an
    /// em dash nor a hyphen at default punctuation, so a joined clause runs the two numbers together —
    /// and this is the browse-mode record that exists precisely so a screen-reader administrator can
    /// find the faults without the announcement. The singular forms are pinned separately because a
    /// one-fault page is the likeliest real case.
    /// </para>
    ///
    /// <para>
    /// The verb covers both kinds. "Could not be read as stored" is false for a clamped row, whose
    /// value was read perfectly well; "aren't using their stored value" is true of both, and matches the
    /// announcement's opening.
    /// </para>
    /// </summary>
    internal static string? FaultSummaryText(
        IReadOnlyDictionary<string, SettingFaultKind> faults,
        IReadOnlyList<SettingSection> sections,
        string search)
    {
        var faulted = sections
            .SelectMany(section => section.Items.Select(item => (Item: item, section.Group)))
            .Where(entry => entry.Item.Field is not null && faults.ContainsKey(entry.Item.Field!))
            .ToList();

        var count = faulted.Count;
        if (count == 0)
        {
            return null;
        }

        var hidden = faulted.Count(entry => !Matches(entry.Item, entry.Group, search));

        if (count == 1)
        {
            return hidden == 0
                ? "1 setting isn't using its stored value."
                : "1 setting isn't using its stored value. It is hidden by the current search.";
        }

        return hidden switch
        {
            0 => $"{count} settings aren't using their stored value.",
            1 => $"{count} settings aren't using their stored value. 1 of them is hidden by the current search.",
            _ => $"{count} settings aren't using their stored value. {hidden} of them are hidden by the current search.",
        };
    }

    /// <summary>The page-level fault record, rendered whenever the page is Ready and a fault exists.</summary>
    private string? FaultSummary =>
        FaultSummaryText(_dto?.ProjectionFaults ?? EmptyFaults, Sections, _search);

    /// <summary>
    /// Moves focus to the first faulted row, clearing the search term on the way.
    ///
    /// <para>
    /// It <strong>clears the term itself</strong> rather than pointing at the search field's clear
    /// button: <c>PageHeader</c> renders its search content only while the region is open, and
    /// collapsing the region does not clear the term — so an administrator can arrive with a filter
    /// applied and no input, or clear button, on screen at all. Both the term and the region's open
    /// state are persisted page state, so that is the normal landing state rather than an edge case.
    /// </para>
    ///
    /// <para>
    /// <c>StateHasChanged()</c> before the interop is required, not defensive: unlike
    /// <see cref="JumpToFirstError"/> this control mutates state first, so its target row is not in the
    /// DOM when the handler starts, and <c>odsFocusById</c> is a bare <c>.focus()</c> that no-ops
    /// silently on a missing id.
    /// </para>
    /// </summary>
    private async Task GoToFirstFault()
    {
        var faults = _dto?.ProjectionFaults ?? EmptyFaults;
        var first = FaultedItems(faults, AllItems).FirstOrDefault().Item;
        if (first is null)
        {
            return;
        }

        _searchOpen = true;
        OnSearchChanged(string.Empty);
        StateHasChanged();

        try
        {
            await JS.InvokeVoidAsync("odsFocusById", TitleId(first.Key));
        }
        catch
        {
            // The element isn't rendered (a concurrent re-render) — non-fatal; the summary still says
            // how many rows are affected.
        }
    }

    private string? AdvisoryText(SettingItem item) =>
        item.Field is { } field && _dto?.Warnings is { Count: > 0 } warnings
        && warnings.TryGetValue(field, out var message)
            ? message
            : null;

    private RenderFragment? AdvisoryFor(SettingItem item) =>
        AdvisoryText(item) is { } message ? builder => builder.AddContent(0, message) : null;

    /// <summary>
    /// The control's <c>aria-describedby</c>: the field's helper line, its error message when one is
    /// showing, and its advisory element when one is present — so a screen-reader user reaching the
    /// control hears all three as part of the field rather than only on navigating past them.
    ///
    /// <para>
    /// The error id is load-bearing here in a way it was not on the old row layout: inside an
    /// <c>OdsSettingField</c> frame the control's own error node is hidden by the sheet, so this
    /// reference is the only route from the control to the message.
    /// </para>
    /// </summary>
    private string DescribedBy(SettingItem item)
    {
        var parts = new List<string>(3) { DescId(item.Key) };

        if (ErrorFor(item) is not null)
        {
            parts.Add(ErrorId(item.Key));
        }

        if (AdvisoryText(item) is not null)
        {
            parts.Add(AdvisoryId(item.Key));
        }

        return string.Join(' ', parts);
    }

    // ── The credential fields (issue #444 §3, regrouped by the design update in 2f61476b) ────────
    //
    // A SEPARATE collection from SettingItem, not new entries in `Sections`. A secret has no Field, no
    // Load and no Write — it commits through its own per-key endpoint rather than the page's
    // whole-resource PUT — so folding it into the catalogue would force
    // SettingsCatalogueTests.Every_stored_row_declares_a_field_a_load_and_a_write to grow a second
    // exemption beyond SettingControl.Export. Keeping it separate means that test never sees these
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
            + "Only printable ASCII can be stored — a relay password outside that range is rejected "
            + "before it is sent, with the constraint named.",
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
            "No stored key backs consent pseudonymisation. In Production account deletion fails "
            + "rather than writing a pseudonym nobody can re-derive; elsewhere a fixed development "
            + "value is substituted so the delete flow still works.",
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
    /// The header's severity rollup for credentials this server cannot decrypt (issue #445).
    ///
    /// <para>
    /// Built from the INTERSECTION, like the rows themselves: a key the server reports but the
    /// catalogue does not describe has no title to name and no row to jump to. Gated on the write
    /// claim for the same reason the group is — there is nothing a read-only caller could do about it.
    /// </para>
    ///
    /// <para>
    /// Deliberately separate from <see cref="BlockingProblems"/>. That list explains a disabled
    /// <b>Save changes</b>; this one reports an outage a Save cannot fix, and folding the two together
    /// would misattribute both.
    /// </para>
    /// </summary>
    private IReadOnlyCollection<PageHeaderProblem>? CredentialProblems
    {
        get
        {
            if (_phase != Phase.Ready || !_hasSecurityUpdate)
            {
                return null;
            }

            var problems = SecretCatalogue
                .Where(item => _secretStatuses.TryGetValue(item.Key, out var status)
                    && status.State == SecretSettingState.Unreadable)
                .Select(item => new PageHeaderProblem
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
                })
                .ToList();

            return problems.Count > 0 ? problems : null;
        }
    }

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

    private async Task Save()
    {
        if (_isSaving || !CanSave || _phase != Phase.Ready)
            return;

        // A blocked attempt is the right moment to announce what is wrong — once, politely. Announcing
        // from ErrorFor instead would fire on every keystroke.
        if (HasErrors)
        {
            if (BlockingSummary is { } blocking)
            {
                Announce(blocking);
            }

            await JumpToFirstError();
            return;
        }

        _isSaving = true;
        _justSaved = false;

        // Rule one of two: a new attempt supersedes whatever the last one was rejected for. Without
        // this the overlay would keep Save disabled forever, since HasErrors gates the button AND
        // this method's own early return.
        _serverErrors.Clear();

        // Per §3: null — never a copy of the loaded value — for every field the caller cannot
        // edit. Sending the loaded value there would let this save silently clobber another
        // admin's concurrent change to a field this page never showed as editable. The claim
        // check on the API side keys off the CapacityLimit OBJECT, never `.Value` (sec F3), so a
        // count field this caller can't edit must be sent as a literal null, not a partially-filled
        // CapacityLimit.
        // One Write per catalogue entry (issue #421 Wave 0b). Each helper gates on the row's own
        // Claim, so "which claim covers which field" is stated once, in the catalogue, instead of
        // being re-derived here — the duplication that made a mismatch possible.
        var request = new SystemSettingsUpdate();
        foreach (var item in AllItems)
        {
            item.Write?.Invoke(this, request);
        }

        var result = await SystemSettings.UpdateAsync(request);
        _isSaving = false;

        if (result.IsSuccess && result.Value is { } dto)
        {
            ApplyLoaded(dto);
            _justSaved = true;
            Announce(AdvisorySuffixed("System settings saved."));
            // A successful save invalidates the client-side import-limits cache (issue #343 fe R9)
            // — without this, an admin who lowers a limit and then opens an import dialog in the
            // same session would pre-validate against the old value indefinitely.
            ImportLimits.Invalidate();
            // Same reasoning for the upload cap (issue #421 Wave 4) — its cache is session-lifetime too,
            // and lowering the cap is precisely the flow where a stale client number would matter.
            UploadLimits.Invalidate();
            // Same reasoning again for the smart-tag cap (issue #434 key 15): the Accounts page's
            // section reads it through a session-lifetime cache, so lowering the cap and then expanding
            // an account without this would pre-validate against the old number for the rest of the
            // session.
            AccountLimits.Invalidate();
            // The processor disclosure (issue #421 Wave 1, extended by #439). All three of the switch,
            // the model and the destination feed the consent gate — the switch decides whether the
            // Analyze affordance is offered at all — so an administrator toggling analysis off must see
            // the affordance change without a reload. Note this reaches only THIS browser: a user with
            // an open gate is covered server-side by the disclosureVersion check, not by this call.
            Disclosures.Invalidate();
            StateHasChanged();
            await Task.Delay(OdsTiming.ConfirmFlashMs);
            _justSaved = false;
            StateHasChanged();
        }
        else
        {
            Snackbar.Add($"Couldn't save settings: {result.Error}", Severity.Error);

            // Per-field messages render on their own rows. The server keys `errors` by the DTO field
            // name; the catalogue keys rows by its own key, so the Write delegate's target property is
            // the join between them — resolved here off the same catalogue, not a second mapping table.
            ApplyServerFieldErrors(result.Problem);

            // A server-rejected round-trip conflict (a race the client's own check missed — e.g. a
            // concurrent edit between load and save) maps to the responsible group's alert via
            // ApiProblem.Code (issue #343 fe D2/R10); an unrecognised code falls back to the
            // snackbar above, which already fired.
            var group = RoundTripGroupFor(result.Problem?.Code);
            if (group is not null)
            {
                StateHasChanged();
                try
                {
                    await JS.InvokeVoidAsync("odsFocusById", RoundTripAlertId(group));
                }
                catch
                {
                    // The element isn't present (e.g. the client's own check no longer agrees with
                    // the server, so no alert rendered) — non-fatal; the snackbar already told the
                    // caller what happened.
                }
            }
        }
    }

    // "system-settings.invalid.round-trip.<pair>" → the group name that pair belongs to.
    private static string? RoundTripGroupFor(string? code)
    {
        const string prefix = "system-settings.invalid.round-trip.";
        if (code is null || !code.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        var pair = code[prefix.Length..];
        return pair switch
        {
            "contacts" => "Contacts import & export",
            "calendars" => "Calendars import & export",
            "tasks" => "Tasks import & export",
            "journal-entries" => "Journal import & export",
            _ => null,
        };
    }

    private static string RoundTripAlertId(string group) => $"ss-rtalert-{group}";

    /// <summary>
    /// Maps a rejected save's per-field messages onto their rows (issue #421 Wave 0b).
    ///
    /// <para>
    /// Two server paths fill <c>ApiProblem.Errors</c>: <c>[ApiController]</c> model validation, which
    /// reports EVERY data-annotation failure at once, and a field-attributed
    /// <c>DomainValidationException</c>. That "all at once" property is why the overlay is keyed by
    /// field rather than driven off <c>Code</c> — a single code can only ever name one field.
    /// </para>
    /// </summary>
    private void ApplyServerFieldErrors(ApiProblem? problem)
    {
        if (problem is null || problem.Errors.Count == 0)
            return;

        foreach (var item in AllItems)
        {
            if (item.Field is { } field && problem.ErrorFor(field) is { } message)
            {
                _serverErrors[item.Key] = message;
            }
        }
    }

    /// <summary>
    /// The rows a Save attempt cannot proceed with, in catalogue order — the source for both the
    /// summary beside the button and the jump target.
    /// </summary>
    private IReadOnlyList<SettingItem> ErroredRows =>
        VisibleSections.SelectMany(section => section.Items)
            .Where(item => ErrorFor(item) is not null)
            .ToList();

    /// <summary>
    /// Sections whose round-trip rule is violated — a group-level failure with no single offending row.
    /// </summary>
    private IReadOnlyList<SettingSection> ErroredSections =>
        VisibleSections.Where(section => RoundTripError(section) is not null).ToList();

    /// <summary>
    /// A one-line summary of what is blocking Save, naming the sections involved. At 22 rows an admin
    /// could scroll to find the offending control; the catalogue is heading for 42 across eleven
    /// sections, where a disabled button with an error two screens away is an unexplained dead end.
    /// </summary>
    private string? BlockingSummary
    {
        get
        {
            if (!HasErrors)
                return null;

            var groups = VisibleSections
                .Where(section => RoundTripError(section) is not null
                                  || section.Items.Any(item => ErrorFor(item) is not null))
                .Select(section => section.Group)
                .ToList();

            var count = ErroredRows.Count + ErroredSections.Count;
            var noun = count == 1 ? "problem" : "problems";
            return $"{count} {noun} to fix in {string.Join(", ", groups)}";
        }
    }

    /// <summary>Unsaved-change count, so the page says how much is pending rather than only dotting rows.</summary>
    private int DirtyCount => VisibleSections.SelectMany(section => section.Items).Count(IsDirty);

    /// <summary>
    /// The Save button's count pill — the pending changes this press will commit. Suppressed during
    /// the post-save confirmation, where the button is saying "Saved" and there is nothing pending.
    /// </summary>
    private string? PendingBadge =>
        DirtyCount > 0 && !_justSaved ? DirtyCount.ToString("N0") : null;

    /// <summary>
    /// Every blocking problem, in catalogue order, for the <c>OdsErrorSummary</c> beside a disabled
    /// Save — the rows first, then the group-level round-trip conflicts.
    ///
    /// <para>
    /// Built from <c>VisibleSections</c>, deliberately: an entry whose row a search filter has removed
    /// from the DOM is a jump target that does not exist, and <c>window.odsFocusById</c> is a bare
    /// <c>.focus()</c> that no-ops silently on a missing id — a dead end presented as a fix.
    /// </para>
    ///
    /// <para>
    /// Each label is the row's own title plus the failure, because the panel is read away from the
    /// row: "Match timeout" alone does not say what is wrong with it.
    /// </para>
    /// </summary>
    private IReadOnlyList<OdsErrorSummaryProblem> BlockingProblems =>
    [
        .. VisibleSections.SelectMany(section => section.Items
            .Where(item => ErrorFor(item) is not null)
            .Select(item => new OdsErrorSummaryProblem(
                $"{item.Title} — {ErrorFor(item)}", section.Group, TitleId(item.Key)))),
        .. ErroredSections.Select(section => new OdsErrorSummaryProblem(
            RoundTripError(section)!, section.Group, RoundTripAlertId(section.Group))),
    ];

    /// <summary>
    /// Moves focus to the picked problem's control and flashes it — the same jump-to-record gesture
    /// the Accounts / Contracts / Insurance lists use, so a sighted keyboard user can see where focus
    /// landed on a page where the target may be a full screen away from the button they pressed.
    /// </summary>
    private Task JumpToProblem(OdsErrorSummaryProblem problem) =>
        problem.TargetId is null ? Task.CompletedTask : JumpToElementAsync(problem.TargetId);

    /// <summary>
    /// The gesture itself: flash the target, move focus to it, then clear the flash. Shared by the
    /// blocking-problems summary and the credential signal, which aim at different kinds of row —
    /// a <c>SettingItem</c>'s control and an <c>OdsSecretSettingField</c>'s label — but perform the
    /// identical move once they have an element id.
    /// </summary>
    private async Task JumpToElementAsync(string targetId)
    {
        _flashTargetId = targetId;
        StateHasChanged();

        try
        {
            await JS.InvokeVoidAsync("odsFocusById", targetId);
        }
        catch
        {
            // The element isn't rendered (a concurrent re-render or filter change) — non-fatal; the
            // summary still names the section or credential the problem is in.
        }

        await Task.Delay(OdsTiming.RowFlashMs);

        // Guarded, so a second jump started while this one was waiting keeps ITS flash rather than
        // having it cleared out from under it by the first jump's timer.
        if (_flashTargetId == targetId)
        {
            _flashTargetId = null;
            StateHasChanged();
        }
    }

    // The row currently wearing the one-shot attention ring, by element id. Null the rest of the time.
    private string? _flashTargetId;

    /// <summary>
    /// The row field's own classes: <c>locked</c> greys the frame and its value while leaving the
    /// helper line fully legible — the reason it is locked is the part still worth reading — and
    /// <c>ss-flash</c> is the one-shot ring after a jump.
    /// </summary>
    private string? FieldClass(SettingItem item)
    {
        var parts = new List<string>(2);
        if (!Editable(item.Claim))
        {
            parts.Add("locked");
        }

        if (_flashTargetId == TitleId(item.Key))
        {
            parts.Add("ss-flash");
        }

        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    /// <summary>
    /// Moves focus to the first offending row, or to the first violated section's alert when the failure
    /// is group-level. Deliberately the first RENDERED one: <c>ErrorFor</c> returns null for rows the
    /// caller cannot edit, and a search filter removes rows from <c>VisibleSections</c> entirely, so
    /// "first in the catalogue" could be a row that is not on screen.
    /// </summary>
    private async Task JumpToFirstError()
    {
        var targetId = ErroredRows.Count > 0
            ? TitleId(ErroredRows[0].Key)
            : ErroredSections.Count > 0 ? RoundTripAlertId(ErroredSections[0].Group) : null;

        if (targetId is null)
            return;

        try
        {
            await JS.InvokeVoidAsync("odsFocusById", targetId);
        }
        catch
        {
            // The element isn't rendered (a concurrent re-render or filter change) — non-fatal, and
            // the summary text still names the section.
        }
    }

    // ── Save-payload helpers ──────────────────────────────────────────────────
    //
    // Each gates on the ROW's own Claim rather than on a hardcoded flag, so the claim covering a field
    // is stated once — in the catalogue — instead of here as well. Per §3 they return null, never a
    // copy of the loaded value, for a field the caller cannot edit: sending the loaded value would let
    // this save clobber another admin's concurrent change to a field this page never showed as
    // editable. Keyed off claim possession, never off a control's momentary Disabled state.
    private bool CanEdit(string key) =>
        AllItems.FirstOrDefault(item => item.Key == key) is { } row && Editable(row.Claim);

    internal bool? BoolRequest(string key) => CanEdit(key) ? GetBool(key) : null;

    internal int? IntRequest(string key) => CanEdit(key) ? GetInt(key) : null;

    internal decimal? DecimalRequest(string key) => CanEdit(key) ? GetDecimal(key) : null;

    internal string? TextRequest(string key) => CanEdit(key) ? GetText(key) : null;

    internal CapacityLimit? CapacityRequest(string key)
    {
        if (!CanEdit(key))
            return null;

        var cap = Capacity(key);
        return cap.Unlimited ? new CapacityLimit { Unlimited = true } : new CapacityLimit { Value = cap.Value };
    }

    // ── Data export ───────────────────────────────────────────────────────────
    private async Task ExportDatabaseJson()
    {
        // Guard against duplicate clicks while a request is in flight (the button also shows a
        // loading state via _isExporting).
        if (_isExporting)
            return;

        _isExporting = true;
        try
        {
            var result = await DataExport.DownloadAsync();
            switch (result.Outcome)
            {
                case DataExportOutcome.Success when result.File is not null:
                    await JS.InvokeVoidAsync("downloadFileFromBytes",
                        result.File.Bytes, result.File.FileName, "application/json");
                    ShowExportReadyToast("Database export ready", result.File.FileName);
                    break;
                case DataExportOutcome.Forbidden:
                    Snackbar.Add("You don't have permission to export the database.", Severity.Error);
                    break;
                case DataExportOutcome.Incomplete:
                    // The download was truncated part-way through, so nothing was saved — a partial
                    // export is worse than none, since it looks like a whole one (issue #401).
                    Snackbar.Add("The export was cut short and is incomplete, so nothing was saved. Please try again.",
                                 Severity.Error);
                    break;
                default:
                    Snackbar.Add("Export failed. Please try again.", Severity.Error);
                    break;
            }
        }
        finally
        {
            _isExporting = false;
        }
    }

    // Success toast for a finished export — title line plus the generated filename in mono,
    // mirroring the design-system export toast (Odyssey Design System · SystemSettings).
    private void ShowExportReadyToast(string title, string fileName)
    {
        RenderFragment message = builder =>
        {
            builder.OpenElement(0, "div");
            builder.AddContent(1, title);
            builder.OpenElement(2, "div");
            builder.AddAttribute(3, "style",
                "font-family: var(--font-mono); font-size: 11.5px; opacity: .75; margin-top: 2px; word-break: break-all;");
            builder.AddContent(4, fileName);
            builder.CloseElement();
            builder.CloseElement();
        };
        Snackbar.Add(message, Severity.Success);
    }
}
