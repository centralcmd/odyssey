using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Dtos.Application;

namespace Odyssey.Client.Pages;

public partial class AccountPermissionsSection
{
    /// <summary>The signed-in user's profile, for the identity tile.</summary>
    [Parameter, EditorRequired] public ProfileDto Profile { get; set; } = new();

    [Parameter] public string Username { get; set; } = string.Empty;
    [Parameter] public string Email { get; set; } = string.Empty;
    [Parameter] public string UserId { get; set; } = string.Empty;
    [Parameter] public string? Role { get; set; }

    /// <summary>The permission claims the cookie carries, already de-duplicated and sorted.</summary>
    [Parameter] public IReadOnlyList<string> Permissions { get; set; } = [];

    // ── Permission catalog (mirrors PermissionClaims.cs grouping) ──
    private static readonly (string Cat, string Icon, string Label)[] Catalog =
    [
        ("accounts", Icons.Material.Filled.AccountBalanceWallet, "Accounts"),
        ("budgets", Icons.Material.Filled.PieChart, "Budgets"),
        ("transactions.tags", Icons.Material.Filled.LocalOffer, "Transaction tags"),
        ("transactions", Icons.Material.Filled.ReceiptLong, "Transactions"),
        ("contacts", Icons.Material.Filled.Store, "Contacts"),
        ("currencies", Icons.Material.Filled.AttachMoney, "Currencies"),
        ("exchangerates", Icons.Material.Filled.CurrencyExchange, "Exchange rates"),
        ("user-preferences", Icons.Material.Filled.Tune, "Preferences"),
        ("files", Icons.Material.Filled.Folder, "Files"),
        ("file-analysis", Icons.Material.Filled.DocumentScanner, "File analysis"),
        ("users", Icons.Material.Filled.ManageAccounts, "Users"),
    ];

    // ── Permissions grouped by area (longest-prefix match) ──
    private List<(string Label, string Icon, List<string> Actions)> GrantedByCategory =>
        [.. Catalog
            .Select(c => (c.Label, c.Icon, Actions: Permissions
                .Where(p => CategoryOf(p) == c.Cat)
                .Select(p => p[(c.Cat.Length + 1)..])
                .OrderBy(a => a, StringComparer.Ordinal)
                .ToList()))
            .Where(g => g.Actions.Count > 0)];

    private static string? CategoryOf(string permission) =>
        Catalog
            .Where(c => permission.StartsWith(c.Cat + ".", StringComparison.Ordinal))
            .OrderByDescending(c => c.Cat.Length)
            .Select(c => c.Cat)
            .FirstOrDefault();
}
