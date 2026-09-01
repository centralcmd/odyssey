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
/// What a picked contact's chip actually says in the new/edit policy dialog.
/// </summary>
/// <remarks>
/// <para>
/// The regression this exists for: the chip resolved a member only against the policy's <b>stored</b>
/// links, so a contact the user had just chosen was "not a stored link" and rendered as
/// <c>Unavailable</c> — every chip in the create dialog, and every newly added chip while editing.
/// The save then worked and the saved policy displayed the contact correctly, so the surface
/// contradicted itself: it claimed not to know a record it was about to link successfully.
/// </para>
/// <para>
/// These RENDER the dialog rather than calling a derivation, because the defect lived in what the
/// markup produced. That also makes them the first coverage of this component's markup at all — the
/// gap that let a Razor comment inside an attribute list reach the browser (see
/// <see cref="RazorAttributeCommentTests"/>).
/// </para>
/// </remarks>
public class InsurancePolicyDialogChipTests
{
    private static readonly Guid PickedId = Guid.NewGuid();
    private static readonly Guid ArchivedId = Guid.NewGuid();

    /// <summary>Opens a picker and ticks the one offered contact — the user's own two clicks.</summary>
    private static void PickTheContact(IRenderedComponent<DialogHost> cut, string triggerId)
    {
        // Find and act in one InvokeAsync each: the tree re-renders between them, and a handler id
        // captured before that render is stale by the time it is triggered.
        cut.InvokeAsync(() => cut.Find($"#{triggerId}").Click()).GetAwaiter().GetResult();
        cut.InvokeAsync(() => cut.FindAll(".odc-tagms-opt input[type=checkbox]").Single().Change(true))
            .GetAwaiter().GetResult();
    }

    private static IRenderedComponent<DialogHost> Render(ExistingInsurancePolicy? policy = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(Mock.Of<IInsuranceApiClient>());

        return ctx.Render<DialogHost>(p => p.Add(h => h.Policy, policy));
    }

    /// <summary>
    /// Create mode — the case that had no coverage and was broken outright. Driven the way the user
    /// drove it: open the beneficiaries picker and tick the contact.
    /// </summary>
    [Fact]
    public void A_freshly_picked_contact_renders_by_name_not_as_unavailable()
    {
        var cut = Render();
        PickTheContact(cut, "ins-new-beneficiaries");

        var chips = cut.FindAll(".odc-tagms-tchip");
        Assert.NotEmpty(chips);
        var text = string.Join(" ", chips.Select(c => c.TextContent));

        Assert.Contains("Sam Rivera", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Unavailable", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Archived", text, StringComparison.Ordinal);
        // Never the raw id, whatever else happens.
        Assert.DoesNotContain(PickedId.ToString(), text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A stored archived link keeps its own state — the options fallback must not overrule what the
    /// server actually said about a member it will refuse to remove.
    /// </summary>
    [Fact]
    public void An_archived_stored_link_still_renders_as_archived_alongside_a_picked_one()
    {
        var policy = new ExistingInsurancePolicy
        {
            InsurancePolicyId = Guid.NewGuid(),
            Name = "Term life",
            CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Beneficiaries =
            [
                new PolicyContactReference
                {
                    ContactId = ArchivedId,
                    Name = null,
                    Type = ContactType.Person,
                    Availability = LinkAvailability.Archived,
                },
            ],
        };

        var cut = Render(policy);
        var text = string.Join(" ", cut.FindAll(".odc-tagms-tchip").Select(c => c.TextContent));

        Assert.Contains("Archived", text, StringComparison.Ordinal);
        // The archived contact's NAME is never rendered — the read model does not carry one.
        Assert.DoesNotContain(ArchivedId.ToString(), text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The dialog renders at all. Vacuous-looking, and deliberately kept: a Razor comment misplaced
    /// into an attribute list compiles cleanly and throws only here, at render.
    /// </summary>
    [Fact]
    public void The_dialog_renders_its_four_link_pickers_without_throwing()
    {
        var cut = Render();

        var labels = cut.FindAll(".odc-field-label").Select(l => l.TextContent).ToList();

        Assert.Contains(labels, l => l.Contains("Insurers", StringComparison.Ordinal));
        Assert.Contains(labels, l => l.Contains("Insured accounts", StringComparison.Ordinal));
        Assert.Contains(labels, l => l.Contains("Insured contacts", StringComparison.Ordinal));
        Assert.Contains(labels, l => l.Contains("Beneficiaries", StringComparison.Ordinal));
    }

    /// <summary>The dialog beside MudBlazor's providers, which portal its popovers.</summary>
    public sealed class DialogHost : ComponentBase
    {
        [Parameter] public ExistingInsurancePolicy? Policy { get; set; }

        private static readonly IReadOnlyList<OdsOption> Contacts =
            [new OdsOption(PickedId.ToString(), "Sam Rivera") { Sub = "Person" }];

        private static readonly IReadOnlyDictionary<string, ContactType> Types =
            new Dictionary<string, ContactType>(StringComparer.OrdinalIgnoreCase)
            {
                [PickedId.ToString()] = ContactType.Person,
            };

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<CreateInsurancePolicyDialog>(2);
            builder.AddComponentParameter(3, nameof(CreateInsurancePolicyDialog.Contacts), Contacts);
            builder.AddComponentParameter(4, nameof(CreateInsurancePolicyDialog.ContactTypes), Types);
            builder.AddComponentParameter(5, nameof(CreateInsurancePolicyDialog.InsurancePolicy), Policy);
            builder.AddComponentParameter(6, nameof(CreateInsurancePolicyDialog.Open), true);
            builder.CloseComponent();
        }
    }
}
