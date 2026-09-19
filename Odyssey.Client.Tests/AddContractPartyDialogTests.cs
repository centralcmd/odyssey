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
/// The contract New/Edit party dialog — the contract sibling of <see cref="AddPolicyPartyDialogTests"/>.
/// Its kind picker is <see cref="OdsCardSelect"/>, so these render the dialog and drive that picker the
/// way a user would: the kind decides which picker follows it, which records that picker may offer, and
/// which of the two scalar ids the request carries.
///
/// <para>
/// Since issue #121 the picker filter is on <b>(kind, role)</b> rather than on the target alone, the
/// dialog doubles as the edit path, and both date rules are mirrored client-side. Those are what the
/// cases below add.
/// </para>
/// </summary>
public class AddContractPartyDialogTests
{
    private static readonly Guid ContractId = Guid.NewGuid();
    private static readonly Guid LinkedAccountId = Guid.NewGuid();
    private static readonly Guid FreeAccountId = Guid.NewGuid();
    private static readonly Guid LinkedPartyId = Guid.NewGuid();

    /// <summary>The contract's own start — the only anchor the From rule has (issue #121 §8 rule 3).</summary>
    private static readonly DateTime ContractStart = new(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ExistingContract Contract(ContractPartyRole linkedRole = ContractPartyRole.Unspecified) => new()
    {
        ContractId = ContractId,
        Name = "Apartment lease",
        StartDate = ContractStart,
        CreatedAtUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Parties =
        [
            new ExistingContractParty
            {
                ContractPartyId = LinkedPartyId,
                ContractId = ContractId,
                Kind = ContractPartyKind.Account,
                Account = new ContractAccountReference { AccountId = LinkedAccountId, Name = "Everyday Checking" },
                Role = linkedRole,
            },
        ],
    };

    /// <summary>The party the edit-mode cases open on: the one already on the contract.</summary>
    private static ExistingContractParty LinkedParty(ContractPartyRole role = ContractPartyRole.Unspecified) =>
        Contract(role).Parties[0];

    private static (IRenderedComponent<DialogHost> Cut, Mock<IContractsApiClient> Contracts) Render(
        ExistingContractParty? party = null,
        ContractPartyRole linkedRole = ContractPartyRole.Unspecified,
        ApiResult? addResult = null,
        List<string>? announcements = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var contracts = new Mock<IContractsApiClient>();
        contracts.Setup(c => c.AddPartyAsync(ContractId, It.IsAny<ContractPartyRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(addResult ?? ApiResult.Success(HttpStatusCode.Created));
        contracts.Setup(c => c.UpdatePartyAsync(ContractId, It.IsAny<Guid>(), It.IsAny<ContractPartyRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.OK));
        ctx.Services.AddSingleton(contracts.Object);
        ctx.Services.AddSingleton(Mock.Of<IContactQuickCreate>());
        ctx.Services.AddSingleton<AuthenticationStateProvider>(new SignedOut());

        var cut = ctx.Render<DialogHost>(parameters => parameters
            .Add(h => h.Party, party)
            .Add(h => h.LinkedRole, linkedRole)
            .Add(h => h.Announcements, announcements));
        return (cut, contracts);
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

        Assert.Contains(cut.FindAll(".odc-field-help"), h => h.TextContent.Contains("1 account available in this role", StringComparison.Ordinal));
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
            It.Is<ContractPartyRequest>(r => r.AccountId == FreeAccountId && r.ContactId == null),
            It.IsAny<CancellationToken>()), Times.Once));
    }

    // ── Roles and terms (issue #122) ───────────────────────────────────────────

    /// <summary>
    /// AC 5 — the picker filter is on <b>(kind, role)</b>, not on the target alone: a record already
    /// linked in ONE role is still offerable in ANOTHER, because uniqueness is per role.
    /// </summary>
    [Fact]
    public void A_record_linked_in_another_role_is_still_offered()
    {
        // The existing party holds Employer; the dialog opens on Unspecified, so both accounts are free.
        var (cut, _) = Render(linkedRole: ContractPartyRole.Employer);

        Assert.Contains(cut.FindAll(".odc-field-help"),
            h => h.TextContent.Contains("2 accounts available in this role", StringComparison.Ordinal));
    }

