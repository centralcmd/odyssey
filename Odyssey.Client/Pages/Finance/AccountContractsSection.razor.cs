using Microsoft.AspNetCore.Components;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class AccountContractsSection
{
    [Parameter, EditorRequired] public Guid AccountId { get; set; }

    private List<AccountContractLink> _contracts = [];
    private bool _isLoading;

    protected override async Task OnInitializedAsync()
    {
        _isLoading = true;
        _contracts = (await Accounts.ListContractsAsync(AccountId)).ItemsOrToast(Snackbar, "contracts");
        _isLoading = false;
    }

    /// <summary>
    /// The foot: every role the account holds on the contract, then the contract's status —
    /// "Borrower · Guarantor · Active". Role words go through <see cref="PartyRoleLabel"/>, so an
    /// ordinal this build cannot name reads as the same stated absence the party tile shows.
    /// </summary>
    internal static string Foot(AccountContractLink link) =>
        string.Join(" · ",
            [.. link.Roles.Select(PartyRoleLabel.For), OdsContractStatus.Meta(link.Status).Label]);

    /// <summary>
    /// View, then the two copies — the design system's menu for this tile. The host renders this
    /// section only for a <c>contracts.read</c> holder, which is also what the Contracts page needs,
    /// so View is never a door into "not authorized".
    /// </summary>
    private IReadOnlyList<OdsMenuItem> MenuFor(AccountContractLink link) =>
    [
        new()
        {
            Icon = "visibility",
            Label = "View",
            OnClick = EventCallback.Factory.Create(this, () => Navigation.NavigateTo("contracts")),
        },
        new()
        {
            Icon = "content_copy",
            Label = "Copy name",
            OnClick = EventCallback.Factory.Create(this, () => Clipboard.CopyAsync(link.Name, "Name copied to clipboard.")),
        },
        new()
        {
            Icon = "fingerprint",
            TrailingIcon = "content_copy",
            Label = "Copy ID",
            OnClick = EventCallback.Factory.Create(this,
                () => Clipboard.CopyAsync(link.ContractId.ToString(), "ID copied to clipboard.")),
        },
    ];
}
