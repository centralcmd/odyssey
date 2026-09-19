using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Odyssey.Client.Components;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class InsurancePolicyLinkTiles : IAsyncDisposable
{
    [Inject] private IJSRuntime JS { get; set; } = default!;


    /// <summary>Members a collection names before it collapses into "+N more".</summary>
    public const int TileLimit = 5;

    /// <summary>The collection's members, already in the server's display order.</summary>
    [Parameter, EditorRequired] public IReadOnlyList<LinkTileMember> Members { get; set; } = [];

    /// <summary>The collection's SINGULAR name — "Insurer", "Insured", "Beneficiary".</summary>
    [Parameter, EditorRequired] public string? Label { get; set; }

    /// <summary>Glyph for a member whose own type resolves to none.</summary>
    [Parameter] public string FallbackIcon { get; set; } = "link";

    /// <summary>Which of the policy's four collections this is — passed back with an edit.</summary>
    [Parameter] public InsurancePartyRole Role { get; set; }

    /// <summary>
    /// Raised with (role, targetId) when a member's edit action is used. Unset means the tiles are
    /// read-only, which is also what a caller without insurance.update passes.
    /// </summary>
    [Parameter] public EventCallback<(InsurancePartyRole Role, Guid TargetId)> OnEditParty { get; set; }

    /// <summary>
    /// Raised with (role, targetId) when a member is detached. Offered for an UNNAMED member too:
    /// removing a link needs only the link, and an unresolvable one is the likeliest to want removing.
    /// Unset means the tiles are read-only.
    /// </summary>
    [Parameter] public EventCallback<(InsurancePartyRole Role, Guid TargetId)> OnRemoveParty { get; set; }

    /// <summary>
    /// Where focus lands when a removal empties THIS collection, so focus is not dropped on the
    /// document. The host supplies it because the answer is outside these tiles — another collection's
    /// menu in the same policy, or the policy's own row actions.
    /// </summary>
    [Parameter] public string[]? FallbackFocusSelectors { get; set; }

    /// <summary>
    /// True for the insured-ACCOUNT collection. Only the "Open …" item reads it — the three contact
    /// collections point at contacts, this one at an account.
    /// </summary>
    [Parameter] public bool IsAccount { get; set; }

    private bool _expanded;

    // Focus return across a removal, through the shared helper the contract party tiles also call
    // (issue #122 §4). Extracted rather than duplicated: this component cannot be dropped into the
    // contract surface — it takes a required role and is instantiated once per collection — so the
    // alternative was a second copy of a mechanism whose drift is invisible until a keyboard user
    // hits it.
    private TileRemovalFocus? _focus;

    private TileRemovalFocus Focus => _focus ??= new TileRemovalFocus(JS);

    internal string PartyMenuId(LinkTileMember member) => PartyMenuIdFor(member.Key);

    private string PartyMenuIdFor(string key) => $"ins-party-{Role}-{key}";

    protected override void OnParametersSet() =>
        Focus.OnKeysChanged([.. Members.Select(member => member.Key)]);

    protected override Task OnAfterRenderAsync(bool firstRender) =>
        Focus.ApplyAsync(key => $"#{PartyMenuIdFor(key)} button", FallbackFocusSelectors);

    public async ValueTask DisposeAsync()
    {
        if (_focus is not null)
        {
            await _focus.DisposeAsync();
        }
    }

    /// <summary>
    /// Raises the removal, having first noted which tile focus should land on — the next member, or
    /// the previous one when the removed tile was last.
    /// </summary>
    private Task RemovePartyAsync(InsurancePartyRole role, Guid targetId)
    {
        Focus.Arm(targetId.ToString(), [.. Members.Select(member => member.Key)]);
        return OnRemoveParty.InvokeAsync((role, targetId));
    }

    private static string UnnamedIcon(LinkTileMember member) =>
        member.State == LinkAvailability.Archived ? "inventory_2" : "link_off";

    /// <summary>
    /// The member's actions. Copy ID is unconditional, which is what keeps the menu non-empty for an
    /// unnamed member — its record is not in the picker (so Edit could not round-trip it) and its name
    /// is not readable, but detaching it must stay possible. Remove detaches the LINK; the contact or
    /// account itself is untouched, which is why it is not worded as a delete.
    /// </summary>
    private IReadOnlyList<OdsMenuItem> MenuFor(LinkTileMember member)
    {
        if (!Guid.TryParse(member.Key, out var targetId)) return [];

        var role = Role;
        var noun = Label?.ToLowerInvariant() ?? "party";
        var items = new List<OdsMenuItem>();

        if (!member.Unnamed)
        {
            items.Add(new()
            {
                Icon = "content_copy",
                Label = "Copy name",
                OnClick = EventCallback.Factory.Create(this, () => Clipboard.CopyAsync(member.Display, "Name copied to clipboard.")),
            });
            items.Add(new()
            {
                Icon = IsAccount ? "account_balance_wallet" : "groups",
                Label = IsAccount ? "Open account" : "Open contact",
                OnClick = EventCallback.Factory.Create(this, () => Navigation.NavigateTo(IsAccount ? "/accounts" : "/contacts")),
            });

            if (OnEditParty.HasDelegate)
            {
                items.Add(new()
                {
                    Icon = "edit",
                    Label = $"Edit {noun}",
                    OnClick = EventCallback.Factory.Create(this, () => OnEditParty.InvokeAsync((role, targetId))),
                });
            }
        }

        items.Add(new()
        {
            Icon = "fingerprint",
            TrailingIcon = "content_copy",
            Label = "Copy ID",
            OnClick = EventCallback.Factory.Create(this, () => Clipboard.CopyAsync(targetId.ToString(), "ID copied to clipboard.")),
        });

        if (OnRemoveParty.HasDelegate)
        {
            items.Add(new() { Divider = true });
            items.Add(new()
            {
                Icon = "link_off",
                Label = $"Remove {noun}",
                Danger = true,
                OnClick = EventCallback.Factory.Create(this, () => RemovePartyAsync(role, targetId)),
            });
        }

        return items;
    }

    /// <summary>
    /// One member of a link collection, flattened for display. Built by the host, which holds both
    /// the reference DTO and the type registry the glyph and colour come from.
    /// </summary>
    public sealed record LinkTileMember
    {
        public required string Key { get; init; }

        /// <summary>The name, or the state word for an unnamed member — always in TEXT.</summary>
        public required string Display { get; init; }

        /// <summary>The type, in text. Null for an unresolvable link, which has no type to state.</summary>
        public string? TypeLabel { get; init; }

        /// <summary>
        /// The member's term in the role, already formatted — null when it is the default (the
        /// policy's own extent), which needs no caption.
        /// </summary>
        public string? Term { get; init; }

        public string? Icon { get; init; }

        public string? IconColor { get; init; }

        public string? IconSoft { get; init; }

        public LinkAvailability State { get; init; } = LinkAvailability.Available;

        public bool Unnamed => State != LinkAvailability.Available;
    }
}
