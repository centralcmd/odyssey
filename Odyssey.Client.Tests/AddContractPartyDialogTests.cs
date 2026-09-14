using System.Net;
using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
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
/// The contract New party dialog — the contract sibling of <see cref="AddPolicyPartyDialogTests"/>. Its
/// kind picker is <see cref="OdsCardSelect"/>, so these render the dialog and drive that picker the way
/// a user would: the kind decides which picker follows it, which records that picker may offer, and
/// which of the two scalar ids the request carries.
/// </summary>
public class AddContractPartyDialogTests
{
    private static readonly Guid ContractId = Guid.NewGuid();
    private static readonly Guid LinkedAccountId = Guid.NewGuid();
    private static readonly Guid FreeAccountId = Guid.NewGuid();

    private static ExistingContract Contract() => new()
    {
        ContractId = ContractId,
        Name = "Apartment lease",
        CreatedAtUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Parties =
        [
            new ExistingContractParty
            {
                ContractPartyId = Guid.NewGuid(),
                ContractId = ContractId,
                Kind = ContractPartyKind.Account,
                Account = new ContractAccountReference { AccountId = LinkedAccountId, Name = "Everyday Checking" },
            },
        ],
    };

    private static (IRenderedComponent<DialogHost> Cut, Mock<IContractsApiClient> Contracts) Render()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var contracts = new Mock<IContractsApiClient>();
        contracts.Setup(c => c.AddPartyAsync(ContractId, It.IsAny<AddContractPartyRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.Created));
        ctx.Services.AddSingleton(contracts.Object);
        ctx.Services.AddSingleton(Mock.Of<IContactQuickCreate>());
        ctx.Services.AddSingleton<AuthenticationStateProvider>(new SignedOut());

        return (ctx.Render<DialogHost>(), contracts);
    }

    private static IReadOnlyList<AngleSharp.Dom.IElement> Cards(IRenderedComponent<DialogHost> cut) =>
        cut.FindAll(".odc-cardsel-opt");

    [Fact]
    public void The_kind_picker_is_a_required_radiogroup_named_by_its_visible_label()
    {
        var (cut, _) = Render();

        var group = cut.Find(".odc-cardsel");
        Assert.Equal("true", group.GetAttribute("aria-required"));
        var labelId = group.GetAttribute("aria-labelledby");
        Assert.False(string.IsNullOrEmpty(labelId));
        Assert.StartsWith("Party kind", cut.Find($"#{labelId}").TextContent.Trim(), StringComparison.Ordinal);

        Assert.Equal(["Account", "Contact"], cut.FindAll(".odc-cardsel-lab").Select(l => l.TextContent));
        Assert.Equal("true", Cards(cut)[0].GetAttribute("aria-checked"));
    }

    [Fact]
    public void An_already_linked_account_is_not_offered_and_the_arrow_key_switches_to_the_contact_picker()
    {
        var (cut, _) = Render();

        Assert.Contains(cut.FindAll(".odc-field-help"), h => h.TextContent.Contains("1 account available to link", StringComparison.Ordinal));
        Assert.Empty(cut.FindComponents<OdsContactSelect>());

        Cards(cut)[0].KeyDown(new KeyboardEventArgs { Key = "ArrowRight" });

        cut.WaitForAssertion(() => Assert.Single(cut.FindComponents<OdsContactSelect>()));
        Assert.Equal("true", Cards(cut)[1].GetAttribute("aria-checked"));
    }

    [Fact]
    public async Task Saving_an_account_party_posts_the_account_id_and_no_contact_id()
    {
        var (cut, contracts) = Render();

        var combobox = cut.FindComponent<OdsCombobox>();
        await cut.InvokeAsync(() => combobox.Instance.ValueChanged.InvokeAsync(FreeAccountId.ToString()));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Create party", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => contracts.Verify(c => c.AddPartyAsync(ContractId,
            It.Is<AddContractPartyRequest>(r => r.AccountId == FreeAccountId && r.ContactId == null),
            It.IsAny<CancellationToken>()), Times.Once));
    }

    private sealed class SignedOut : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    /// <summary>The dialog beside MudBlazor's providers, which host its modal and popovers.</summary>
    public sealed class DialogHost : ComponentBase
    {
        private static readonly IReadOnlyList<OdsOption> Accounts =
        [
            new OdsOption(LinkedAccountId.ToString(), "Everyday Checking"),
            new OdsOption(FreeAccountId.ToString(), "Savings"),
        ];

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<AddContractPartyDialog>(2);
            builder.AddComponentParameter(3, nameof(AddContractPartyDialog.Contract), Contract());
            builder.AddComponentParameter(4, nameof(AddContractPartyDialog.Accounts), Accounts);
            builder.AddComponentParameter(5, nameof(AddContractPartyDialog.Open), true);
            builder.CloseComponent();
        }
    }
}
