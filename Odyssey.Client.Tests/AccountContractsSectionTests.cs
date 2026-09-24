using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The account record's Contracts section — the design system's <c>AccountDetail</c> tile per
/// contract naming the account: the contract's TYPE as the overline, its name as the value,
/// "roles · status" as the foot, and a ⋯ menu of View / Copy name / Copy ID.
/// </summary>
public class AccountContractsSectionTests
{
    static AccountContractsSectionTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private static readonly Guid AccountId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid ContractId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static AccountContractLink Mortgage() => new()
    {
        ContractId = ContractId,
        Name = "Mortgage with a name long enough to wrap",
        Type = ContractType.Loan,
        Status = ContractStatus.Active,
        Roles = [ContractPartyRole.Borrower, ContractPartyRole.Collateral],
    };

    private static IRenderedComponent<Host> Render(params AccountContractLink[] rows)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(Mock.Of<IClipboardService>());
        var client = new Mock<IAccountsApiClient>();
        client.Setup(c => c.ListContractsAsync(AccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<AccountContractLink>>.Success([.. rows], HttpStatusCode.OK));
        ctx.Services.AddSingleton(client.Object);
        return ctx.Render<Host>();
    }

    [Fact]
    public void A_contract_reads_as_its_type_its_name_and_the_roles_then_the_status()
    {
        var cut = Render(Mortgage());

        var tile = cut.Find(".con-party-tile");
        Assert.Equal(OdsTypeRegistries.ContractTypeOf(ContractType.Loan).Label, tile.QuerySelector(".con-role")!.TextContent.Trim());
        Assert.Contains("Mortgage with a name long enough to wrap", tile.TextContent, StringComparison.Ordinal);
        Assert.Equal("Borrower · Collateral · Active",
            tile.QuerySelector(".odc-infotile-foot")!.TextContent.Trim());
    }

    /// <summary>A role this build cannot name reads as the party tile's stated absence, never as a number.</summary>
    [Fact]
    public void An_unrecognised_role_reads_as_a_stated_absence_in_the_foot()
    {
        var link = Mortgage();
        link.Roles = [(ContractPartyRole)int.MaxValue];

        Assert.Equal($"{PartyRoleLabel.For((ContractPartyRole)int.MaxValue)} · Active", AccountContractsSection.Foot(link));
    }

    [Fact]
    public void The_menu_is_view_then_the_two_copies_and_view_opens_the_contracts_page()
    {
        var cut = Render(Mortgage());

        cut.Find(".con-tile-menu button").Click();
        cut.WaitForElement("div.mud-menu-item");
        var labels = cut.FindAll(".mud-menu-item .odc-menu-item-body > span:first-child")
            .Select(n => n.TextContent.Trim()).Where(t => t.Length > 0).ToList();
        Assert.Equal(["View", "Copy name", "Copy ID"], labels);

        cut.FindAll(".mud-menu-item").First(i => i.TextContent.Contains("View", StringComparison.Ordinal)).Click();
        Assert.EndsWith("/contracts", cut.Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void No_rows_render_no_grid_so_the_hosts_empty_line_stands_alone()
    {
        var cut = Render();

        Assert.Empty(cut.FindAll(".con-party-tile"));
        Assert.Empty(cut.FindAll(".odc-tilegrid"));
    }

    /// <summary>The section beside a MudPopoverProvider, which an open menu portals into.</summary>
    public sealed class Host : ComponentBase
    {
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AccountContractsSection>(1);
            builder.AddComponentParameter(2, nameof(AccountContractsSection.AccountId), AccountId);
            builder.CloseComponent();
        }
    }
}
