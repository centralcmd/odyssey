using Microsoft.AspNetCore.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class AccountContractsSection
{
    [Parameter, EditorRequired] public Guid AccountId { get; set; }

    private List<ContractLinkTile> _contracts = [];
    private bool _isLoading;

    protected override async Task OnInitializedAsync()
    {
        _isLoading = true;
        _contracts = [.. (await Accounts.ListContractsAsync(AccountId)).ItemsOrToast(Snackbar, "contracts")
            .Select(ContractLinkTile.From)];
        _isLoading = false;
    }

    /// <summary>The tile foot for one account link — see <see cref="ContractLinkTiles.Foot"/>.</summary>
    internal static string Foot(AccountContractLink link) => ContractLinkTiles.Foot(link.Roles, link.Status);
}
