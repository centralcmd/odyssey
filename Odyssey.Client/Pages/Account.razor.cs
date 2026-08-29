using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor;
using Odyssey.Dtos.Application;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Models;
using Odyssey.Client.Services;
using Odyssey.Dtos.Authorization;

namespace Odyssey.Client.Pages;

public partial class Account
{
    // ── Identity (loaded from manage/info + cookie claims) ──
    private bool _isLoading = true;
    private string _email = string.Empty;
    private bool _emailConfirmed;
    private string _username = string.Empty;
    private string _userId = string.Empty;
    private string? _role;
    private List<string> _permissions = [];

    // The canonical profile (issue #316). The Profile section edits a working copy and hands the
    // saved server values back, so the header, avatar and Permissions tile re-render with no reload.
    private ProfileDto _profile = new();

    // Two-factor status. Owned here rather than in the section because the header chips and the
    // Security overview card show it too — and they must stay right while the search filters the
    // section away.
    private AccountTwoFactorSection.TwoFactorStatus _twoFactor = new(false, 0, string.Empty);

    // ── Search ──
    private const string PageStateKey = "account-page";
    private bool _problemsOpen = true;   // defaults match the header: Problems + Overview
    private bool _overviewOpen = true;   // open, Search collapsed.
    private bool _searchOpen;
    private string _search = string.Empty;

    // ── Search index: which terms reveal each section ──
    private static readonly Dictionary<string, string[]> SectionTerms = new()
    {
        ["profile"] = ["profile", "name", "display name", "first name", "last name", "middle name", "title", "date of birth", "birthday", "dob", "sex", "identity", "who i am"],
        ["email"] = ["email", "email address", "change email", "username", "confirmation", "sign-in", "sign in"],
        ["password"] = ["password", "change password", "reset password", "credentials", "passphrase"],
        ["twofa"] = ["two-factor", "two factor", "2fa", "mfa", "authenticator", "recovery codes", "otp", "verification", "security"],
        ["permissions"] = ["permissions", "claims", "access", "role", "authorization"],
    };

    private string _displayName = "Account";
    private string _initials = "?";

    // ── Resolved profile identity (issue #316) — drives the header, the Overview Identity card and the
    // Permissions tile, so a save re-renders the name everywhere with no reload. Falls back to the
    // username/email heuristic only until the profile loads (the onboarding gate guarantees it exists). ──
    private string ResolvedName =>
        ProfileValidation.ResolveName(_profile) is { Length: > 0 } resolved ? resolved : $"@{_username}";

    private string ResolvedInitials =>
        ProfileValidation.Initials(_profile) is { } initials && initials != "?" ? initials : _initials;

    private string ShortId => _userId.Length > 8 ? _userId[..8] : _userId;

    private string RoleIcon => _role switch
    {
        "Admin" => Icons.Material.Filled.Shield,
        "Owner" => Icons.Material.Filled.VerifiedUser,
        "User" => Icons.Material.Filled.HowToReg,
        _ => Icons.Material.Filled.Person,
    };

    // ── Page-state persistence (header sections + search box) ─────────────────
    private Task RestorePageStateAsync() =>
        PageState.RestoreOrSeedAsync<AccountPageState>(PageStateKey, ApplyPageState, BuildPageState);

    private void ApplyPageState(AccountPageState state)
    {
        _problemsOpen = state.ProblemsOpen;
        _overviewOpen = state.OverviewOpen;
        _searchOpen   = state.SearchOpen;
        _search       = state.Search ?? string.Empty;
    }

    private AccountPageState BuildPageState() => new()
    {
        ProblemsOpen = _problemsOpen,
        OverviewOpen = _overviewOpen,
        SearchOpen   = _searchOpen,
        Search       = _search,
    };

    private void PersistPageState() => PageState.QueueSave(PageStateKey, BuildPageState());

    private void OnProblemsToggled(bool open) { _problemsOpen = open; PersistPageState(); }
    private void OnOverviewToggled(bool open) { _overviewOpen = open; PersistPageState(); }
    private void OnSearchToggled(bool open) { _searchOpen = open; PersistPageState(); }
    private void OnSearchChanged(string value) { _search = value ?? string.Empty; PersistPageState(); }