    /// <summary>
    /// AC 10 — changing the role so that a chosen record becomes ineligible clears it AND announces
    /// the discard. Clearing silently leaves a screen-reader user with no cue that a completed field
    /// went blank (WCAG 3.2.2).
    /// </summary>
    [Fact]
    public async Task Changing_the_role_discards_an_ineligible_selection_and_announces_it()
    {
        var announcements = new List<string>();
        // The existing party holds Employer, so Everyday Checking is ineligible in that role only.
        var (cut, _) = Render(linkedRole: ContractPartyRole.Employer, announcements: announcements);

        var combobox = cut.FindComponent<OdsCombobox>();
        await cut.InvokeAsync(() => combobox.Instance.ValueChanged.InvokeAsync(LinkedAccountId.ToString()));

        var role = cut.FindComponent<OdsContractPartyRoleSelect>();
        await cut.InvokeAsync(() => role.Instance.ValueChanged.InvokeAsync(nameof(ContractPartyRole.Employer)));

        Assert.Contains(announcements, a => a.Contains("Everyday Checking cleared", StringComparison.Ordinal));
        Assert.Contains(announcements, a => a.Contains("employer", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// AC 1 — the role and both dates ride on the request. The dialog always sends all three, because
    /// the write is a full replacement rather than a patch.
    /// </summary>
    [Fact]
    public async Task Saving_carries_the_role_and_the_term()
    {
        var (cut, contracts) = Render();

        var combobox = cut.FindComponent<OdsCombobox>();
        await cut.InvokeAsync(() => combobox.Instance.ValueChanged.InvokeAsync(FreeAccountId.ToString()));

        var role = cut.FindComponent<OdsContractPartyRoleSelect>();
        await cut.InvokeAsync(() => role.Instance.ValueChanged.InvokeAsync(nameof(ContractPartyRole.Seller)));

        var pickers = cut.FindComponents<OdsDatePicker>();
        await cut.InvokeAsync(() => pickers[0].Instance.ValueChanged.InvokeAsync(ContractStart.AddMonths(1)));
        await cut.InvokeAsync(() => pickers[1].Instance.ValueChanged.InvokeAsync(ContractStart.AddMonths(6)));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Create party", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => contracts.Verify(c => c.AddPartyAsync(ContractId,
            It.Is<ContractPartyRequest>(r =>
                r.Role == ContractPartyRole.Seller
                && r.FromDate == ContractStart.AddMonths(1)
                && r.ToDate == ContractStart.AddMonths(6)),
            It.IsAny<CancellationToken>()), Times.Once));
    }

