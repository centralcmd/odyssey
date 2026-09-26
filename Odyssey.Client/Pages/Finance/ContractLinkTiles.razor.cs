using Microsoft.AspNetCore.Components;
using Odyssey.Client.Components;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// One contract tile's data — the fields <c>AccountContractLink</c> and <c>PropertyContractLink</c>
/// share, which a server-side guard test pins as identical.
/// </summary>
public sealed record ContractLinkTile(
    Guid ContractId, string Name, ContractType Type, ContractStatus Status, IReadOnlyList<ContractPartyRole> Roles)
{
    public static ContractLinkTile From(AccountContractLink link) =>
        new(link.ContractId, link.Name, link.Type, link.Status, link.Roles);

    public static ContractLinkTile From(PropertyContractLink link) =>
        new(link.ContractId, link.Name, link.Type, link.Status, link.Roles);
}

public partial class ContractLinkTiles
{
    [Parameter, EditorRequired] public IReadOnlyList<ContractLinkTile> Links { get; set; } = [];

    /// <summary>
    /// Mutes an archived contract's value — the link is history. The property record's section
    /// draws it this way; the account record's does not, so it is the host's choice.
    /// </summary>
    [Parameter] public bool DimArchived { get; set; }

    private OdsInfoTileTone ToneFor(ContractLinkTile link) =>
        DimArchived && link.Status == ContractStatus.Archived ? OdsInfoTileTone.Muted : OdsInfoTileTone.Default;

    /// <summary>
    /// The foot: every role the record holds on the contract, then the contract's status —
    /// "Borrower · Guarantor · Active". Role words go through <see cref="PartyRoleLabel"/>, so an
    /// ordinal this build cannot name reads as the same stated absence the party tile shows.
    /// </summary>
    internal static string Foot(IEnumerable<ContractPartyRole> roles, ContractStatus status) =>
        string.Join(" · ", [.. roles.Select(PartyRoleLabel.For), OdsContractStatus.Meta(status).Label]);

    /// <summary>
    /// View, then the two copies — the design system's menu for this tile. A host renders this only
    /// for a <c>contracts.read</c> holder, which is also what the Contracts page needs, so View is
    /// never a door into "not authorized".
    /// </summary>
    private IReadOnlyList<OdsMenuItem> MenuFor(ContractLinkTile link) =>
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