    private sealed class AccountPageState
    {
        public bool ProblemsOpen { get; set; } = true;
        public bool OverviewOpen { get; set; } = true;
        public bool SearchOpen { get; set; }
        public string Search { get; set; } = string.Empty;
    }

    // ── Header problems (the single info nudge to enable 2FA) ──
    private IReadOnlyCollection<PageHeaderProblem>? HeaderProblems =>
        _isLoading || _twoFactor.Enabled
            ? null
            : new[]
            {
                new PageHeaderProblem
                {
                    Severity = PageHeaderSeverity.Information,
                    Lead = "Add two-factor authentication",
                    Message = "Your account is currently protected by its password alone. A second step at sign-in keeps it safe even if your password is exposed.",
                    ViewLabel = "Set up 2FA",
                    OnView = EventCallback.Factory.Create(this, () => ScrollToSection("twofa")),
                },
            };

    // ── Search filtering ──
    private bool Matches(string key)
    {
        var q = _search.Trim();
        if (string.IsNullOrEmpty(q)) return true;
        q = q.ToLowerInvariant();
        return SectionTerms.TryGetValue(key, out var terms) &&
               terms.Any(t => t.Contains(q, StringComparison.Ordinal) || q.Contains(t, StringComparison.Ordinal));
    }

    private int VisibleSectionCount => SectionTerms.Keys.Count(Matches);

    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
            return;

        await RestorePageStateAsync();
        StateHasChanged();

        var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;
        _userId = user.UserId();
        _username = user.FindFirst(ClaimTypes.Name)?.Value ?? user.Identity?.Name ?? string.Empty;
        _role = user.FindFirst(ClaimTypes.Role)?.Value ?? user.FindFirst("role")?.Value;
        _permissions = [.. user.FindAll(PermissionClaims.Type)
            .Select(c => c.Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(v => v, StringComparer.Ordinal)];

        var info = await AuthApiClient.GetInfoAsync();
        if (info is not null)
        {
            _email = info.Email;
            _emailConfirmed = info.IsEmailConfirmed;
        }

        if (string.IsNullOrWhiteSpace(_username))
            _username = _email;

        _displayName = BuildDisplayName(_username, _email);
        _initials = BuildInitials(_displayName);

        if (await ProfileApi.GetAsync() is { IsSuccess: true, Value: { } profile })
            _profile = profile;

        await LoadTwoFactorAsync();

        _isLoading = false;
    }

    private async Task LoadTwoFactorAsync()
    {
        // Posting an empty body reads status and hands back a pending shared key (Identity
        // generates one if absent) so the setup wizard is ready without a second round-trip.
        if (await AuthApiClient.GetTwoFactorStatusAsync() is not { } status)
            return;

        _twoFactor = new AccountTwoFactorSection.TwoFactorStatus(
            status.IsTwoFactorEnabled, status.RecoveryCodesLeft, status.SharedKey ?? string.Empty);
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender)
            StateHasChanged();
    }

    private void OnProfileSaved(ProfileDto profile) => _profile = profile;

    private void OnTwoFactorChanged(AccountTwoFactorSection.TwoFactorStatus status) => _twoFactor = status;

    private static string BuildDisplayName(string username, string email)
    {
        var basis = !string.IsNullOrWhiteSpace(username) ? username : email;
        var local = basis.Split('@')[0];
        var parts = local.Split('.', '_', '-', ' ').Where(p => p.Length > 0).ToArray();
        if (parts.Length == 0) return basis;
        return string.Join(" ", parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    private static string BuildInitials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant();
        return $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant();
    }

    private async Task ScrollToSection(string key) =>
        await ScrollManager.ScrollIntoViewAsync($"#acc-sec-{key}", ScrollBehavior.Smooth);

    private async Task LogoutAsync()
    {
        await AuthApiClient.LogoutAsync();
        await AuthStateProvider.RefreshAsync();
        NavigationManager.NavigateTo("/login", true);
    }
}
