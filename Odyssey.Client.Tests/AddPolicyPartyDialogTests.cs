using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The New / Edit party dialog — the one place a policy's four link collections are written since the
/// design system moved them out of Edit policy (a party carries a TERM, which that dialog has nowhere
/// to put).
/// </summary>
/// <remarks>
/// <para>
/// These RENDER the dialog rather than calling a derivation: the rules under test are about what the
/// markup produces from the policy it is handed — which role is preselected, which records the picker
/// is allowed to offer, and whether the stored term is loaded back into the date fields.
/// </para>
/// <para>
/// The render-smoke assertion is deliberately kept: a Razor comment misplaced into an attribute list
/// compiles cleanly and throws only here, at render (see <see cref="RazorAttributeCommentTests"/>).
/// It replaces the same guard the removed link-picker tests carried.
/// </para>
/// </remarks>
public class AddPolicyPartyDialogTests
{
    private static readonly Guid LinkedId = Guid.NewGuid();
    private static readonly Guid FreeId = Guid.NewGuid();
    private static readonly Guid PolicyId = Guid.NewGuid();

    private static ExistingInsurancePolicy Policy(params PolicyContactReference[] beneficiaries) => new()
    {
        InsurancePolicyId = PolicyId,
        Name = "Term life",
        CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Beneficiaries = [.. beneficiaries],
    };

    private static PolicyContactReference Beneficiary(Guid id, DateTime? from = null, DateTime? to = null) => new()
    {
        ContactId = id,
        Name = "Sam Rivera",
        Type = ContactType.Person,
        Availability = LinkAvailability.Available,
        FromDate = from,
        ToDate = to,
    };

    private static IRenderedComponent<DialogHost> Render(
        ExistingInsurancePolicy policy, AddPolicyPartyDialog.PartyLink? party = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(Mock.Of<IInsuranceApiClient>());

        return ctx.Render<DialogHost>(p => p
            .Add(h => h.Policy, policy)
            .Add(h => h.Party, party));
    }

    /// <summary>The dialog renders, and offers all four of the policy's roles.</summary>
    [Fact]
    public void The_dialog_renders_its_four_roles_without_throwing()
    {
        var cut = Render(Policy());

        var roles = cut.FindAll(".ins-kind-opt .ins-kind-lab").Select(l => l.TextContent).ToList();

        Assert.Equal(
            ["Insurer", "Insured account", "Insured contact", "Beneficiary"],
            roles);
    }

    /// <summary>
    /// A record already in the chosen role is filtered out of the picker, so the same record cannot
    /// be attached twice in one role.
    /// </summary>
    [Fact]
    public void An_already_linked_record_is_not_offered_in_that_role()
    {
        var cut = Render(Policy(Beneficiary(LinkedId)));

        // Insurer is the default role and holds nobody, so both contacts are offered there.
        Assert.Equal(2, OfferedCount(cut));

        PickRole(cut, "Beneficiary");

        Assert.Equal(1, OfferedCount(cut));
    }

    /// <summary>
    /// The party BEING EDITED is not "already linked" as far as its own picker is concerned — only
    /// its siblings are, or a date-only edit could not round-trip its own record.
    /// </summary>
    [Fact]
    public void The_party_being_edited_stays_in_its_own_picker()
    {
        var cut = Render(
            Policy(Beneficiary(LinkedId)),
            new AddPolicyPartyDialog.PartyLink(InsurancePartyRole.Beneficiary, LinkedId));

        Assert.Equal(2, OfferedCount(cut));
    }

    /// <summary>Editing loads the stored term back into the two date fields.</summary>
    [Fact]
    public void Editing_loads_the_stored_term()
    {
        var from = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);
        var cut = Render(
            Policy(Beneficiary(LinkedId, from, to)),
            new AddPolicyPartyDialog.PartyLink(InsurancePartyRole.Beneficiary, LinkedId));

        var dates = cut.FindAll("input")
            .Select(i => i.GetAttribute("value"))
            .Where(v => !string.IsNullOrEmpty(v))
            .ToList();

        Assert.Contains("2026-03-01", dates);
        Assert.Contains("2026-09-30", dates);
    }

    /// <summary>An undated link is the DEFAULT term — the policy's own extent — and loads as empty.</summary>
    [Fact]
    public void An_undated_link_loads_with_empty_dates()
    {
        var cut = Render(
            Policy(Beneficiary(LinkedId)),
            new AddPolicyPartyDialog.PartyLink(InsurancePartyRole.Beneficiary, LinkedId));

        var dates = cut.FindAll("input")
            .Select(i => i.GetAttribute("value"))
            .Where(v => !string.IsNullOrEmpty(v) && v!.Contains('-', StringComparison.Ordinal))
            .ToList();

        Assert.Empty(dates);
    }

    /// <summary>
    /// The help line under the picker states how many records are still linkable — the surface the
    /// filtering is actually visible on. Absent means the line said "every … is already linked", i.e.
    /// none.
    /// </summary>
    private static int OfferedCount(IRenderedComponent<DialogHost> cut)
    {
        var line = cut.FindAll(".odc-field-help")
            .Select(h => h.TextContent)
            .FirstOrDefault(h => h.Contains("available to link", StringComparison.Ordinal));

        var match = line is null
            ? System.Text.RegularExpressions.Match.Empty
            : System.Text.RegularExpressions.Regex.Match(line, @"(\d+) \w+s? available to link");

        return match.Success
            ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            : 0;
    }

    private static void PickRole(IRenderedComponent<DialogHost> cut, string label)
    {
        cut.InvokeAsync(() => cut
            .FindAll(".ins-kind-opt")
            .Single(b => b.TextContent.Contains(label, StringComparison.Ordinal))
            .Click()).GetAwaiter().GetResult();
    }

    /// <summary>The dialog beside MudBlazor's providers, which portal its popovers.</summary>
    public sealed class DialogHost : ComponentBase
    {
        [Parameter] public ExistingInsurancePolicy Policy { get; set; } = default!;

        [Parameter] public AddPolicyPartyDialog.PartyLink? Party { get; set; }

        private static readonly IReadOnlyList<OdsOption> Contacts =
        [
            new OdsOption(LinkedId.ToString(), "Sam Rivera") { Sub = "Person" },
            new OdsOption(FreeId.ToString(), "Dana Okafor") { Sub = "Person" },
        ];

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<AddPolicyPartyDialog>(2);
            builder.AddComponentParameter(3, nameof(AddPolicyPartyDialog.Policy), Policy);
            builder.AddComponentParameter(4, nameof(AddPolicyPartyDialog.Party), Party);
            builder.AddComponentParameter(5, nameof(AddPolicyPartyDialog.Contacts), Contacts);
            builder.AddComponentParameter(6, nameof(AddPolicyPartyDialog.Open), true);
            builder.CloseComponent();
        }
    }
}
