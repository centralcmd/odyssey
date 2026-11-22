using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Pages.Library;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Recipes;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The New / Edit recipe dialog rendered (issue #526): row fieldsets with aria-disabled moves, client validation into
/// the footer ErrorSummary, the payload's arrays, and the 409 conflict's Reload.
/// </summary>
public class RecipeDialogTests
{
    private static ExistingRecipe Read() => new()
    {
        RecipeId = Guid.NewGuid(),
        Version = 3,
        Title = "Soup",
        Servings = 4,
        DisplayedServings = 4,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
        Ingredients = [new() { RecipeIngredientId = Guid.NewGuid(), Position = 0, Name = "tomatoes", Unit = IngredientUnit.Kilogram, Amount = 1 }],
        Steps = [new() { RecipeStepId = Guid.NewGuid(), Position = 0, Text = "Roast." }],
        TagIds = [Guid.NewGuid()],
    };

    private static (IRenderedComponent<Bunit.Rendering.ContainerFragment> Cut, Mock<IRecipesApiClient> Api, List<ExistingRecipe> Saved) Render(
        BunitContext ctx, ExistingRecipe? recipe, bool canTags = true)
    {
        var api = new Mock<IRecipesApiClient>();
        ctx.Services.AddSingleton(api.Object);
        var saved = new List<ExistingRecipe>();
        var cut = ctx.Render(b =>
        {
            b.OpenComponent<MudBlazor.MudDialogProvider>(0);
            b.CloseComponent();
            b.OpenComponent<MudBlazor.MudPopoverProvider>(1);
            b.CloseComponent();
            b.OpenComponent<RecipeDialog>(2);
            b.AddComponentParameter(3, nameof(RecipeDialog.Open), true);
            b.AddComponentParameter(4, nameof(RecipeDialog.Recipe), recipe);
            b.AddComponentParameter(5, nameof(RecipeDialog.CanReadTags), canTags);
            b.AddComponentParameter(6, nameof(RecipeDialog.OnSaved), EventCallback.Factory.Create<ExistingRecipe>(saved, r => saved.Add(r)));
            b.CloseComponent();
        });
        return (cut, api, saved);
    }

    private static void ClickSubmit(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string label) =>
        cut.FindAll("button").Last(b => b.TextContent.Trim().EndsWith(label, StringComparison.Ordinal)).Click();

    [Fact]
    public async Task A_new_recipe_has_one_row_each_with_both_moves_aria_disabled_and_validates_on_submit()
    {
        await using var ctx = PageTestContext.Create(PermissionClaims.RecipesCreate);
        var (cut, api, _) = Render(ctx, null);

        cut.WaitForAssertion(() => Assert.Contains("Ingredient 1", cut.Markup, StringComparison.Ordinal));
        var up = cut.Find("button[aria-label='Move ingredient 1 up']");
        Assert.Equal("true", up.GetAttribute("aria-disabled"));
        Assert.Contains("Ingredient 1 is already first.", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Tags", cut.Markup, StringComparison.Ordinal);

        ClickSubmit(cut, "Create recipe");

        cut.WaitForAssertion(() => Assert.Contains("Give the recipe a title.", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("odc-errsum", cut.Markup, StringComparison.Ordinal);
        api.Verify(a => a.CreateAsync(It.IsAny<NewRecipe>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task An_edit_puts_null_photos_and_files_and_the_read_version()
    {
        await using var ctx = PageTestContext.Create(PermissionClaims.RecipesUpdate);
        var read = Read();
        var (cut, api, saved) = Render(ctx, read, canTags: false);
        UpdateRecipe? sent = null;
        api.Setup(a => a.UpdateAsync(read.RecipeId, It.IsAny<UpdateRecipe>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, UpdateRecipe body, CancellationToken _) => sent = body)
            .ReturnsAsync(ApiResult<ExistingRecipe>.Success(read with { Version = 4 }, HttpStatusCode.OK));

        cut.WaitForAssertion(() => Assert.Contains("Edit recipe", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("Add tag", cut.Markup, StringComparison.Ordinal);
        ClickSubmit(cut, "Save");

        cut.WaitForAssertion(() => Assert.NotNull(sent));
        Assert.Equal((3, (Guid[]?)null, (Guid[]?)null), (sent!.Version, sent.PhotoIds, sent.AttachmentFileIds));
        Assert.Equal(read.TagIds, sent.TagIds);
        cut.WaitForAssertion(() => Assert.Single(saved));
    }

    [Fact]
    public async Task A_version_conflict_keeps_the_changes_and_offers_reload()
    {
        await using var ctx = PageTestContext.Create(PermissionClaims.RecipesUpdate);
        var read = Read();
        var (cut, api, saved) = Render(ctx, read);
        api.Setup(a => a.UpdateAsync(read.RecipeId, It.IsAny<UpdateRecipe>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingRecipe>.Failure(HttpStatusCode.Conflict,
                new ApiProblem { Status = 409, Code = RecipeErrorCodes.VersionConflict, Title = "Conflict" }));

        cut.WaitForAssertion(() => Assert.Contains("Edit recipe", cut.Markup, StringComparison.Ordinal));
        ClickSubmit(cut, "Save");

        cut.WaitForAssertion(() => Assert.Contains("Someone else changed this recipe while you were editing.", cut.Markup, StringComparison.Ordinal));
        Assert.NotNull(cut.Find($"#{RecipeDialog.ReloadId}"));
        Assert.Empty(saved);
    }
}
