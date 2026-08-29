using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.Dtos.Application;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;

namespace Odyssey.Client.Pages;

public partial class Users
{
    private const string EnabledValue = "enabled";
    private const string DisabledValue = "disabled";

    private static readonly IReadOnlyList<OdsOption> _statusOptions =
        [new(EnabledValue, "Enabled"), new(DisabledValue, "Disabled")];
    private IReadOnlyList<OdsOption> RoleOptions => [.. _roles.Select(r => new OdsOption(r.Name, r.Name))];
    // Rows per page (OdsPager); OdsPageSizes.All shows every filtered row. Client-side over the
    // fetched set — Role/Status are client-side multi-selects, so the pager pages FilteredUsers.
    private int _pageSize = OdsPageSizes.Default[0];

    private bool _isLoading = true;
    private bool _loadError;

    private List<ExistingUser> _users = [];
    // Unfiltered set backing the header total/enabled count (issue #277 follow-up), so it reflects
    // all users rather than the current server-searched page.
    private List<ExistingUser> _allUsers = [];
    private List<ExistingRole> _roles = [];
    private Dictionary<string, IReadOnlyList<string>> _rolePermissions = new(StringComparer.OrdinalIgnoreCase);
    private int _totalCount;

    // Applied filters (server-side); the bound controls stage the next query.
    private const string PageStateKey = "users-page";
    private bool _searchOpen = true;
    private string _search = string.Empty;
    private IReadOnlyCollection<string> _roleFilter = [];
    private IReadOnlyCollection<string> _statusFilter = [];

    // Shared sort (§6.12): one OdsTableSort drives the toolbar OdsSortSelect AND the clickable
    // column headers (retained — no discoverability regression). All six historical keys preserved.
    private static readonly OdsTableSort DefaultSort = new("name", OdsSortDirection.Asc);
    private OdsTableSort _sort = DefaultSort;
    private static readonly IReadOnlyList<OdsSortField<ExistingUser>> _sortFields =
    [
        new() { Key = "name", Label = "Name", Type = OdsSortType.Text, SortValue = u => UserDisplay.DisplayName(u).ToLowerInvariant() },
        new() { Key = "fullname", Label = "Full name", Type = OdsSortType.Text, SortValue = u => UserDisplay.FullName(u)?.ToLowerInvariant() },
        new() { Key = "birthdate", Label = "Date of birth", Type = OdsSortType.Date, SortValue = u => u.BirthDate },
        new() { Key = "email", Label = "Email", Type = OdsSortType.Text, SortValue = u => u.Email?.ToLowerInvariant() },
        new() { Key = "role", Label = "Role", Type = OdsSortType.Status, SortValue = u => u.Role?.ToLowerInvariant() },
        new() { Key = "emailstatus", Label = "Email status", Type = OdsSortType.Status, SortValue = u => u.EmailConfirmed },
        new() { Key = "account", Label = "Account status", Type = OdsSortType.Status, SortValue = u => u.Enabled },
        // 'created' is not offered: ApplicationUser has no creation timestamp, so CreatedAtUtc is always
        // null (the column reads "—"). Re-add here + in the server allowlist once the column exists.
    ];
    // 1-based, matching OdsPager's contract and every other paged page.
    private int _page = 1;
    private string _announce = "";

    private string? _expandedId;
    private string? _editingId;

    // ── Edit panel save state ──
    // The editable fields themselves are the panel's working copy (UserEditPanel.Draft); the page
    // only owns the in-flight/failed state of the save it performs on the panel's behalf.
    private bool _editSaving;
    private string? _editError;

    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
            return;