    /// <summary>
    /// The From rule mirrors the server's: a party cannot be in the role before the contract began.
    /// Nothing is sent, and the message names the date.
    /// </summary>
    [Fact]
    public async Task A_from_date_before_the_contract_start_is_refused_inline_and_sends_nothing()
    {
        var (cut, contracts) = Render();

        var combobox = cut.FindComponent<OdsCombobox>();
        await cut.InvokeAsync(() => combobox.Instance.ValueChanged.InvokeAsync(FreeAccountId.ToString()));

        var pickers = cut.FindComponents<OdsDatePicker>();
        await cut.InvokeAsync(() => pickers[0].Instance.ValueChanged.InvokeAsync(ContractStart.AddDays(-1)));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Create party", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains("2025-06-01", cut.Markup, StringComparison.Ordinal));
        contracts.Verify(c => c.AddPartyAsync(It.IsAny<Guid>(), It.IsAny<ContractPartyRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>The To rule, likewise mirrored: end not before start.</summary>
    [Fact]
    public async Task A_to_date_before_the_from_date_is_refused_inline_and_sends_nothing()
    {
        var (cut, contracts) = Render();

        var combobox = cut.FindComponent<OdsCombobox>();
        await cut.InvokeAsync(() => combobox.Instance.ValueChanged.InvokeAsync(FreeAccountId.ToString()));

        var pickers = cut.FindComponents<OdsDatePicker>();
        await cut.InvokeAsync(() => pickers[0].Instance.ValueChanged.InvokeAsync(ContractStart.AddMonths(6)));
        await cut.InvokeAsync(() => pickers[1].Instance.ValueChanged.InvokeAsync(ContractStart.AddMonths(1)));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Create party", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("End date can't be before the start date.", cut.Markup, StringComparison.Ordinal));
        contracts.Verify(c => c.AddPartyAsync(It.IsAny<Guid>(), It.IsAny<ContractPartyRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// AC 3 — with a party supplied the dialog is the EDIT dialog: its title and confirm label switch,
    /// the fields are pre-filled, and the save goes to the PUT carrying that party's row id.
    /// </summary>
    [Fact]
    public void With_a_party_the_dialog_edits_it()
    {
        var party = LinkedParty(ContractPartyRole.Employer);
        var (cut, contracts) = Render(party, linkedRole: ContractPartyRole.Employer);

        Assert.Contains("Edit party", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Change the role, the linked record, or the dates", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(nameof(ContractPartyRole.Employer), cut.FindComponent<OdsContractPartyRoleSelect>().Instance.Value);

        cut.FindAll("button").Single(b => b.TextContent.Contains("Save changes", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => contracts.Verify(c => c.UpdatePartyAsync(ContractId, LinkedPartyId,
            It.Is<ContractPartyRequest>(r => r.AccountId == LinkedAccountId && r.Role == ContractPartyRole.Employer),
            It.IsAny<CancellationToken>()), Times.Once));
    }

    /// <summary>
    /// AC 8 — a server failure keyed on the record picker's field renders INLINE on that control,
    /// matched on the FIELD KEY and never on the message text. The duplicate 409 is the case the
    /// (kind, role) filter normally pre-empts and cannot in a race.
    /// </summary>
    [Fact]
    public async Task A_server_error_keyed_on_the_picker_renders_inline_there()
    {
        var conflict = ApiResult.Failure(HttpStatusCode.Conflict, new ApiProblem
        {
            Detail = "That party is already linked to the contract in that role.",
            Errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                [nameof(ContractPartyRequest.AccountId)] = ["That party is already linked to the contract in that role."],
            },
        });
        var (cut, _) = Render(addResult: conflict);

        var combobox = cut.FindComponent<OdsCombobox>();
        await cut.InvokeAsync(() => combobox.Instance.ValueChanged.InvokeAsync(FreeAccountId.ToString()));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Create party", StringComparison.Ordinal)).Click();

        // Inline on the record picker, as role="alert", not a toast: the 409 is about the value in
        // that field. The branch keys on the field name the server returned and never on the text.
        cut.WaitForAssertion(() => Assert.Contains(
            cut.FindAll(".odc-field-help.error"),
            e => e.TextContent.Contains("already linked to the contract in that role", StringComparison.Ordinal)
                && e.GetAttribute("role") == "alert"));
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

        [Parameter] public ExistingContractParty? Party { get; set; }

        [Parameter] public ContractPartyRole LinkedRole { get; set; }

        /// <summary>Collects the dialog's live-region lines, which the real host routes to its announcer.</summary>
        [Parameter] public List<string>? Announcements { get; set; }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<AddContractPartyDialog>(2);
            builder.AddComponentParameter(3, nameof(AddContractPartyDialog.Contract), Contract(LinkedRole));
            builder.AddComponentParameter(4, nameof(AddContractPartyDialog.Accounts), Accounts);
            builder.AddComponentParameter(5, nameof(AddContractPartyDialog.Open), true);
            builder.AddComponentParameter(6, nameof(AddContractPartyDialog.Party), Party);
            builder.AddComponentParameter(7, nameof(AddContractPartyDialog.OnAnnounce),
                EventCallback.Factory.Create<string>(this, m => Announcements?.Add(m)));
            builder.CloseComponent();
        }
    }
}
