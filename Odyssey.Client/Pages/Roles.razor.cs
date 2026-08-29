using MudBlazor;
using Odyssey.Dtos.Application;

namespace Odyssey.Client.Pages;

public partial class Roles
{
    private bool _isLoading = true;
    private bool _loadError;
    private string? _loadErrorDetail;

    private List<ExistingRole> _roles = [];
    private string? _openRole;

    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
            return;

        await LoadRolesAsync();
        _isLoading = false;
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender)
            StateHasChanged();
    }

    private async Task LoadRolesAsync()
    {
        var rolesResult = await Users.ListRolesAsync();
        _roles = rolesResult.ValueOr([]);
        _loadErrorDetail = rolesResult.Error;
        _loadError = _loadErrorDetail is not null;
    }

    private async Task ReloadRolesAsync()
    {
        await LoadRolesAsync();
        StateHasChanged();
    }

    // Roles read from most- to least-privileged (by claim count), matching the
    // design specimen; ties fall back to alphabetical for a stable order.
    private IEnumerable<ExistingRole> OrderedRoles =>
        _roles.OrderByDescending(r => r.Permissions.Count)
              .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase);

    private string HeaderSubLine
    {
        get
        {
            var roleCount = _roles.Count;
            var claimCount = DistinctPermissions.Count;
            var roleWord = roleCount == 1 ? "role" : "roles";
            var claimWord = claimCount == 1 ? "permission claim" : "permission claims";
            return $"{roleCount} {roleWord} · {claimCount} {claimWord}";
        }
    }

    private void ToggleRole(string name) => _openRole = _openRole == name ? null : name;

    // ── Permission-claims catalog (derived from the union of all role claims) ──

    // Every distinct claim defined across the seeded roles, in first-seen order.
    private List<string> DistinctPermissions =>
        _roles.SelectMany(r => r.Permissions).Distinct(StringComparer.Ordinal).ToList();

    // Group claims into "category → actions", where a claim "a.b.c" splits into
    // category "a.b" and action "c". Category order follows first appearance.
    private IReadOnlyList<ClaimCategory> Categories
    {
        get
        {
            var byCategory = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var claim in DistinctPermissions)
            {
                var lastDot = claim.LastIndexOf('.');
                var category = lastDot > 0 ? claim[..lastDot] : claim;
                var action = lastDot > 0 ? claim[(lastDot + 1)..] : claim;
                if (!byCategory.TryGetValue(category, out var actions))
                {
                    actions = [];
                    byCategory[category] = actions;
                    order.Add(category);
                }
                actions.Add(action);
            }
            return order.Select(c => new ClaimCategory(c, byCategory[c])).ToList();
        }
    }

    private sealed record ClaimCategory(string Name, IReadOnlyList<string> Actions);

    // ── Role + category presentation (semantic palette tokens; valid in both themes) ──
    private static string RoleClass(string role) => role.ToLowerInvariant() switch
    {
        "owner" => "owner",
        "admin" => "admin",
        "manager" => "manager",
        "member" or "user" => "member",
        _ => "guest",
    };

    private static string RoleIcon(string role) => role.ToLowerInvariant() switch
    {
        "owner" => Icons.Material.Filled.VerifiedUser,
        "admin" => Icons.Material.Filled.Shield,
        "manager" => Icons.Material.Filled.Badge,
        "member" or "user" => Icons.Material.Filled.HowToReg,
        "viewer" or "guest" => Icons.Material.Filled.Visibility,
        _ => Icons.Material.Filled.Person,
    };

    private static string RoleDescription(string role) => role.ToLowerInvariant() switch
    {
        "owner" => "Full control of the workspace, including every administrative action.",
        "admin" => "Manages users and all finance data — can grant access and disable accounts.",
        "manager" => "Creates and edits finance data across the workspace, but cannot manage users.",
        "member" or "user" => "Day-to-day access: records transactions and files against existing accounts.",
        "viewer" or "guest" => "Read-only access to finance data. Cannot open the admin area.",
        _ => string.Empty,
    };

    private static string CategoryIcon(string category) => category switch
    {
        "accounts" => Icons.Material.Filled.AccountBalanceWallet,
        "budgets" => Icons.Material.Filled.PieChart,
        "transactions" => Icons.Material.Filled.ReceiptLong,
        "transactions.tags" => Icons.Material.Filled.LocalOffer,
        "contacts" => Icons.Material.Filled.Store,
        "currencies" => Icons.Material.Filled.AttachMoney,
        "exchangerates" => Icons.Material.Filled.CurrencyExchange,
        "user-preferences" => Icons.Material.Filled.Tune,
        "files" => Icons.Material.Filled.Folder,
        "file-analysis" => Icons.Material.Filled.DocumentScanner,
        "users" => Icons.Material.Filled.ManageAccounts,
        _ => Icons.Material.Filled.Key,
    };
}
