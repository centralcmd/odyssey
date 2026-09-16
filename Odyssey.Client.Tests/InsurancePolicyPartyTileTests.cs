using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using Odyssey.Client.Pages.Finance;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The ⋯ menu on an insurance policy's party tiles (Odyssey Design System · Insurance "Parties").
/// </summary>
/// <remarks>
/// <para>
/// The design system replaced the tile's hover-revealed edit button with the same ⋯ menu the
/// contact-method tiles carry, in the same corner at the same target — a menu that appears only on
/// hover is invisible to a touch user and to anyone reading the grid without a pointer. These RENDER
/// the component, because that is the rule: the affordance has to be in the markup unconditionally,
/// which a derivation test could not tell apart from one that is merely styled to zero opacity.
/// </para>
/// <para>
/// The other rule pinned here is the unnamed member's item set. Edit stays withheld — its record is
/// not in the picker, so the dialog could not round-trip it — but Copy ID and Remove survive, because
/// detaching an unresolvable link needs only the link and is exactly the cleanup a reader most needs.
/// </para>
/// </remarks>
public class InsurancePolicyPartyTileTests
{
    private static readonly Guid AcmeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GhostId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static InsurancePolicyLinkTiles.LinkTileMember Member(
        Guid id, string display, LinkAvailability state = LinkAvailability.Available) => new()
        {
            Key = id.ToString(),
            Display = display,
            TypeLabel = state == LinkAvailability.Available ? "Organization" : null,
            State = state,
        };

    private static IRenderedComponent<TilesHost> Render(
        IReadOnlyList<InsurancePolicyLinkTiles.LinkTileMember> members,
        bool writable = true,
        bool isAccount = false)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(Mock.Of<IClipboardService>());

