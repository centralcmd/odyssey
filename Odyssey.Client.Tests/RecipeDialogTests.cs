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
/// The New / Edit recipe wizard (issue #526; design system components-recipes.md · The dialog, version 1795477868-c9a2):
/// three steps under the path in the header, Next checking only its own step, Create / Save checking all three and
/// opening the first step with a problem, focus on a step's first field, the sortable Ingredients and Method lists with
/// their section rows, the payload's arrays, and the 409 conflict's Reload.
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

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string label) =>
        cut.FindAll(".ods-modal-foot button").Last(b => b.TextContent.Contains(label, StringComparison.Ordinal));

    private static void Click(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string label) =>
        cut.InvokeAsync(() => Button(cut, label).Click()).GetAwaiter().GetResult();

    private static bool HasButton(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string label) =>
        cut.FindAll(".ods-modal-foot button").Any(b => b.TextContent.Contains(label, StringComparison.Ordinal));

    private static AngleSharp.Dom.IElement Field(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string id) =>
        cut.Find($"input#{id}, textarea#{id}, #{id} input, #{id} textarea");

    private static string FieldId(string key) => RecipeDraft.FieldId(key);

    /// <summary>The path's steps: (label, is current, class).</summary>
    private static List<(string Label, bool Current, string Class)> Path(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut) =>
        [.. cut.FindAll("nav.odc-wiz li.odc-wiz-step").Select(li => (
            System.Text.RegularExpressions.Regex.Replace(li.QuerySelector(".odc-wiz-label")!.TextContent.Trim(), @"^Step \d of \d: ", string.Empty),
            li.QuerySelector("[aria-current=step]") is not null,
            li.GetAttribute("class") ?? string.Empty))];

    private static void OpenStep(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, int step) =>
        cut.InvokeAsync(() => cut.FindAll("nav.odc-wiz button.odc-wiz-btn")[step].Click()).GetAwaiter().GetResult();

    private static bool Focused(BunitContext ctx, string id) =>
        ctx.JSInterop.Invocations.Any(i => i.Identifier == "odsFocusFirstIn" && Equals(i.Arguments[0], id));

    [Fact]
    public async Task A_new_recipe_opens_on_details_under_a_three_step_path_with_next_as_the_primary()
    {
        await using var ctx = PageTestContext.Create(PermissionClaims.RecipesCreate);
        var (cut, _, _) = Render(ctx, null);

        cut.WaitForAssertion(() => Assert.Equal(3, Path(cut).Count));
        Assert.Equal(["Details", "Ingredients", "Method"], Path(cut).Select(p => p.Label));
        Assert.True(Path(cut)[0].Current);
        Assert.Equal("Recipe steps", cut.Find("nav.odc-wiz").GetAttribute("aria-label"));
        Assert.Contains("wizard", cut.Find(".mud-dialog").GetAttribute("class"), StringComparison.Ordinal);
        Assert.Contains("soup_kitchen", cut.Find(".ods-modal-lead").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".ods-modal-sub"));
        Assert.Equal("Details", cut.Find("section.rc-wiz-panel").GetAttribute("aria-label"));
        Assert.Empty(cut.FindAll(".rc-wiz-panel .odc-sectiondivider"));
        Assert.True(HasButton(cut, "Next"));
        Assert.False(HasButton(cut, "Back"));
        Assert.False(HasButton(cut, "Create recipe"));
        Assert.Contains("Tags", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Next_checks_only_the_current_step_and_keeps_its_errors_there()
    {
        await using var ctx = PageTestContext.Create(PermissionClaims.RecipesCreate);
        var (cut, api, _) = Render(ctx, null);
        cut.WaitForAssertion(() => Field(cut, FieldId("title")));

        Click(cut, "Next");

        cut.WaitForAssertion(() => Assert.Contains("Give the recipe a title.", cut.Markup, StringComparison.Ordinal));
        Assert.True(Path(cut)[0].Current);
        Assert.Contains("is-error", Path(cut)[0].Class, StringComparison.Ordinal);
        Assert.Contains("Has errors", cut.Find("nav.odc-wiz").TextContent, StringComparison.Ordinal);
        // The empty ingredient and step are not checked yet: those steps are not marked.
        Assert.DoesNotContain("is-error", Path(cut)[1].Class, StringComparison.Ordinal);
        Assert.DoesNotContain("Name the ingredient.", cut.Markup, StringComparison.Ordinal);
        cut.WaitForAssertion(() => Assert.True(Focused(ctx, FieldId("title"))));
        api.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Next_opens_the_next_step_focuses_its_first_field_and_says_where_you_are()
    {
        await using var ctx = PageTestContext.Create(PermissionClaims.RecipesCreate);
        var (cut, _, _) = Render(ctx, null);
        cut.WaitForAssertion(() => Field(cut, FieldId("title")));
        Field(cut, FieldId("title")).Input("Bread");
        // Typing never moves focus.
        Assert.DoesNotContain(ctx.JSInterop.Invocations, i => i.Identifier == "odsFocusFirstIn");

        Click(cut, "Next");

        cut.WaitForAssertion(() => Assert.True(Path(cut)[1].Current));
        Assert.Equal("Ingredients", cut.Find("section.rc-wiz-panel").GetAttribute("aria-label"));
        Assert.Contains("is-done", Path(cut)[0].Class, StringComparison.Ordinal);
        Assert.True(HasButton(cut, "Back"));
        // The first field is the first row's Amount — never its drag handle.
        cut.WaitForAssertion(() => Assert.True(Focused(ctx, FieldId("ingredients[0].amount"))));
        cut.WaitForAssertion(() => Assert.Contains(RecipeDialog.StepAnnouncement(1), cut.Markup, StringComparison.Ordinal));
        Assert.Equal("Step 2 of 3: Ingredients", RecipeDialog.StepAnnouncement(1));

        Click(cut, "Back");
        cut.WaitForAssertion(() => Assert.True(Path(cut)[0].Current));
        Assert.Equal("Bread", Field(cut, FieldId("title")).GetAttribute("value"));
    }

    [Fact]
    public async Task The_ingredient_list_is_a_sortable_list_of_one_line_cards_with_a_column_head_drawn_once()
    {
        await using var ctx = PageTestContext.Create(PermissionClaims.RecipesUpdate);
        var read = Read() with
        {
            Ingredients =
            [
                new() { RecipeIngredientId = Guid.NewGuid(), Position = 0, Name = "flour", Unit = IngredientUnit.Gram, Amount = 500, Section = "Dough" },
                new() { RecipeIngredientId = Guid.NewGuid(), Position = 1, Name = "salt", Unit = IngredientUnit.ToTaste },
            ],
        };
        var (cut, _, _) = Render(ctx, read);
        cut.WaitForAssertion(() => Field(cut, FieldId("title")));

        OpenStep(cut, 1);

        cut.WaitForAssertion(() => cut.Find(".rc-sort-ing"));
        var rows = cut.FindAll(".rc-sort-ing > fieldset.odc-sortable-row");
        Assert.Equal(3, rows.Count);
        Assert.Contains("heading", rows[0].GetAttribute("class"), StringComparison.Ordinal);
        Assert.Equal("Section 1", rows[0].QuerySelector("legend")!.TextContent);
        Assert.Equal("Ingredient 1", rows[1].QuerySelector("legend")!.TextContent);
        Assert.Equal("Dough", Field(cut, FieldId("ingredientsections[0].title")).GetAttribute("value"));
        Assert.Equal("e.g. wheat flour", Field(cut, FieldId("ingredients[0].name")).GetAttribute("placeholder"));
        Assert.Contains("has-headings", cut.Find(".rc-sort-ing").GetAttribute("class"), StringComparison.Ordinal);
        Assert.Equal("true", cut.Find(".rc-sort-ing > .odc-sortable-head").GetAttribute("aria-hidden"));
        Assert.Equal(["", "Amount", "Unit", "Ingredient*", "", ""],
            cut.Find(".rc-sort-ing > .odc-sortable-head").Children.Select(c => c.TextContent.Trim()));
        Assert.NotNull(cut.Find("button[aria-label='Move ingredient 2']"));
        Assert.NotNull(cut.Find("button[aria-label='Actions for section 1']"));
        Assert.Contains("Optional", rows[2].TextContent, StringComparison.Ordinal);
        Assert.Contains("Add ingredient", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Add section", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_checks_every_step_and_opens_the_first_one_with_a_problem()
    {
        await using var ctx = PageTestContext.Create(PermissionClaims.RecipesUpdate);
        var read = Read() with
        {
            Ingredients = [new() { RecipeIngredientId = Guid.NewGuid(), Position = 0, Name = "salt", Unit = IngredientUnit.ToTaste, Amount = 2 }],
        };
        var (cut, api, saved) = Render(ctx, read);
        cut.WaitForAssertion(() => Field(cut, FieldId("title")));
        OpenStep(cut, 2);
        cut.WaitForAssertion(() => Assert.True(HasButton(cut, "Save")));

        Click(cut, "Save");

        cut.WaitForAssertion(() => Assert.True(Path(cut)[1].Current));
        Assert.Contains("Leave the amount empty for “To taste”.", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("is-error", Path(cut)[1].Class, StringComparison.Ordinal);
        Assert.DoesNotContain("is-error", Path(cut)[2].Class, StringComparison.Ordinal);
        cut.WaitForAssertion(() => Assert.True(Focused(ctx, FieldId("ingredients[0].amount"))));
        Assert.Contains("odc-errsum", cut.Markup, StringComparison.Ordinal);
        api.Verify(a => a.UpdateAsync(It.IsAny<Guid>(), It.IsAny<UpdateRecipe>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(saved);
    }

    [Fact]
    public async Task A_new_recipe_submitted_empty_marks_every_step_and_stays_on_the_one_it_is_on()
    {
        await using var ctx = PageTestContext.Create(PermissionClaims.RecipesCreate);
        var (cut, _, _) = Render(ctx, null);
        cut.WaitForAssertion(() => Field(cut, FieldId("title")));
        OpenStep(cut, 2);
        cut.WaitForAssertion(() => Assert.True(HasButton(cut, "Create recipe")));

        Click(cut, "Create recipe");

        cut.WaitForAssertion(() => Assert.All(Path(cut), p => Assert.Contains("is-error", p.Class, StringComparison.Ordinal)));
        Assert.True(Path(cut)[2].Current);
        Assert.Contains("Write the step, or remove it.", cut.Markup, StringComparison.Ordinal);
        cut.WaitForAssertion(() => Assert.True(Focused(ctx, FieldId("steps[0].text"))));
        cut.Find("button.odc-errsum").Click();
        var list = cut.Find(".odc-errsum-list").TextContent;
        Assert.Contains("Details", list, StringComparison.Ordinal);
        Assert.Contains("Ingredient 1", list, StringComparison.Ordinal);
        Assert.Contains("Step 1", list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_section_needs_a_name_and_a_line_under_it()
    {
        await using var ctx = PageTestContext.Create(PermissionClaims.RecipesUpdate);
        var (cut, _, _) = Render(ctx, Read());
        cut.WaitForAssertion(() => Field(cut, FieldId("title")));
        OpenStep(cut, 1);
        cut.WaitForAssertion(() => cut.Find($"#{RecipeDialog.AddRowId("ingredient-section")}"));

        cut.Find($"#{RecipeDialog.AddRowId("ingredient-section")}").Click();

        cut.WaitForAssertion(() => Field(cut, FieldId("ingredientsections[0].title")));
        cut.WaitForAssertion(() => Assert.True(Focused(ctx, FieldId("ingredientsections[0].title"))));
        Click(cut, "Next");
        cut.WaitForAssertion(() => Assert.Contains("Name the section, or remove it.", cut.Markup, StringComparison.Ordinal));

        Field(cut, FieldId("ingredientsections[0].title")).Input("Garnish");
        Click(cut, "Next");
        cut.WaitForAssertion(() => Assert.Contains("Add an ingredient under this section, or remove it.", cut.Markup, StringComparison.Ordinal));
        Assert.True(Path(cut)[1].Current);
    }

    [Fact]
    public async Task Section_rows_save_as_the_section_of_each_line_below_them()
    {
        await using var ctx = PageTestContext.Create(PermissionClaims.RecipesUpdate);
        var read = Read() with
        {
            Steps =
            [
                new() { RecipeStepId = Guid.NewGuid(), Position = 0, Text = "Roast.", Section = "Tray" },
                new() { RecipeStepId = Guid.NewGuid(), Position = 1, Text = "Blend.", Section = "Pot" },
            ],
        };
        var (cut, api, saved) = Render(ctx, read);
        UpdateRecipe? sent = null;
        api.Setup(a => a.UpdateAsync(read.RecipeId, It.IsAny<UpdateRecipe>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, UpdateRecipe body, CancellationToken _) => sent = body)
            .ReturnsAsync(ApiResult<ExistingRecipe>.Success(read with { Version = 4 }, HttpStatusCode.OK));
        cut.WaitForAssertion(() => Field(cut, FieldId("title")));
        OpenStep(cut, 2);
        cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll(".rc-sort-step > fieldset").Count));
        Assert.Equal(["1", "2"], cut.FindAll(".rc-step-disc").Select(d => d.TextContent));

        // Remove the "Pot" heading: Blend joins the section above.
        cut.Find("button[aria-label='Actions for section 2']").Click();
        cut.WaitForAssertion(() => cut.Find("[aria-label='Remove heading section 2']"));
        cut.Find("[aria-label='Remove heading section 2']").Click();
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".rc-sort-step > fieldset").Count));
        Click(cut, "Save");

        cut.WaitForAssertion(() => Assert.NotNull(sent));
        Assert.Equal([("Tray", "Roast."), ("Tray", "Blend.")], sent!.Steps.Select(s => (s.Section, s.Text)));
        cut.WaitForAssertion(() => Assert.Single(saved));
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
        // Every step counts as visited when editing: the path opens Method directly, where Save is.
        OpenStep(cut, 2);
        cut.WaitForAssertion(() => Assert.True(HasButton(cut, "Save")));
        Assert.Contains("is-done", Path(cut)[1].Class, StringComparison.Ordinal);
        Click(cut, "Save");

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
        OpenStep(cut, 2);
        cut.WaitForAssertion(() => Assert.True(HasButton(cut, "Save")));
        Click(cut, "Save");

        cut.WaitForAssertion(() => Assert.Contains("Someone else changed this recipe while you were editing.", cut.Markup, StringComparison.Ordinal));
        Assert.NotNull(cut.Find($"#{RecipeDialog.ReloadId}"));
        Assert.Empty(saved);
    }

    [Fact]
    public async Task A_successful_create_posts_empty_arrays_and_raises_on_saved()
    {
        await using var ctx = PageTestContext.Create(PermissionClaims.RecipesCreate, PermissionClaims.RecipeTagsRead);
        var (cut, api, saved) = Render(ctx, null);
        NewRecipe? sent = null;
        api.Setup(a => a.CreateAsync(It.IsAny<NewRecipe>(), It.IsAny<CancellationToken>()))
            .Callback((NewRecipe body, CancellationToken _) => sent = body)
            .ReturnsAsync(ApiResult<ExistingRecipe>.Success(Read(), HttpStatusCode.Created));
        cut.WaitForAssertion(() => Field(cut, FieldId("title")));

        Field(cut, FieldId("title")).Input("Bread");
        Click(cut, "Next");
        cut.WaitForAssertion(() => Field(cut, FieldId("ingredients[0].name")));
        Field(cut, FieldId("ingredients[0].name")).Input("flour");
        Click(cut, "Next");
        cut.WaitForAssertion(() => Field(cut, FieldId("steps[0].text")));
        Field(cut, FieldId("steps[0].text")).Input("Knead.");
        Click(cut, "Create recipe");

        cut.WaitForAssertion(() => Assert.NotNull(sent));
        Assert.Equal(("Bread", 4, "flour", "Knead."), (sent!.Title, sent.Servings!.Value, sent.Ingredients.Single().Name, sent.Steps.Single().Text));
        Assert.Null(sent.Ingredients.Single().Section);
        Assert.Empty(sent.PhotoIds);
        Assert.Empty(sent.AttachmentFileIds);
        Assert.Empty(sent.TagIds);
        cut.WaitForAssertion(() => Assert.Single(saved));
    }

    [Fact]
    public async Task A_422_lands_on_the_row_fields_opens_their_step_and_fills_the_error_summary()
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
                },
            }));
        cut.WaitForAssertion(() => Assert.Contains("Edit recipe", cut.Markup, StringComparison.Ordinal));
        OpenStep(cut, 2);
        cut.WaitForAssertion(() => Assert.True(HasButton(cut, "Save")));

        Click(cut, "Save");

        cut.WaitForAssertion(() => Assert.True(Path(cut)[1].Current));
        Assert.Contains("That name is too long.", cut.Markup, StringComparison.Ordinal);
        Assert.Equal("true", Field(cut, FieldId("ingredients[0].name")).GetAttribute("aria-invalid"));
        var summary = cut.Find("button.odc-errsum");
        Assert.StartsWith("1 problem", summary.GetAttribute("aria-label"), StringComparison.Ordinal);
        Assert.Empty(saved);
    }

    [Fact]
    public async Task An_error_summary_jump_to_another_step_opens_it_and_focuses_the_field()
    {
        await using var ctx = PageTestContext.Create(PermissionClaims.RecipesCreate);
        var (cut, _, _) = Render(ctx, null);
        cut.WaitForAssertion(() => Field(cut, FieldId("title")));
        OpenStep(cut, 2);
        cut.WaitForAssertion(() => Assert.True(HasButton(cut, "Create recipe")));
        Click(cut, "Create recipe");
        cut.WaitForAssertion(() => cut.Find("button.odc-errsum"));
        cut.Find("button.odc-errsum").Click();

        cut.WaitForAssertion(() => cut.FindAll(".odc-errsum-list button").First(b => b.TextContent.Contains("Ingredient 1", StringComparison.Ordinal)).Click());

        cut.WaitForAssertion(() => Assert.True(Path(cut)[1].Current));
        cut.WaitForAssertion(() => Assert.True(Focused(ctx, FieldId("ingredients[0].name"))));
    }

    [Fact]
    public async Task Removing_the_only_step_focuses_the_add_row_and_announces_it()
    {
        await using var ctx = PageTestContext.Create(PermissionClaims.RecipesUpdate);
        var (cut, _, _) = Render(ctx, Read());
        cut.WaitForAssertion(() => Field(cut, FieldId("title")));
        OpenStep(cut, 2);
        cut.WaitForAssertion(() => cut.Find("button[aria-label='Actions for step 1']"));

        cut.Find("button[aria-label='Actions for step 1']").Click();
        cut.WaitForAssertion(() => cut.Find("[aria-label='Remove step 1']"));
        cut.Find("[aria-label='Remove step 1']").Click();

        cut.WaitForAssertion(() => Assert.Contains("Step 1 removed. 0 steps.", cut.Markup, StringComparison.Ordinal));
        cut.WaitForAssertion(() => Assert.True(Focused(ctx, RecipeDialog.AddRowId("step"))));
    }

    [Fact]
    public async Task At_the_cap_the_add_row_is_aria_disabled_with_its_reason_and_the_path_counts()
    {
        await using var ctx = PageTestContext.Create(PermissionClaims.RecipesUpdate);
        var read = Read() with
        {
            Ingredients = [.. Enumerable.Range(0, RecipeLimits.MaxIngredients).Select(i =>
                new RecipeIngredientDto { RecipeIngredientId = Guid.NewGuid(), Position = i, Name = $"item {i}", Unit = IngredientUnit.None })],
        };
        var (cut, _, _) = Render(ctx, read);
        cut.WaitForAssertion(() => Field(cut, FieldId("title")));

        Assert.Contains($"{RecipeLimits.MaxIngredients} of {RecipeLimits.MaxIngredients}", cut.Find("nav.odc-wiz").TextContent, StringComparison.Ordinal);
        OpenStep(cut, 1);
        cut.WaitForAssertion(() => cut.Find($"#{RecipeDialog.AddRowId("ingredient")}"));
        var add = cut.Find($"#{RecipeDialog.AddRowId("ingredient")}");
        Assert.Equal("true", add.GetAttribute("aria-disabled"));
        Assert.Contains($"A recipe holds at most {RecipeLimits.MaxIngredients} ingredients.", add.TextContent, StringComparison.Ordinal);
        Assert.NotNull(add.GetAttribute("aria-describedby"));
    }

    [Fact]
    public void The_row_announcements_and_caps_say_what_happened()
    {
        Assert.Equal("Step 2 removed. 4 steps.", RecipeDialog.RemovedMessage("step 2", "step", 4));
        Assert.Equal("Section 1 removed. 3 ingredients.", RecipeDialog.RemovedMessage("section 1", "ingredient", 3));
        Assert.Equal($"A recipe holds at most {RecipeLimits.MaxIngredients} ingredients.",
            RecipeDialog.CapMessage("ingredient", RecipeLimits.MaxIngredients, RecipeLimits.MaxIngredients));
        Assert.Null(RecipeDialog.CapMessage("step", RecipeLimits.MaxSteps - 1, RecipeLimits.MaxSteps));
    }
}
