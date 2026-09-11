using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Pages.Finance;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The Aliases section of an expanded contact card (issue #48 §3, AC 36, 40, 46).
/// </summary>
/// <remarks>
/// These RENDER the component rather than calling a derivation, because the rules under test are
/// about what the markup produces: that an alias is a TILE with a menu rather than a chip (v1
/// specified an <c>OdsChip</c>, which exposes no <c>Class</c>, no splat and no positioning context,
/// so it could not host a ⋯ trigger at a 24×24 target at all), and that the menu's item set depends
/// on three distinct permission gates rather than one.
/// </remarks>
public class ContactAliasSectionTests
{
    private static readonly Guid ContactId = Guid.NewGuid();

    private static ExistingContact Contact(bool archived = false, params ExistingContactAlias[] aliases) => new()
    {
        ContactId = ContactId,
        ResolvedDisplayName = "Karoline Hansen",
        NormalizedName = "KAROLINE HANSEN",
        ExternalUid = $"urn:uuid:{ContactId}",
        Type = ContactType.Person,
        Archived = archived ? new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null,
        PersonDetails = new PersonDetailsDto { FirstName = "Karoline", LastName = "Hansen" },
        Aliases = aliases,
    };

    private static ExistingContactAlias Alias(string value, string? label = null) => new()
    {
        Id = Guid.NewGuid(),
        ContactId = ContactId,
        Value = value,
        Label = label,
    };

    private static IRenderedComponent<SectionHost> Render(
        ExistingContact contact, bool canCreate = true, bool canUpdate = true, bool canDelete = true)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(Mock.Of<IContactsApiClient>());
        ctx.Services.AddSingleton(Mock.Of<IClipboardService>());

        return ctx.Render<SectionHost>(p => p
            .Add(h => h.Contact, contact)
            .Add(h => h.CanCreate, canCreate)
            .Add(h => h.CanUpdate, canUpdate)
            .Add(h => h.CanDelete, canDelete));
    }

    /// <summary>
    /// The section next to a MudPopoverProvider. MudBlazor portals an OPEN menu into that provider,
    /// so without one in the same tree the items render nowhere and every menu assertion would pass
    /// vacuously against an empty popover.
    /// </summary>
    public sealed class SectionHost : Microsoft.AspNetCore.Components.ComponentBase
    {
        [Microsoft.AspNetCore.Components.Parameter] public ExistingContact Contact { get; set; } = default!;
        [Microsoft.AspNetCore.Components.Parameter] public bool CanCreate { get; set; }
        [Microsoft.AspNetCore.Components.Parameter] public bool CanUpdate { get; set; }
        [Microsoft.AspNetCore.Components.Parameter] public bool CanDelete { get; set; }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<ContactAliasSection>(1);
            builder.AddComponentParameter(2, nameof(ContactAliasSection.Contact), Contact);
            builder.AddComponentParameter(3, nameof(ContactAliasSection.CanCreate), CanCreate);
            builder.AddComponentParameter(4, nameof(ContactAliasSection.CanUpdate), CanUpdate);
            builder.AddComponentParameter(5, nameof(ContactAliasSection.CanDelete), CanDelete);
            builder.CloseComponent();
        }
    }

    // AC 36. An alias is a tile in the section's OWN grid — not an OdsChip, and not
    // ContactDetailPanel's .cp-tile-grid, whose model is positional and carries IsPrimary.
    [Fact]
    public void An_alias_renders_as_a_tile_in_its_own_grid_never_as_a_chip()
    {
        var cut = Render(Contact(aliases: [Alias("Berg", "maiden name")]));

        Assert.Single(cut.FindAll(".odc-alias-grid"));
        var tile = Assert.Single(cut.FindAll(".odc-alias-tile"));
        Assert.Equal("Berg", tile.QuerySelector(".odc-alias-value")!.TextContent.Trim());
        Assert.Equal("maiden name", tile.QuerySelector(".odc-alias-foot")!.TextContent.Trim());

        // The menu lives inside the tile, which is what an OdsChip could not host.
        Assert.NotNull(tile.QuerySelector(".odc-alias-menu"));
        Assert.Empty(cut.FindAll(".odc-chip"));
        Assert.Empty(cut.FindAll(".cp-tile-grid"));
    }

    // The value truncates with an ellipsis, so the FULL string has to survive somewhere reachable —
    // on the tile's title AND, for assistive tech, in the menu's accessible name. Never title alone.
    [Fact]
    public void The_full_value_reaches_both_the_title_and_the_menus_accessible_name()
    {
        const string Long = "Karoline Marie Hansen-Berg of the Northern Reaches";
        var cut = Render(Contact(aliases: [Alias(Long)]));

        var tile = Assert.Single(cut.FindAll(".odc-alias-tile"));
        Assert.Equal(Long, tile.QuerySelector(".odc-alias-value")!.GetAttribute("title"));
        Assert.Contains(
            $"Actions for alias: {Long}",
            cut.Markup,
            StringComparison.Ordinal);
    }

    [Fact]
    public void An_unlabelled_alias_renders_no_label_line()
    {
        var cut = Render(Contact(aliases: [Alias("Kari")]));

        Assert.Empty(cut.FindAll(".odc-alias-foot"));
    }

    // The empty line is the SECTION's own, not .cp-empty-row — whose copy names only address, email
    // and phone, and whose entry count does not cover aliases.
    [Theory]
    [InlineData(true, false, "No aliases yet — use the ⋯ menu to add one.")]
    [InlineData(false, false, "No aliases.")]
    [InlineData(true, true, "No aliases.")]
    public void The_empty_line_reflects_whether_an_alias_could_be_added(bool canCreate, bool archived, string expected)
    {
        var cut = Render(Contact(archived), canCreate: canCreate);

        Assert.Equal(expected, cut.Find(".odc-aliases-empty").TextContent.Trim());
        Assert.Empty(cut.FindAll(".cp-empty-row"));
    }

    // AC 40. The copy items are UNCONDITIONAL, which is what keeps the menu from ever being empty —
    // v2's "withheld when empty" clause was unsatisfiable — and the ⋯ trigger always rendered.
    [Fact]
    public void A_read_only_principal_still_gets_a_menu_with_both_copy_items_and_neither_write_item()
    {
        var cut = Render(Contact(aliases: [Alias("Berg")]), canCreate: false, canUpdate: false, canDelete: false);

        Assert.NotNull(cut.Find(".odc-alias-menu"));
        var items = MenuLabels(cut);
        Assert.Equal(["Copy alias", "Copy ID"], items);
    }

    // An archived contact is read-only for the same reason, even for a fully-permissioned principal.
    [Fact]
    public void An_archived_contact_offers_the_copy_items_only()
    {
        var cut = Render(Contact(archived: true, aliases: [Alias("Berg")]));

        Assert.Equal(["Copy alias", "Copy ID"], MenuLabels(cut));
    }

    // AC 40's second half. ContactDetailPanel today gates Edit AND Delete on one flag, so a
    // contacts.update-without-contacts.delete principal is offered a Delete that 403s. This section
    // deliberately does not inherit that.
    [Fact]
    public void Update_without_delete_gets_Edit_but_not_Delete()
    {
        var cut = Render(Contact(aliases: [Alias("Berg")]), canUpdate: true, canDelete: false);

        var items = MenuLabels(cut);
        Assert.Contains("Edit", items);
        Assert.DoesNotContain("Delete", items);
    }

    [Fact]
    public void Delete_without_update_gets_Delete_but_not_Edit()
    {
        var cut = Render(Contact(aliases: [Alias("Berg")]), canUpdate: false, canDelete: true);

        var items = MenuLabels(cut);
        Assert.Contains("Delete", items);
        Assert.DoesNotContain("Edit", items);
    }

    // AC 46's structural half: every action is a real focusable control in the menu, so an alias can
    // be added, edited and deleted without a pointer. (The trigger is an OdsMenu, the app's one
    // keyboard-operable menu primitive, which is why no new widget type is introduced.)
    [Fact]
    public void Every_action_is_a_focusable_item_reachable_from_the_trigger()
    {
        var cut = Render(Contact(aliases: [Alias("Berg")]));

        // The trigger itself is a real button, so Tab reaches it…
        Assert.NotNull(cut.Find(".odc-alias-menu button"));
        // …and every action behind it is a list item, not a pointer-only affordance.
        Assert.Equal(["Copy alias", "Edit", "Copy ID", "Delete"], MenuLabels(cut));
    }

    // Aliases arrive INLINE on the contact (§6), so the section seeds from the already-loaded record.
    // That is why it has no load-failure state: there is no per-card fetch to fail.
    [Fact]
    public void The_section_renders_from_the_inline_collection_without_fetching()
    {
        var api = new Mock<IContactsApiClient>(MockBehavior.Strict);
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(api.Object);
        ctx.Services.AddSingleton(Mock.Of<IClipboardService>());

        var cut = ctx.Render<ContactAliasSection>(p => p
            .Add(c => c.Contact, Contact(aliases: [Alias("Berg"), Alias("Kari", "nickname")])));


        Assert.Equal(2, cut.FindAll(".odc-alias-tile").Count);
        // MockBehavior.Strict: any call at all would have thrown by now.
        api.VerifyNoOtherCalls();
    }

    // Order is the order the API returned — no client re-sort, since a .NET comparer cannot reproduce
    // a MariaDB _ci ordering on accented input.
    [Fact]
    public void Aliases_render_in_the_order_they_were_supplied()
    {
        var cut = Render(Contact(aliases: [Alias("Zed"), Alias("alpha"), Alias("Émile")]));

        Assert.Equal(
            ["Zed", "alpha", "Émile"],
            cut.FindAll(".odc-alias-value").Select(node => node.TextContent.Trim()));
    }

    /// <summary>
    /// Opens the tile's ⋯ menu and returns its item labels. The menu has to be OPENED first: MudBlazor
    /// renders the items into the popover only then, so a closed-menu assertion would be vacuous.
    /// </summary>
    private static List<string> MenuLabels(IRenderedComponent<SectionHost> cut)
    {
        cut.Find(".odc-alias-menu button").Click();
        // The item's own body span, not the whole item: OdsMenu renders the leading and trailing
        // glyphs as aria-hidden Material Icons LIGATURES, so an item's raw TextContent reads
        // "content_copyCopy alias" — the ligature text, which no user ever sees or hears.
        return [.. cut.FindAll(".mud-menu-item .odc-menu-item-body > span:first-child")
            .Select(node => node.TextContent.Trim())
            .Where(text => text.Length > 0)];
    }
}
