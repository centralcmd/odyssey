using Microsoft.AspNetCore.Components;
using Odyssey.Client.Services;

namespace Odyssey.Client.Pages.Finance;

public partial class PropertyContractsSection
{
    [Parameter, EditorRequired] public Guid PropertyId { get; set; }

    private List<ContractLinkTile> _contracts = [];
    private bool _isLoading;

    protected override async Task OnInitializedAsync()
    {
        _isLoading = true;
        _contracts = [.. (await Properties.ListContractsAsync(PropertyId)).ItemsOrToast(Snackbar, "contracts")
            .Select(ContractLinkTile.From)];
        _isLoading = false;
    }
}
