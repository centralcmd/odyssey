using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Dtos.Application;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages;

public partial class Settings : IDisposable
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

    // ── Exchange rates card ─────────────────────────────────────────────────────
    // The sync's status, Run now and the per-currency controls live on /currencies; this card keeps the
    // two instance-wide settings and a link there. Status is read once, for the synced count.
    private Odyssey.Dtos.Finance.ExchangeRateSyncStatus? _syncStatus;

    private async Task LoadSyncCountAsync()
    {
        var result = await ExchangeRateSync.GetStatusAsync();
        if (result.IsSuccess && result.Value is { } status)
        {
            _syncStatus = status;
            StateHasChanged();
        }
    }

    // The new controls are built here rather than as markup in Settings.razor, whose @code block
    // is capped (SourceConventionTests); each is a plain composition of existing atoms.

    // The history start date: a picker over an invariant yyyy-MM-dd string row.
    private RenderFragment DateControl(SettingItem item) => builder =>
    {
        builder.OpenComponent<OdsDateField>(0);
        builder.AddComponentParameter(1, nameof(OdsDateField.Id), ControlId(item.Key));
        builder.AddComponentParameter(2, nameof(OdsDateField.Value), GetDate(item.Key));
        builder.AddComponentParameter(3, nameof(OdsDateField.ValueChanged),
            EventCallback.Factory.Create<DateTime?>(this, v => SetDate(item.Key, v)));
        builder.AddComponentParameter(4, nameof(OdsDateField.Min), (DateTime?)ExchangeRateHistoryStartDateRule.Floor.ToDateTime(TimeOnly.MinValue));
        builder.AddComponentParameter(5, nameof(OdsDateField.Max), (DateTime?)Time.GetUtcNow().UtcDateTime.Date);
        builder.AddComponentParameter(6, nameof(OdsDateField.Placeholder), item.Placeholder ?? "Select date");
        builder.AddComponentParameter(7, nameof(OdsDateField.Disabled), IsRowDisabled(item));
        builder.AddComponentParameter(8, nameof(OdsDateField.Error), FieldStateFor(item));
        builder.AddComponentParameter(9, nameof(OdsDateField.DescribedBy), DescribedBy(item));
        builder.CloseComponent();
    };

    // Manage currencies navigates, so it is a link.
    private RenderFragment ActionControl(SettingItem item) => builder =>
    {
        builder.OpenComponent<OdsButton>(0);
        builder.AddComponentParameter(1, nameof(OdsButton.Variant), OdsButtonVariant.Outlined);
        builder.AddComponentParameter(2, nameof(OdsButton.Icon), "tune");
        builder.AddComponentParameter(3, nameof(OdsButton.Href), "/currencies");
        builder.AddComponentParameter(4, nameof(OdsButton.ChildContent), (RenderFragment)(b => b.AddContent(0, "Manage currencies")));
        builder.CloseComponent();
    };

    private DateTime? GetDate(string key) => ParseStoredDate(GetText(key));

    private void SetDate(string key, DateTime? value) => SetText(key, FormatStoredDate(value));

    /// <summary>The stored yyyy-MM-dd text of a Date row as the picker's value; empty or malformed is null.</summary>
    internal static DateTime? ParseStoredDate(string stored) =>
        DateOnly.TryParseExact(stored.Trim(), ExchangeRateHistoryStartDateRule.Format,
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date)
            ? date.ToDateTime(TimeOnly.MinValue)
            : null;

    /// <summary>Invariant yyyy-MM-dd, empty ↔ null (SEC F6): the row stays a string on the wire.</summary>
    internal static string FormatStoredDate(DateTime? value) =>
        value is { } d
            ? DateOnly.FromDateTime(d).ToString(ExchangeRateHistoryStartDateRule.Format, System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;

    private enum Phase { Loading, Ready, Error }
    private Phase _phase = Phase.Loading;

    // Claim possession — computed once from the loaded AuthenticationState in OnInitializedAsync,
    // NOT from any control's momentary Disabled state (spec §3): a save-in-flight or still-loading
    // row also renders Disabled, but that must never null out a field the caller actually holds the
    // claim for. Axis split (issue #343 §10 item 4): Security → system-settings.security.update
    // (the three original Security toggles AND the four size caps); Count → system-settings.update
    // (the seven count caps).
    private bool _hasSecurityUpdate;
    private bool _hasCountUpdate;
    private bool CanSave => _hasSecurityUpdate || _hasCountUpdate;

    private SystemSettingsDto? _dto;
    private bool _isSaving;
    private bool _justSaved;

    // A save refused with 403 — the write claim was withdrawn after sign-in. Cleared by the next edit,
    // as the design system's page does: the reader has seen it and chosen to carry on.
    private bool _authDenied;

    private const string AuthAlertId = "ss-authalert";

    /// <summary>What every edit does to the page's transient save outcomes.</summary>
    private void Touch()
    {
        _justSaved = false;
        _authDenied = false;
    }

    /// <summary>
    /// The 403 alert's remedy. Permissions live in the auth cookie and are read at sign-in, so a fresh
    /// sign-in is the only way the page can learn what the caller may change now. The full reload is
    /// what drops the cached auth state and every app-lifetime cache with it, as on Account.
    /// </summary>
    private async Task SignOutAsync()
    {
        await AuthApiClient.LogoutAsync();
        Nav.NavigateTo("/login", forceLoad: true);
    }

    /// <summary>
    /// The row's "last changed" stamp (Odyssey Design System · SystemSettings): who last wrote it and
    /// when, or that it still holds the shipped default. Read from the DTO's per-key provenance, joined
    /// on the row's <see cref="SettingItem.Field"/> the same way Warnings are. A tile with nothing to
    /// change (an export or a link) has no stamp.
    /// </summary>
    private string? MetaFor(SettingItem item)
    {
        if (IsTileOnly(item.Control) || item.Field is not { } field || _dto is null)
        {
            return null;
        }

        return _dto.Provenance.TryGetValue(field, out var stamp)
            ? $"Last changed by {stamp.UpdatedByDisplayName}, {stamp.UpdatedAt.ToLocalTime():d MMM yyyy, HH:mm}."
            : "Never changed — default value.";
    }

    // ── Encrypted credentials (issue #444) ────────────────────────────────────────────────────
    // The server's statuses, keyed by registry key. The page renders the INTERSECTION of the static
    // client catalogue with these, so the server stays the single authority on which secrets exist.
    private readonly Dictionary<string, SecretSettingStatusDto> _secretStatuses = new(StringComparer.Ordinal);

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
        {
            return;
        }

        await PageState.RestoreOrSeedAsync<SettingsPageState>(PageStateKey, ApplyPageState, BuildPageState);

        var user = await Auth.GetUserAsync();
        _hasSecurityUpdate = user.HasPermission(PermissionClaims.SystemSettingsSecurityUpdate);
        _hasCountUpdate = user.HasPermission(PermissionClaims.SystemSettingsUpdate);
        _canReadStorage = user.HasPermission(PermissionClaims.FileStorageRead);
        _canReadPhotoStats = user.HasPermission(PermissionClaims.PhotosRead) && user.HasPermission(PermissionClaims.SystemSettingsRead);

        await Task.WhenAll(LoadSettingsAsync(), LoadSyncCountAsync(), LoadStorageHealthAsync());
    }

    private async Task LoadSettingsAsync()
    {
        _phase = Phase.Loading;
        StateHasChanged();

        var result = await SystemSettings.GetAsync();
        if (result.IsSuccess && result.Value is { } dto)
        {
            await LoadWeatherOptionsAsync();
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

    /// <summary>The points that can be the household weather default, served at read time (issue #348, #387).</summary>
    private IReadOnlyList<WeatherPointOption> _weatherOptions = [];

    private async Task LoadWeatherOptionsAsync()
    {
        // weather.read, not a settings claim: the options endpoint serves the same picker the dashboard
        // uses. Without it the row still shows its stored value, it just offers nothing else to pick.
        if (!(await Auth.GetUserAsync()).HasPermission(PermissionClaims.WeatherRead))
        {
            return;
        }

        var options = await WeatherOptions.GetAsync();
        _weatherOptions = options.IsSuccess && options.Value is { } value ? [.. value.Points.Where(p => p.ForecastEnabled)] : [];
    }

    /// <summary>
    /// <i>None</i>, then every selectable point. A stored default that is no longer selectable stays in the
    /// list, named as such, so opening the page never silently changes what is saved.
    /// </summary>
    internal IReadOnlyList<OdsOption> WeatherDefaultOptions()
    {
        var options = new List<OdsOption> { new("", "None") };
        options.AddRange(_weatherOptions.Select(p => new OdsOption(p.WeatherPointId.ToString("D"), p.Name)));
        var stored = _texts.TryGetValue("weatherDefaultPointId", out var state) ? state.Saved : "";
        if (stored.Length > 0 && options.All(o => !string.Equals(o.Value, stored, StringComparison.OrdinalIgnoreCase)))
        {
            options.Add(new OdsOption(stored, "A weather point that is no longer selectable"));
        }

        return options;
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

    /// <summary>Cancels any pending guidance announcement, so none fires after the page has gone.</summary>
    public void Dispose()
    {
        foreach (var state in _longTextAnnouncers.Values)
        {
            state.Pending?.Cancel();
            state.Pending?.Dispose();
        }

        _longTextAnnouncers.Clear();
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
        {
            return null;
        }

        var cap = Capacity(key);
        return cap.Unlimited ? new CapacityLimit { Unlimited = true } : new CapacityLimit { Value = cap.Value };
    }
}
