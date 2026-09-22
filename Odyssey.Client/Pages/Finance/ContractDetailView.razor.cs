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
    /// Gates the Smart tags section (issue #166), which resolves its watchlist through the
    /// transactions endpoint. Without <c>transactions.read</c> the section could only show chips and
    /// an error, so it is withheld rather than rendered broken.
    /// </summary>
    [Parameter] public bool CanReadTransactions { get; set; }

    /// <summary>
    /// The contract's watched-tag count, for the section divider's meta. It comes from the LIST row
    /// (<c>ContractListItem.SmartTagCount</c>) rather than from <c>ExistingContract</c>, which carries
    /// no counts — the same split every other section's meta uses.
    /// </summary>
    [Parameter] public int SmartTagCount { get; set; }

    /// <summary>
    /// Raised with the new count after an add or a remove, so the collapsed row's counts strip and
    /// this section's own meta stay live without re-fetching the contracts list.
    /// </summary>
    [Parameter] public EventCallback<int> OnSmartTagCountChanged { get; set; }

    /// <summary>Raised with the party to edit; unset means no edit affordance is offered.</summary>
    [Parameter] public EventCallback<ExistingContractParty> OnEditParty { get; set; }

    /// <summary>Raised after a party detach or file detach so the host re-fetches the contract.</summary>
    [Parameter] public EventCallback OnChanged { get; set; }

    /// <summary>
    /// Raised with this contract's documents, freshly read, after a document METADATA edit
    /// (issue #146). Such an edit creates and removes no row, so nothing outside the documents
    /// collection can have changed — the host patches it in place instead of pulling the contract,
    /// the contracts list and the summary back down.
    /// </summary>
    [Parameter] public EventCallback<List<ExistingContractFile>> OnFilesRefreshed { get; set; }

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

    /// <summary>
    /// An outstanding "New term" request from the record's row action menu, forwarded to the Terms
    /// section. The sections carry no action slot of their own — every section-level action lives in
    /// that menu, which is where a reader looks for actions on this record.
    /// </summary>
    [Parameter] public Guid? NewTermRequestToken { get; set; }

    /// <summary>
    /// An outstanding "New event" request from the record's row action menu — same token shape, and
    /// same reason, as <see cref="NewTermRequestToken"/>: the section carries no action slot, so the
    /// ask arrives as data handed only to the record that made it.
    /// </summary>
    [Parameter] public Guid? NewEventRequestToken { get; set; }

    /// <summary>
    /// The contract host's empty-state sentence. It deliberately does NOT say "on this contract":
    /// the match is by tag and spans every record watching that tag, so a scope claim here would be
    /// false. It says what the watchlist is FOR instead.
    /// </summary>
    private string ContractSmartTagsEmptyDesc => CanWrite
        ? "Pin the tags this agreement settles against, and what it actually costs reads here — no filter to rebuild on the Transactions page."
        : "No tags are being watched on this contract.";

    private IReadOnlyList<ContractFileItem> ContractFiles => [.. Contract.Files.Select(ContractFileItem.From)];

    // Focus return across a detach, through the helper the insurance party tiles also call. A
    // contract's parties are one flat list, so the keys are party ids.
    private TileRemovalFocus? _focus;

    private TileRemovalFocus Focus => _focus ??= new TileRemovalFocus(JS);

    private static string PartyMenuIdFor(string key) => $"con-party-{key}";

    /// <summary>
    /// The parties in RENDER order: the ones naming what the agreement is ABOUT first
    /// (<c>Object</c>, <c>Property</c>, <c>Collateral</c> — issue #169), then the parties that stand
    /// on a side of it. Each group keeps the order the server sent, so the grouping is the only thing
    /// this imposes.
    /// </summary>
    /// <remarks>
    /// The thing contracted over is what a reader scans a tenancy or a loan for, and it is the one
    /// tile that is not a counterparty — leading with it costs the counterparties nothing, since they
    /// keep their relative order.
    /// </remarks>
    private IReadOnlyList<ExistingContractParty> SortedParties =>
    [
        .. Contract.Parties.Where(p => OdsTypeRegistries.IsObjectRole(p.Role)),
        .. Contract.Parties.Where(p => !OdsTypeRegistries.IsObjectRole(p.Role)),
    ];

    // The focus keys follow the RENDERED order, not the server's: after a detach the helper lands
    // focus on the removed tile's neighbour, and a list ordered differently from the one on screen
    // would name the wrong neighbour.
    private IReadOnlyList<string> PartyKeys => [.. SortedParties.Select(p => p.ContractPartyId.ToString())];

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
    /// The party tile's classes. An object party (issue #169) takes <c>object</c> so the tile can be
    /// marked as naming what the agreement is ABOUT rather than a side of it.
    /// </summary>
    private static string TileClass(ContractPartyRole role) =>
        OdsTypeRegistries.IsObjectRole(role) ? "con-party-tile object" : "con-party-tile";

    /// <summary>
    /// The role overline's classes: <c>unset</c> for a role this build cannot name — an ABSENCE, not
    /// a category — and <c>object</c> for one naming the thing contracted over.
    /// </summary>
    /// <remarks>
    /// Built here rather than interpolated in the markup because two <c>@(…)</c> expressions side by
    /// side in one attribute value do not parse inside a <c>@&lt;text&gt;</c> <c>RenderFragment</c>
    /// lambda — see <c>docs/frontend-mudblazor-gotchas.md</c>.
    /// </remarks>
    private static string RoleClass(ContractPartyRole role) =>
        "con-role"
        + (PartyRoleLabel.IsNamed(role) ? "" : " unset")
        + (OdsTypeRegistries.IsObjectRole(role) ? " object" : "");

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
        /// The caption: the record's own TYPE and nothing else — "Property", "Person",
        /// "Organization". The KIND is already said by the tile's icon and by the record the tile
        /// names, so prefixing it ("Account · Property") spends the caption restating the glyph and
        /// buries the one word the reader came for. It stays as the FALLBACK, for a party whose
        /// target did not resolve and which therefore has no type to state.
        /// </summary>
        /// <remarks>
        /// This is the same caption the party tiles carry — the bare type label when there is one —
        /// which is what the design system's <c>PartyTile</c> means by <c>typeLabel || kindLabel</c>.
        /// </remarks>
        public string Caption => string.IsNullOrWhiteSpace(TypeLabel) ? KindLabel : TypeLabel;
    }

    private static PartyVisual Resolve(ExistingContractParty party)
    {
        switch (party.Kind)
        {
            case ContractPartyKind.Account when party.Account is { } a:
            {
                // An account whose type did not resolve has no type to STATE, so the caption falls
                // back to the kind: "Account" carries more than the bare word "Unknown", which names
                // nothing a reader can act on.
                var typeLabel = a.Type is AccountType.Unknown ? null : AccountTypeVisuals.Label(a.Type);
                return new PartyVisual("Account", a.Name, typeLabel,
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
            // Not gated on the archive state: the server refuses no party write on an archived
            // contract, so an archived party edits exactly as any other does.
            items.Add(new OdsMenuItem
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
