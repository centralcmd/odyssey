using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class ContractDetailView : IAsyncDisposable
{
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private IClipboardService Clipboard { get; set; } = default!;
    [Inject] private TimeProvider Time { get; set; } = default!;

    [Parameter, EditorRequired] public ExistingContract Contract { get; set; } = default!;

    [Parameter] public bool CanWrite { get; set; }
    [Parameter] public bool CanDownload { get; set; }

    /// <summary>
    /// True when the contract is archived. The server refuses an add or an edit on one (422) but still
    /// permits a detach, so <b>Edit party</b> is disabled with its reason while <b>Detach</b> stays
    /// live — the asymmetry is the server's, not a UI choice (issue #121 §5.3).
    /// </summary>
    [Parameter] public bool Archived { get; set; }

    /// <summary>Raised with the party to edit; unset means no edit affordance is offered.</summary>
    [Parameter] public EventCallback<ExistingContractParty> OnEditParty { get; set; }

    /// <summary>Raised after a party detach or file detach so the host re-fetches the contract.</summary>
    [Parameter] public EventCallback OnChanged { get; set; }

    /// <summary>Raised with the line the host's live region should read out.</summary>
    [Parameter] public EventCallback<string> OnAnnounce { get; set; }

    /// <summary>
    /// Formats a money-valued term in its own currency (issue #135). Supplied by the host because a
    /// contract has no currency of its own — every term names the one it is priced in, so the
    /// formatter is per-value rather than per-record.
    /// </summary>
    [Parameter, EditorRequired]
    public Func<decimal, string?, string> FormatMoney { get; set; } =
        (v, _) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private ContractTermsSection? _terms;

    /// <summary>
    /// Opens the Terms section's create dialog. The sections carry no action slot — every
    /// section-level action lives in the record's row action menu, which is where a reader looks for
    /// actions on this record — so the host drives it through here.
    /// </summary>
    public void OpenNewTerm() => _terms?.OpenNew();

    private IReadOnlyList<ContractFileItem> ContractFiles => [.. Contract.Files.Select(ContractFileItem.From)];

    // Focus return across a detach, through the helper the insurance party tiles also call. A
    // contract's parties are one flat list, so the keys are party ids.
    private TileRemovalFocus? _focus;

    private TileRemovalFocus Focus => _focus ??= new TileRemovalFocus(JS);

    private static string PartyMenuIdFor(string key) => $"con-party-{key}";

    private IReadOnlyList<string> PartyKeys => [.. Contract.Parties.Select(p => p.ContractPartyId.ToString())];

    protected override void OnParametersSet() => Focus.OnKeysChanged(PartyKeys);

    protected override Task OnAfterRenderAsync(bool firstRender) =>
        // When the detach empties the section, focus lands on the empty-state line rather than being
        // dropped on <body>.
        Focus.ApplyAsync(key => $"#{PartyMenuIdFor(key)} button", ["#con-parties-empty"]);

    public async ValueTask DisposeAsync()
    {
        if (_focus is not null)
        {
            await _focus.DisposeAsync();
        }
    }

    /// <summary>
    /// A party whose term closed before today: still a party of record, drawn quieter than one
    /// currently in its role. Read from the injected <see cref="TimeProvider"/>, so the rendering is
    /// deterministic under test rather than depending on the wall clock.
    /// </summary>
    private bool IsPast(ExistingContractParty party) =>
        party.ToDate is { } to && to.Date < Time.GetUtcNow().UtcDateTime.Date;

    private sealed record PartyVisual(string KindLabel, string Name, string? TypeLabel, string Icon, string? Color, string? Soft)
    {
        /// <summary>
        /// The caption: the party KIND and the record's own type, in that order. Both dropped out of
        /// the overline when the role took it, and both are still stated — meaning never rides on the
        /// glyph alone.
        /// </summary>
        public string Caption => TypeLabel is null ? KindLabel : $"{KindLabel} · {TypeLabel}";
    }

    private static PartyVisual Resolve(ExistingContractParty party)
    {
        switch (party.Kind)
        {
            case ContractPartyKind.Account when party.Account is { } a:
            {
                return new PartyVisual("Account", a.Name, AccountTypeVisuals.Label(a.Type),
                    AccountTypeVisuals.MaterialIcon(a.Type), AccountTypeVisuals.FgColor(a.Type), AccountTypeVisuals.BgColor(a.Type));
            }
            // The DTO member is still named Institution — it is a serialized enum value, so renaming it
            // would de-authorize nothing but would break every stored and in-flight payload. The design
            // renamed what the reader sees, which is all that changed here.
            case ContractPartyKind.Institution when party.Institution is { } c:
            {
                var m = OdsTypeRegistries.ContactTypeOf(c.Type.ToString());
                return new PartyVisual("Contact", c.Name, m.Label, m.Icon, m.Color, m.Soft);
            }
            default:
                // Neither reference projection resolved. The ROLE still reads on the overline — it is a
                // top-level field on the party and does not depend on the target — but there is no
                // record to name a type from.
                return new PartyVisual("Party", "—", null, "help", null, null);
        }
    }

    /// <summary>
    /// The party's actions. Copy ID is unconditional, which is what keeps the menu non-empty for a
    /// party whose target did not resolve. Detach removes the LINK; the account or contact itself is
    /// untouched, which is why it is not worded as a delete.
    /// </summary>
    /// <remarks>
    /// <b>Edit party is withheld for an UNRECOGNISED role</b> — an ordinal this build's registry does
    /// not contain. The <c>PUT</c> is a full replacement, so a dialog that cannot name the role would
    /// silently rewrite it to whatever it could show. Detach stays, on the same reasoning the
    /// insurance tiles already apply to an unresolvable target: detaching a link needs only the link.
    /// <c>Unspecified</c> is deliberately NOT withheld — it is a role the picker holds and can
    /// round-trip perfectly.
    /// </remarks>
    private IReadOnlyList<OdsMenuItem> MenuFor(ExistingContractParty party, PartyVisual visual)
    {
        if (!CanWrite) return [];

        var items = new List<OdsMenuItem>();
        var resolved = party.Account is not null || party.Institution is not null;

        if (OnEditParty.HasDelegate && resolved && !PartyRoleLabel.IsUnknown(party.Role))
        {
            items.Add(Archived
                ? new OdsMenuItem
                {
                    Icon = "edit",
                    Label = "Edit party",
                    Disabled = true,
                    Description = "Unarchive the contract to change its parties.",
                }
                : new OdsMenuItem
                {
                    Icon = "edit",
                    Label = "Edit party",
                    OnClick = EventCallback.Factory.Create(this, () => OnEditParty.InvokeAsync(party)),
                });
        }

        if (resolved)
        {
            items.Add(new OdsMenuItem
            {
                Icon = "content_copy",
                Label = "Copy name",
                OnClick = EventCallback.Factory.Create(this, () => Clipboard.CopyAsync(visual.Name, "Name copied to clipboard.")),
            });
        }

        items.Add(new OdsMenuItem
        {
            Icon = "fingerprint",
            TrailingIcon = "content_copy",
            Label = "Copy ID",
            OnClick = EventCallback.Factory.Create(this,
                () => Clipboard.CopyAsync(party.ContractPartyId.ToString(), "ID copied to clipboard.")),
        });

        items.Add(new OdsMenuItem { Divider = true });
        items.Add(new OdsMenuItem
        {
            Icon = "link_off",
            Label = "Detach party",
            Danger = true,
            OnClick = EventCallback.Factory.Create(this, () => DetachPartyAsync(party)),
        });

        return items;
    }

    private async Task DetachPartyAsync(ExistingContractParty party)
    {
        if (!CanWrite) return;
        var r = Resolve(party);
        var confirmed = await DialogService.ShowMessageBoxAsync(
            "Detach party",
            $"Detach '{r.Name}' from this contract? The {r.KindLabel.ToLowerInvariant()} itself is not affected.",
            yesText: "Detach", cancelText: "Cancel");

        if (confirmed != true) return;

        // Armed BEFORE the write, while the list still holds the party: the tile that had focus is
        // gone by the time the re-fetched contract arrives.
        Focus.Arm(party.ContractPartyId.ToString(), PartyKeys);

        if ((await Contracts.RemovePartyAsync(Contract.ContractId, party.ContractPartyId))
            .Toast(Snackbar, "Detach failed", "Party detached."))
        {
            var remaining = Contract.Parties.Count - 1;
            await OnAnnounce.InvokeAsync(
                $"{r.Name} detached. {remaining} part{(remaining == 1 ? "y" : "ies")} remain{(remaining == 1 ? "s" : "")}.");
            await OnChanged.InvokeAsync();
        }
    }
}