        return ctx.Render<TilesHost>(p => p
            .Add(h => h.Members, members)
            .Add(h => h.Writable, writable)
            .Add(h => h.IsAccount, isAccount));
    }

    /// <summary>
    /// The tiles next to a MudPopoverProvider. MudBlazor portals an OPEN menu into that provider, so
    /// without one in the same tree the items render nowhere and every menu assertion would pass
    /// vacuously against an empty popover.
    /// </summary>
    public sealed class TilesHost : Microsoft.AspNetCore.Components.ComponentBase
    {
        [Microsoft.AspNetCore.Components.Parameter]
        public IReadOnlyList<InsurancePolicyLinkTiles.LinkTileMember> Members { get; set; } = [];

        [Microsoft.AspNetCore.Components.Parameter] public bool Writable { get; set; }

        [Microsoft.AspNetCore.Components.Parameter] public bool IsAccount { get; set; }

        public List<(InsurancePartyRole Role, Guid TargetId)> Edited { get; } = [];

        public List<(InsurancePartyRole Role, Guid TargetId)> Removed { get; } = [];

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<InsurancePolicyLinkTiles>(1);
            builder.AddComponentParameter(2, nameof(InsurancePolicyLinkTiles.Members), Members);
            builder.AddComponentParameter(3, nameof(InsurancePolicyLinkTiles.Label), "Insurer");
            builder.AddComponentParameter(4, nameof(InsurancePolicyLinkTiles.Role), InsurancePartyRole.Insurer);
            builder.AddComponentParameter(5, nameof(InsurancePolicyLinkTiles.IsAccount), IsAccount);
            if (Writable)
            {
                builder.AddComponentParameter(6, nameof(InsurancePolicyLinkTiles.OnEditParty),
                    Microsoft.AspNetCore.Components.EventCallback.Factory
                        .Create<(InsurancePartyRole, Guid)>(this, Edited.Add));
                builder.AddComponentParameter(7, nameof(InsurancePolicyLinkTiles.OnRemoveParty),
                    Microsoft.AspNetCore.Components.EventCallback.Factory
                        .Create<(InsurancePartyRole, Guid)>(this, Removed.Add));
            }
            builder.CloseComponent();
        }
    }

    // The affordance is a menu, in the markup for every member, and never the old hover-revealed
    // button — which is the whole point of the change.
    [Fact]
    public void Every_member_carries_a_menu_and_no_hover_revealed_edit_button()
    {
        var cut = Render([Member(AcmeId, "Acme Insurance"), Member(GhostId, "Unavailable", LinkAvailability.Unresolvable)]);

        Assert.Equal(2, cut.FindAll(".ins-tile-menu").Count);
        Assert.Empty(cut.FindAll(".ins-tile-edit"));
    }

    // The member's name reaches the menu's accessible name, so the action is identifiable without
    // reading the tile it sits on.
    [Fact]
    public void The_menu_names_the_member_it_acts_on()
    {
        var cut = Render([Member(AcmeId, "Acme Insurance")]);

        Assert.Contains("Actions for insurer Acme Insurance", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void A_named_member_offers_the_full_item_set()
    {
        var cut = Render([Member(AcmeId, "Acme Insurance")]);

        Assert.Equal(
            ["Copy name", "Open contact", "Edit insurer", "Copy ID", "Remove insurer"],
            MenuLabels(cut));
    }

    // An unnamed member keeps Copy ID and Remove: its name is not readable and its record is not in
    // the picker, but detaching the LINK needs neither.
    [Fact]
    public void An_unnamed_member_keeps_copy_id_and_remove_but_not_edit()
    {
        var cut = Render([Member(GhostId, "Unavailable", LinkAvailability.Unresolvable)]);

        Assert.Equal(["Copy ID", "Remove insurer"], MenuLabels(cut));
    }

    // Read-only tiles still name the link — Copy ID is what keeps the menu non-empty — but offer
    // nothing that writes.
    [Fact]
    public void Without_write_callbacks_the_menu_holds_only_the_read_actions()
    {
        var cut = Render([Member(AcmeId, "Acme Insurance")], writable: false);

        Assert.Equal(["Copy name", "Open contact", "Copy ID"], MenuLabels(cut));
    }

    // The insured-ACCOUNT collection points at an account; the other three point at contacts.
    [Fact]
    public void The_open_item_follows_the_collections_target_kind()
    {
        var cut = Render([Member(AcmeId, "Rental property")], isAccount: true);

        Assert.Contains("Open account", MenuLabels(cut));
    }

    [Fact]
    public void Remove_reports_the_role_and_target_rather_than_a_link_row_id()
    {
        var cut = Render([Member(AcmeId, "Acme Insurance")]);
        OpenMenu(cut);

        // The click handler is on MudMenuItem's own div.mud-menu-item — .odc-menu-item is a span
        // inside it, so clicking that would raise nothing.
        cut.FindAll("div.mud-menu-item")
            .First(node => node.TextContent.Contains("Remove insurer", StringComparison.Ordinal))
            .Click();

        Assert.Equal([(InsurancePartyRole.Insurer, AcmeId)], cut.Instance.Removed);
        Assert.Empty(cut.Instance.Edited);
    }

    private static void OpenMenu(IRenderedComponent<TilesHost> cut) =>
        cut.Find(".ins-tile-menu button").Click();

    /// <summary>
    /// Opens the tile's ⋯ menu and returns its item labels. The menu has to be OPENED first: MudBlazor
    /// renders the items into the popover only then, so a closed-menu assertion would be vacuous.
    /// </summary>
    private static List<string> MenuLabels(IRenderedComponent<TilesHost> cut)
    {
        OpenMenu(cut);
        // The item's own body span, not the whole item: OdsMenu renders the leading and trailing
        // glyphs as aria-hidden Material Icons LIGATURES, so an item's raw TextContent reads
        // "content_copyCopy name" — the ligature text, which no user ever sees or hears.
        return [.. cut.FindAll(".mud-menu-item .odc-menu-item-body > span:first-child")
            .Select(node => node.TextContent.Trim())
            .Where(text => text.Length > 0)];
    }
}
