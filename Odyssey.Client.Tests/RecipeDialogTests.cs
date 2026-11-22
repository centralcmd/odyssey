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
    private static AngleSharp.Dom.IElement Field(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string id) =>
        cut.Find($"input#{id}, textarea#{id}, #{id} input, #{id} textarea");

    [Fact]
    public async Task A_successful_create_posts_empty_arrays_and_raises_on_saved()
    {
        await using var ctx = PageTestContext.Create(PermissionClaims.RecipesCreate, PermissionClaims.RecipeTagsRead);
        var (cut, api, saved) = Render(ctx, null);
        NewRecipe? sent = null;
        api.Setup(a => a.CreateAsync(It.IsAny<NewRecipe>(), It.IsAny<CancellationToken>()))
            .Callback((NewRecipe body, CancellationToken _) => sent = body)
            .ReturnsAsync(ApiResult<ExistingRecipe>.Success(Read(), HttpStatusCode.Created));
        cut.WaitForAssertion(() => Field(cut, RecipeDraft.FieldId("title")));

        Field(cut, RecipeDraft.FieldId("title")).Input("Bread");
        cut.WaitForAssertion(() => Assert.Equal("Bread", Field(cut, RecipeDraft.FieldId("title")).GetAttribute("value")));
        Field(cut, RecipeDraft.FieldId("ingredients[0].name")).Input("flour");
        cut.WaitForAssertion(() => Assert.Equal("flour", Field(cut, RecipeDraft.FieldId("ingredients[0].name")).GetAttribute("value")));
        Field(cut, RecipeDraft.FieldId("steps[0].text")).Input("Knead.");
        cut.WaitForAssertion(() => Assert.Contains("Knead.", cut.Markup, StringComparison.Ordinal));
        ClickSubmit(cut, "Create recipe");

        cut.WaitForAssertion(() => Assert.NotNull(sent));
        Assert.Equal(("Bread", 4, "flour", "Knead."), (sent!.Title, sent.Servings!.Value, sent.Ingredients.Single().Name, sent.Steps.Single().Text));
        Assert.Empty(sent.PhotoIds);
        Assert.Empty(sent.AttachmentFileIds);
        Assert.Empty(sent.TagIds);
        cut.WaitForAssertion(() => Assert.Single(saved));
    }

    [Fact]
    public async Task A_422_lands_on_the_row_fields_and_the_error_summary()
    {
        await using var ctx = PageTestContext.Create(PermissionClaims.RecipesUpdate);
        var read = Read();
        var (cut, api, saved) = Render(ctx, read);
        api.Setup(a => a.UpdateAsync(read.RecipeId, It.IsAny<UpdateRecipe>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingRecipe>.Failure(HttpStatusCode.UnprocessableEntity, new ApiProblem
            {
                Status = 422,
                Errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Ingredients[0].Name"] = ["That name is too long."],
                    ["steps[0].text"] = ["Say what to do."],
                },
            }));
        cut.WaitForAssertion(() => Assert.Contains("Edit recipe", cut.Markup, StringComparison.Ordinal));

        ClickSubmit(cut, "Save");

        cut.WaitForAssertion(() => Assert.Contains("That name is too long.", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("Say what to do.", cut.Markup, StringComparison.Ordinal);
        Assert.Equal("true", Field(cut, RecipeDraft.FieldId("ingredients[0].name")).GetAttribute("aria-invalid"));
        var summary = cut.Find("button.odc-errsum");
        Assert.StartsWith("2 problems", summary.GetAttribute("aria-label"), StringComparison.Ordinal);
        summary.Click();
        Assert.Contains("Ingredient 1", cut.Find(".odc-errsum-list").TextContent, StringComparison.Ordinal);
        Assert.Contains("Step 1", cut.Find(".odc-errsum-list").TextContent, StringComparison.Ordinal);
        Assert.Empty(saved);
    }

    [Fact]
    public async Task Moving_a_row_keeps_focus_on_its_button_or_the_sibling_and_announces_it()
    {
        await using var ctx = PageTestContext.Create(PermissionClaims.RecipesUpdate);
        var read = Read() with
        {
            Ingredients =
            [
                new() { RecipeIngredientId = Guid.NewGuid(), Position = 0, Name = "tomatoes", Unit = IngredientUnit.None },
                new() { RecipeIngredientId = Guid.NewGuid(), Position = 1, Name = "salt", Unit = IngredientUnit.None },
            ],
        };
        var (cut, _, _) = Render(ctx, read);
        cut.WaitForAssertion(() => cut.Find("button[aria-label='Move ingredient 1 down']"));
        var moving = cut.Find("button[aria-label='Move ingredient 1 down']").Id!;

        cut.Find("button[aria-label='Move ingredient 1 down']").Click();

        cut.WaitForAssertion(() => Assert.Equal("Ingredient 1 moved to position 2 of 2.", cut.Find("[role=status].sr-only").TextContent));
        // The row is now last, so its Move down is aria-disabled and focus goes to its Move up.
        var expected = moving.Replace("-down", "-up", StringComparison.Ordinal);
        cut.WaitForAssertion(() => Assert.Contains(ctx.JSInterop.Invocations,
            i => i.Identifier == "odsFocusById" && Equals(i.Arguments[0], expected)));
        Assert.Equal(expected, cut.Find("button[aria-label='Move ingredient 2 up']").Id);
    }

    [Fact]
    public void Focus_after_a_move_follows_the_row_and_skips_an_aria_disabled_button()
    {
        var key = Guid.NewGuid();
        Assert.Equal(RecipeDialog.RowButtonId("step", key, RecipeDialog.RowButton.Up),
            RecipeDialog.MoveFocusTarget("step", key, RecipeDialog.RowButton.Up, newIndex: 1, count: 3));
        Assert.Equal(RecipeDialog.RowButtonId("step", key, RecipeDialog.RowButton.Down),
            RecipeDialog.MoveFocusTarget("step", key, RecipeDialog.RowButton.Up, newIndex: 0, count: 3));
        Assert.Equal(RecipeDialog.RowButtonId("step", key, RecipeDialog.RowButton.Up),
            RecipeDialog.MoveFocusTarget("step", key, RecipeDialog.RowButton.Down, newIndex: 2, count: 3));
        Assert.NotEqual(RecipeDialog.RowButtonId("step", key, RecipeDialog.RowButton.Up),
            RecipeDialog.RowButtonId("ingredient", key, RecipeDialog.RowButton.Up));
    }

    [Fact]
    public void Focus_after_a_removal_goes_to_the_next_row_then_the_previous_then_the_add_row()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        Assert.Equal(RecipeDialog.RowButtonId("ingredient", b, RecipeDialog.RowButton.Remove),
            RecipeDialog.RemoveFocusTarget("ingredient", [a, b], removedIndex: 1));
        Assert.Equal(RecipeDialog.RowButtonId("ingredient", b, RecipeDialog.RowButton.Remove),
            RecipeDialog.RemoveFocusTarget("ingredient", [a, b], removedIndex: 2));
        Assert.Equal(RecipeDialog.AddRowId("ingredient"), RecipeDialog.RemoveFocusTarget("ingredient", [], removedIndex: 0));
    }

    [Fact]
    public void The_row_announcements_say_what_happened_and_the_cap()
    {
        Assert.Equal("Ingredient 3 moved to position 2 of 5.", RecipeDialog.MovedMessage("ingredient", 2, 1, 5));
        Assert.Equal("Step 2 removed. 4 steps.", RecipeDialog.RemovedMessage("step", 1, 4));
        Assert.Equal("Step 1 removed. 0 steps.", RecipeDialog.RemovedMessage("step", 0, 0));
        Assert.Equal($"A recipe holds at most {RecipeLimits.MaxIngredients} ingredients.",
            RecipeDialog.CapMessage("ingredient", RecipeLimits.MaxIngredients, RecipeLimits.MaxIngredients));
        Assert.Null(RecipeDialog.CapMessage("step", RecipeLimits.MaxSteps - 1, RecipeLimits.MaxSteps));
    }

    [Fact]
    public async Task Removing_the_only_row_focuses_the_add_row()
    {
        await using var ctx = PageTestContext.Create(PermissionClaims.RecipesUpdate);
        var (cut, _, _) = Render(ctx, Read());
        cut.WaitForAssertion(() => cut.Find("button[aria-label='Remove step 1']"));

        cut.Find("button[aria-label='Remove step 1']").Click();

        cut.WaitForAssertion(() => Assert.Equal("Step 1 removed. 0 steps.", cut.Find("[role=status].sr-only").TextContent));
        Assert.Equal(RecipeDialog.AddRowId("step"), cut.Find("button.odc-add-row[id='rc-step-add']").Id);
        cut.WaitForAssertion(() => Assert.Contains(ctx.JSInterop.Invocations,
            i => i.Identifier == "odsFocusById" && Equals(i.Arguments[0], RecipeDialog.AddRowId("step"))));
    }
}
