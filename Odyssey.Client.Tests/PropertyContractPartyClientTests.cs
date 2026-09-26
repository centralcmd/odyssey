using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Pages.Finance;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The property half of issue #208 on the client: the property record's Contracts section, the
/// delete confirmation's party-link line, and the party picker's property options.
/// </summary>
public class PropertyContractPartyClientTests
{
    static PropertyContractPartyClientTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private static readonly Guid PropertyId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private static PropertyContractLink Link(string name, ContractStatus status, params ContractPartyRole[] roles) => new()
    {
        ContractId = Guid.NewGuid(),
        Name = name,
        Type = ContractType.Loan,
        Status = status,
        Roles = [.. roles],
    };

    private static (BunitContext Ctx, Mock<IPropertiesApiClient> Client) Context(params PropertyContractLink[] rows)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(Mock.Of<IClipboardService>());
        var client = new Mock<IPropertiesApiClient>();
        client.Setup(c => c.ListContractsAsync(PropertyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<PropertyContractLink>>.Success([.. rows], HttpStatusCode.OK));
        ctx.Services.AddSingleton(client.Object);
        return (ctx, client);
    }

    // ── Contracts section ─────────────────────────────────────────────────────

    [Fact]
    public void The_section_draws_one_tile_per_contract_with_roles_then_status_and_mutes_an_archived_one()
    {
        var (ctx, _) = Context(
            Link("Mortgage", ContractStatus.Active, ContractPartyRole.Collateral, ContractPartyRole.Property),
            Link("Old cover", ContractStatus.Archived, ContractPartyRole.Insured));

        var cut = ctx.Render<PropertyContractsSection>(p => p.Add(s => s.PropertyId, PropertyId));

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".con-party-tile").Count));
        var tiles = cut.FindAll(".con-party-tile");
        Assert.Equal("Collateral · Property · Active", tiles[0].QuerySelector(".odc-infotile-foot")!.TextContent.Trim());
        Assert.DoesNotContain("tone-muted", tiles[0].InnerHtml, StringComparison.Ordinal);
        Assert.Contains("tone-muted", tiles[1].InnerHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_list_renders_no_grid_so_the_hosts_empty_line_stands_alone()
    {
        var (ctx, _) = Context();

        var cut = ctx.Render<PropertyContractsSection>(p => p.Add(s => s.PropertyId, PropertyId));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[aria-busy='true']")));
        Assert.Empty(cut.FindAll(".odc-tilegrid"));
    }

    // ── Delete confirmation ───────────────────────────────────────────────────

    private static ExistingProperty Property(int? contractCount) => new()
    {
        PropertyId = PropertyId,
        Name = "Maple St house",
        Description = "Home",
        CurrencyCode = "USD",
        Type = PropertyType.RealEstate,
        ContractCount = contractCount,
    };

    private static IRenderedComponent<DeleteHost> RenderDelete(
        BunitContext ctx, ExistingProperty property, bool canReadContracts) =>
        ctx.Render<DeleteHost>(p => p
            .Add(h => h.Property, property)
            .Add(h => h.CanReadContracts, canReadContracts));

    [Fact]
    public void Delete_counts_the_party_links_and_their_contracts_and_says_the_contracts_are_kept()
    {
        var (ctx, _) = Context(
            Link("Mortgage", ContractStatus.Active, ContractPartyRole.Collateral, ContractPartyRole.Property),
            Link("Cover", ContractStatus.Active, ContractPartyRole.Insured));

        var cut = RenderDelete(ctx, Property(contractCount: 2), canReadContracts: true);

        cut.WaitForAssertion(() => Assert.Contains("3 party links on 2 contracts", cut.Markup, StringComparison.Ordinal));
        // Announced: the count lands after the dialog has rendered (WCAG 4.1.3).
        Assert.Contains(cut.FindAll("[role='status'][aria-live='polite']"),
            e => e.TextContent.Contains("3 party links on 2 contracts", StringComparison.Ordinal));
        Assert.Contains("Contracts are kept", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>Without contracts.read the line states no number — never "none".</summary>
    [Fact]
    public void Delete_without_contracts_read_states_the_links_without_counting_them_and_reads_nothing()
    {
        var (ctx, client) = Context();

        var cut = RenderDelete(ctx, Property(contractCount: null), canReadContracts: false);

        Assert.Contains("Any contract party links naming it", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("No contract party links", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Contracts are kept", cut.Markup, StringComparison.Ordinal);
        client.Verify(c => c.ListContractsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Delete_of_a_property_on_no_contract_says_so_and_drops_the_kept_sentence()
    {
        var (ctx, client) = Context();

        var cut = RenderDelete(ctx, Property(contractCount: 0), canReadContracts: true);

        Assert.Contains("No contract party links", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Contracts are kept", cut.Markup, StringComparison.Ordinal);
        client.Verify(c => c.ListContractsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Picker options ────────────────────────────────────────────────────────

    [Fact]
    public void Party_options_keep_archived_and_disposed_properties_labelled_owned_first()
    {
        var owned = Property(null) with { PropertyId = Guid.NewGuid(), Name = "b house", Status = PropertyStatus.Owned };
        var disposed = Property(null) with { PropertyId = Guid.NewGuid(), Name = "a car", Type = PropertyType.Vehicle, Status = PropertyStatus.Disposed };
        var archived = Property(null) with { PropertyId = Guid.NewGuid(), Name = "a cabin", Status = PropertyStatus.Archived, Archived = DateTime.UtcNow };

        var options = PropertyVisuals.PartyOptions([owned, disposed, archived]);
        var states = PropertyVisuals.PartyLinkStates([owned, disposed, archived]);

        Assert.Equal(["a car (disposed)", "b house", "a cabin (archived)"], options.Select(o => o.Label));
        Assert.Equal("disposed", states[disposed.PropertyId.ToString()]);
        Assert.Equal("archived", states[archived.PropertyId.ToString()]);
        Assert.False(states.ContainsKey(owned.PropertyId.ToString()));
    }

    // ── Card header counts ────────────────────────────────────────────────────

    /// <summary>
    /// The collapsed card's Contracts count: absent when the server withheld it (no contracts.read),
    /// absent at zero — a property party to nothing states no count, as on an account — and shown
    /// otherwise, after Estimates and Smart tags.
    /// </summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData(0, false)]
    [InlineData(2, true)]
    public void The_card_counts_carry_contracts_only_when_there_are_some_to_count(int? contractCount, bool shown)
    {
        var counts = PropertiesCard.CountsFor(Property(contractCount), canReadEstimates: true);

        var contracts = counts.SingleOrDefault(c => c.Label == "Contracts");
        Assert.Equal(shown, contracts is not null);
        if (shown)
        {
            Assert.Equal("handshake", contracts!.Icon);
            Assert.Equal("2", contracts.Value);
            Assert.Equal(["Estimates", "Smart tags", "Contracts"], counts.Select(c => c.Label));
        }
    }

    [Fact]
    public void The_card_counts_drop_estimates_without_the_estimates_claim()
    {
        var counts = PropertiesCard.CountsFor(Property(1), canReadEstimates: false);

        Assert.Equal(["Smart tags", "Contracts"], counts.Select(c => c.Label));
    }

    /// <summary>The dialog beside MudBlazor's providers, which host its modal.</summary>
    public sealed class DeleteHost : ComponentBase
    {
        [Parameter] public ExistingProperty Property { get; set; } = default!;

        [Parameter] public bool CanReadContracts { get; set; }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<DeletePropertyDialog>(2);
            builder.AddComponentParameter(3, nameof(DeletePropertyDialog.Property), Property);
            builder.AddComponentParameter(4, nameof(DeletePropertyDialog.Open), true);
            builder.AddComponentParameter(5, nameof(DeletePropertyDialog.CanReadContracts), CanReadContracts);
            builder.CloseComponent();
        }
    }
}
