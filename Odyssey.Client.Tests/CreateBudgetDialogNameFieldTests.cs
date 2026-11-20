using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Pages.Finance;
using Odyssey.Client.Services;
using Odyssey.Client.Theme;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The budget name is an <c>OdsField</c>, which binds on input rather than on change. The create
/// dialog must still refuse an empty name with the field's own error, and take a typed one.
/// </summary>
public class CreateBudgetDialogNameFieldTests
{
    private const string NameError = "Give the budget a name.";

    [Fact]
    public async Task An_empty_name_is_refused_with_the_fields_error_and_a_typed_one_clears_it()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var budgets = new Mock<IBudgetsApiClient>();
        ctx.Services.AddSingleton(budgets.Object);
        ctx.Services.AddSingleton(Mock.Of<IUserPreferenceService>());
        ctx.Services.AddSingleton(Mock.Of<IReferenceDataCache>());

        var cut = ctx.Render<Host>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".ods-modal input")));

        Submit(cut);
        cut.WaitForAssertion(() => Assert.Contains(NameError, cut.Markup, StringComparison.Ordinal));

        cut.FindAll(".ods-modal input").First().Input("January 2027");
        Submit(cut);

        cut.WaitForAssertion(() => Assert.DoesNotContain(NameError, cut.Markup, StringComparison.Ordinal));
        // No base currency is set in this host, so the save stops there and never reaches the API.
        budgets.Verify(b => b.CreateAsync(It.IsAny<NewBudget>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static void Submit(IRenderedComponent<Host> cut) =>
        cut.FindAll(".ods-modal button").Single(b => b.TextContent.Trim().EndsWith("Create budget", StringComparison.Ordinal)).Click();

    /// <summary>The dialog beside MudBlazor's providers, which portal the modal it renders into.</summary>
    public sealed class Host : ComponentBase
    {
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<MudSnackbarProvider>(2);
            builder.CloseComponent();
            builder.OpenComponent<CreateBudgetDialog>(3);
            builder.AddComponentParameter(4, nameof(CreateBudgetDialog.Open), true);
            builder.CloseComponent();
        }
    }
}