        await RestorePageStateAsync();
        StateHasChanged();
        _actorUserId = (await AuthenticationStateProvider.GetUserAsync()).UserId();
        await LoadRolesAsync();
        await LoadUsersAsync();
        await RefreshAllUsersAsync();
        _isLoading = false;
    }

    // ── Page-state persistence (search section + filters) ─────────────────────
    // Role is a free string (roles load later) → restored as-is; Status is one of
    // a fixed set → coerced back to "all" if it's anything else.
    private Task RestorePageStateAsync() =>
        PageState.RestoreOrSeedAsync<UsersPageState>(PageStateKey, ApplyPageState, BuildPageState);

    private void ApplyPageState(UsersPageState state)
    {
        _searchOpen = state.SearchOpen;
        _search = state.Search ?? string.Empty;
        _roleFilter = state.Roles ?? [];   // roles load later → restored as-is
        _statusFilter = (state.Statuses ?? []).Where(s => s is EnabledValue or DisabledValue).ToList();
        _sort = OdsSortHelpers.Resolve(_sortFields, state.SortField, state.SortDirection, DefaultSort);
        _pageSize = OdsPageSizes.Restore(state.PageSize);
    }

    private UsersPageState BuildPageState() => new()
    {
        SearchOpen = _searchOpen,
        Search = _search,
        Roles = [.. _roleFilter],
        Statuses = [.. _statusFilter],
        SortField = _sort.Key,
        SortDirection = _sort.Dir,
        PageSize = _pageSize,
    };

    private void PersistPageState() => PageState.QueueSave(PageStateKey, BuildPageState());

    private void OnSearchToggled(bool open) { _searchOpen = open; PersistPageState(); }
    private void OnSearchChanged(string value) { _search = value ?? string.Empty; _page = 1; PersistPageState(); }
    private void OnRoleFilterChanged(IReadOnlyCollection<string> values) { _roleFilter = values ?? []; _page = 1; PersistPageState(); Announce(); }
    private void OnStatusFilterChanged(IReadOnlyCollection<string> values) { _statusFilter = values ?? []; _page = 1; PersistPageState(); Announce(); }

    // The search is server-side and Role/Status client-side, so an empty table can mean either.
    private bool _hasFilters => !string.IsNullOrWhiteSpace(_search) || _roleFilter.Count > 0 || _statusFilter.Count > 0;

    private async Task ClearFilters()
    {
        _search = string.Empty;
        _roleFilter = [];
        _statusFilter = [];
        _page = 1;
        PersistPageState();
        await LoadUsersAsync();
    }

    private sealed class UsersPageState
    {
        public bool SearchOpen { get; set; } = true;
        public string Search { get; set; } = string.Empty;
        public List<string> Roles { get; set; } = [];
        public List<string> Statuses { get; set; } = [];
        public string? SortField { get; set; }
        public OdsSortDirection? SortDirection { get; set; }
        public int PageSize { get; set; } = OdsPageSizes.Default[0];
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender)
            StateHasChanged();
    }

    // ── Data loading ──
    // The header total/enabled count is over all users, not the current search page.
    private async Task RefreshAllUsersAsync() =>
        _allUsers = (await UsersApi.ListAllAsync()).ItemsOrToast(Snackbar, "users");

    private async Task LoadRolesAsync()
    {
        // Roles are reference data for the filter + permissions detail; a failure here
        // shouldn't block the user list, so the error is discarded (no toast, no inline state).
        _roles = (await UsersApi.ListRolesAsync()).ValueOr([]);
        _rolePermissions = _roles.ToDictionary(r => r.Name, r => r.Permissions, StringComparer.OrdinalIgnoreCase);
    }

    private async Task LoadUsersAsync()
    {
        // Server-side (issue #277): search + sort are applied by the API (including the SQL role-join
        // sort). Role/status stay as client-side multi-selects over the fetched set (see FilteredUsers).
        var result = await UsersApi.ListAsync(
            page: 1, pageSize: PagedQuery.SizeAll,
            search: _search,
            sortBy: _sort.Key,
            sortDir: _sort.Dir == OdsSortDirection.Asc ? "asc" : "desc");

        var load = result.PagedOrToast(Snackbar, "users");
        if (!load.IsSuccess)
        {
            _loadError = true;
            _users = [];
            _totalCount = 0;
            return;
        }

        _loadError = false;
        _users = [.. load.Items];
        _totalCount = load.TotalCount;
        _page = 1;
        _expandedId = null;
        _editingId = null;
        Announce();
    }

    // ── Filtering (client-side over the loaded set) ──
    private IReadOnlyList<ExistingUser> FilteredUsers
    {
        get
        {
            IEnumerable<ExistingUser> query = _users;

            if (_roleFilter.Count > 0)
                query = query.Where(u => _roleFilter.Contains(u.Role));

            if (_statusFilter.Count > 0)
                query = query.Where(u => _statusFilter.Contains(u.Enabled ? EnabledValue : DisabledValue));

            // Search is applied server-side (issue #277); role/status remain client-side multi-selects.
            return query.ToList();
        }
    }

    // ── Header sub-line ──
    private string HeaderSubLine
    {
        get
        {
            var enabled = _allUsers.Count(u => u.Enabled);
            var total = _allUsers.Count;
            return $"{total} {(total == 1 ? "user" : "users")} · {enabled} enabled";
        }
    }

    // ── Sorting ── (server-side, issue #277)
    // Sort is computed by the API (including the SQL role-join sort); the client renders the
    // returned order and re-fetches when the active sort changes.

    // Header click and the toolbar OdsSortSelect share one OdsTableSort. On a column change the
    // direction comes from the shared default-direction helper (§8.4), not an unconditional asc.
    private async Task ToggleSort(string column)
    {
        _sort = _sort.Key == column
            ? _sort with { Dir = _sort.Dir == OdsSortDirection.Asc ? OdsSortDirection.Desc : OdsSortDirection.Asc }
            : new OdsTableSort(column, DefaultDirFor(column));
        _page = 1;
        PersistPageState();
        await LoadUsersAsync();
    }

    private async Task OnSortChanged(OdsTableSort sort) { _sort = sort; _page = 1; PersistPageState(); await LoadUsersAsync(); }

    private static OdsSortDirection DefaultDirFor(string key)
    {
        var field = _sortFields.FirstOrDefault(f => f.Key == key);
        return field is null ? OdsSortDirection.Asc : OdsSortHelpers.DefaultDir(field);
    }

    // ── Pagination (client-side over the loaded set) ──
    private int FilteredCount => FilteredUsers.Count;
    private int PageCount => _pageSize == OdsPageSizes.All ? 1 : Math.Max(1, (int)Math.Ceiling(FilteredCount / (double)_pageSize));
    private int PageStartIndex => _pageSize == OdsPageSizes.All ? 0 : (_page - 1) * _pageSize;
    private IEnumerable<ExistingUser> PageItems => _pageSize == OdsPageSizes.All
        ? FilteredUsers
        : FilteredUsers.Skip(PageStartIndex).Take(_pageSize);

    private void GoToPage(int page)
    {
        _page = Math.Clamp(page, 1, PageCount);
        _expandedId = null;
        _editingId = null;
        Announce();
    }

    private void OnPageSizeChanged(int size)
    {
        _pageSize = size;
        _page = 1;
        _expandedId = null;
        _editingId = null;
        PersistPageState();
        Announce();
    }

    // Paging and filtering happen client-side, so nothing on the wire signals the change —
    // WCAG 2.2 §4.1.3 needs the new result window stated out loud (see OdsLiveAnnouncer).
    private void Announce()
    {
        var count = FilteredCount;
        _announce = count == 0
            ? "No users match your filters."
            : _pageSize == OdsPageSizes.All
                ? $"Showing all {count} user{(count == 1 ? "" : "s")}."
                : $"Page {_page} of {PageCount}, {count} user{(count == 1 ? "" : "s")}.";
    }

    // ── Expand / edit ──
    // A row is the disclosure control: clicking an open row (detail OR edit)
    // collapses it; clicking a closed row opens its read-only detail.
    private void ToggleExpand(string id)
    {
        if (_expandedId == id || _editingId == id)
        {
            _expandedId = null;
            _editingId = null;
        }
        else
        {
            _editingId = null;
            _expandedId = id;
        }
    }

    private void OnRowKeyDown(KeyboardEventArgs args, string id)
    {
        if (args.Key is "Enter" or " " or "Spacebar")
            ToggleExpand(id);
    }

    private void BeginEdit(ExistingUser user)
    {
        _editingId = user.Id;
        _expandedId = null;
        _editSaving = false;
        _editError = null;
    }

    private void CancelEdit()
    {
        _editingId = null;
        _editError = null;
    }

    private async Task ApplyEdit(ExistingUser user, UserEditPanel.Draft draft)
    {
        _editSaving = true;
        _editError = null;

        try
        {
            // Flags (email confirmed / enabled) go through PATCH; role through PUT.
            if (draft.EmailConfirmed != user.EmailConfirmed || draft.Enabled != user.Enabled)
            {
                var body = new UpdatedUser
                {
                    EmailConfirmed = draft.EmailConfirmed != user.EmailConfirmed ? draft.EmailConfirmed : null,
                    Enabled = draft.Enabled != user.Enabled ? draft.Enabled : null,
                };
                var patched = await UsersApi.UpdateAsync(user.Id, body);
                if (!patched.IsSuccess)
                {
                    _editError = patched.Error;
                    return;
                }
            }

            if (!string.Equals(draft.Role, user.Role, StringComparison.Ordinal))
            {
                var roled = await UsersApi.UpdateRoleAsync(user.Id, new UpdatedUserRole { Role = draft.Role });
                if (!roled.IsSuccess)
                {
                    _editError = roled.Error;
                    return;
                }
            }

            // Re-read the canonical row so the table reflects exactly what the API stored.
            var updated = (await UsersApi.GetAsync(user.Id)).Value;
            if (updated is not null)
            {
                var index = _users.FindIndex(u => u.Id == user.Id);
                if (index >= 0)
                    _users[index] = updated;
            }

            // Enabled/role edits change the header's total/enabled tally.
            await RefreshAllUsersAsync();
            Snackbar.Add($"Updated {UserDisplay.DisplayName(user)}.", Severity.Success);
            _editingId = null;
        }
        catch (Exception ex)
        {
            _editError = ex.Message;
        }
        finally
        {
            _editSaving = false;
        }
    }

    // ── Permanent delete (users.delete) ──
    private ExistingUser? _pendingDelete;
    private bool _deleting;

    private int EnabledAdminCount =>
        _users.Count(u => u.Enabled && string.Equals(u.Role, "Admin", StringComparison.OrdinalIgnoreCase));

    // Mirror the Edit panel's self-protection: the last enabled Admin can't be deleted.
    // The API enforces this too (409), but the dialog blocks it inline first.
    private bool TargetIsLastEnabledAdmin =>
        _pendingDelete is { Enabled: true } target
        && string.Equals(target.Role, "Admin", StringComparison.OrdinalIgnoreCase)
        && EnabledAdminCount <= 1;

    private Task CopyUserId(ExistingUser user) => Clipboard.CopyAsync(user.Id, "User ID copied.");

    private void RequestDelete(ExistingUser user) => _pendingDelete = user;

    private void OnDeleteOpenChanged(bool open)
    {
        if (!open && !_deleting)
            _pendingDelete = null;
    }

    private async Task DeleteConfirmedAsync()
    {
        if (_pendingDelete is not { } user || TargetIsLastEnabledAdmin)
            return;

        _deleting = true;
        var deleted = await DeleteUserAsync(user);
        _deleting = false;
        if (deleted)
            _pendingDelete = null;
    }

    private async Task<bool> DeleteUserAsync(ExistingUser user)
    {
        // The API guards self-deletion and the last enabled admin (409); the toast surfaces that
        // message, so no client-side duplication of those rules is needed.
        if ((await UsersApi.DeleteAsync(user.Id)).Toast(Snackbar, "Couldn't delete user", $"Deleted {UserDisplay.DisplayName(user)}."))
        {
            _users.RemoveAll(u => u.Id == user.Id);
            _allUsers.RemoveAll(u => u.Id == user.Id);
            if (_expandedId == user.Id)
                _expandedId = null;
            if (_editingId == user.Id)
                _editingId = null;
            _totalCount = Math.Max(0, _totalCount - 1);
            if (_page > 1 && PageStartIndex >= FilteredCount)
                _page--;
            return true;
        }
        return false;
    }

    // ── Send password reset (users.update, issue #406) ──
    private ExistingUser? _pendingReset;
    private bool _sendingReset;

    // Resolved once at load: the confirmation copy says something extra when the target is the acting
    // admin, because that call signs them out everywhere else and gates their current session too.
    private string _actorUserId = string.Empty;

    private bool TargetIsSelf =>
        _pendingReset is { } target
        && !string.IsNullOrEmpty(_actorUserId)
        && string.Equals(target.Id, _actorUserId, StringComparison.Ordinal);

    private void RequestPasswordReset(ExistingUser user) => _pendingReset = user;

    private void OnResetOpenChanged(bool open)
    {
        if (!open && !_sendingReset)
            _pendingReset = null;
    }

    /// <summary>
    /// Trigger the reset and report what actually happened. Four outcomes, never conflated — the whole
    /// point of the endpoint returning a body rather than a bare 204 is that "the reset was applied but
    /// the mail didn't go out" is something the admin has to act on.
    /// </summary>
    private async Task SendPasswordResetAsync()
    {
        if (_pendingReset is not { } target || _sendingReset)
            return;

        _sendingReset = true;
        var result = await UsersApi.SendPasswordResetAsync(target.Id);
        _sendingReset = false;

        var name = UserDisplay.DisplayName(target);
        var email = target.Email ?? target.UserName ?? target.Id;

        if (result.IsSuccess)
        {
            if (result.Value?.EmailDelivered == true)
            {
                Snackbar.Add($"Password reset link sent to {email}.", Severity.Success);
            }
            else
            {
                // Not a failure: the reset stands (sessions revoked, flag set) and retrying the whole
                // call would be wrong — the user has to be routed to Forgot password instead.
                Snackbar.Add(
                    $"Reset applied, but the email couldn't be delivered. Ask {name} to use Forgot password on the sign-in page.",
                    Severity.Warning);
            }

            await RefreshUserRowAsync(target.Id);
            _pendingReset = null;
            return;
        }

        // 422 (no confirmed address) and 429 (throttled — the ProblemDetails distinguishes "too many to
        // this address" from "too many from this account") both mean nothing was mutated. The server's
        // own wording carries that distinction, so it is surfaced rather than replaced.
        var severity = result.Status == System.Net.HttpStatusCode.TooManyRequests
            ? Severity.Warning
            : Severity.Error;
        Snackbar.Add(result.Error ?? $"Couldn't send a password reset for {name}.", severity);
        _pendingReset = null;
    }

    /// <summary>Re-read one row so the pending-reset chip reflects exactly what the API stored.</summary>
    private async Task RefreshUserRowAsync(string id)
    {
        if ((await UsersApi.GetAsync(id)).Value is not { } updated)
            return;

        var index = _users.FindIndex(u => u.Id == id);
        if (index >= 0)
            _users[index] = updated;

        var allIndex = _allUsers.FindIndex(u => u.Id == id);
        if (allIndex >= 0)
            _allUsers[allIndex] = updated;
    }

    private string? _claimsOpenId;
    private void ToggleClaims(string id) => _claimsOpenId = _claimsOpenId == id ? null : id;

    private IReadOnlyList<string> RolePermissionsFor(string role) =>
        _rolePermissions.TryGetValue(role, out var permissions) ? permissions : [];
}
