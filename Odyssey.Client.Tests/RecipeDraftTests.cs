using Odyssey.Client.Pages.Library;
using Odyssey.Dtos.Recipes;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The New / Edit recipe payloads (issue #526; design system components-recipes.md · Arrays sent) and the rules the
/// dialog mirrors from the annotations. Photos and files are never edited in the dialog: <c>[]</c> on POST, <c>null</c>
/// (unchanged) on PUT. Tags follow the claim at open.
/// </summary>
public class RecipeDraftTests
{
    private static readonly Guid TagA = Guid.NewGuid();
    private static readonly Guid TagB = Guid.NewGuid();

    private static ExistingRecipe Read() => new()
    {
        RecipeId = Guid.NewGuid(),
        Version = 7,
        Title = "Soup",
        Servings = 4,
        DisplayedServings = 4,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
        Ingredients =
        [
            new() { RecipeIngredientId = Guid.NewGuid(), Position = 1, Name = "salt", Unit = IngredientUnit.ToTaste },
            new() { RecipeIngredientId = Guid.NewGuid(), Position = 0, Name = "tomatoes", Amount = 1.2m, Unit = IngredientUnit.Kilogram, Section = "Tray" },
        ],
        Steps = [new() { RecipeStepId = Guid.NewGuid(), Position = 0, Text = "Roast." }],
        Photos = [new() { RecipePhotoId = Guid.NewGuid(), PhotoId = Guid.NewGuid(), Position = 0 }],
        TagIds = [TagA],
        Archived = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void A_new_recipe_posts_empty_photo_file_and_tag_arrays_without_the_tag_claim()
    {
        var draft = RecipeDraft.New();
        draft.Title = "  Bread  ";
        ((RecipeIngredientDraft)draft.Ingredients[0]).Name = "flour";
        ((RecipeStepDraft)draft.Method[0]).Text = "Knead.";
        draft.TagIds = [TagA.ToString()];

        var body = draft.ToNew(canReadTags: false);

        Assert.Equal("Bread", body.Title);
        Assert.Empty(body.PhotoIds);
        Assert.Empty(body.AttachmentFileIds);
        Assert.Empty(body.TagIds);
        Assert.Equal([TagA], draft.ToNew(canReadTags: true).TagIds);
    }

    [Fact]
    public void An_edit_puts_null_photos_and_files_the_read_version_and_archived_state()
    {
        var read = Read();
        var draft = RecipeDraft.From(read);

        var body = draft.ToUpdate(read, canReadTags: true);

        Assert.Null(body.PhotoIds);
        Assert.Null(body.AttachmentFileIds);
        Assert.Equal(7, body.Version);
        Assert.True(body.Archived);
        Assert.Equal(["tomatoes", "salt"], body.Ingredients.Select(i => i.Name));
        Assert.Equal("Tray", body.Ingredients[0].Section);
        // A stored line with no section after a sectioned one joins the section above on edit (it cannot be shown
        // otherwise) — visible in the dialog before saving.
        Assert.Equal("Tray", body.Ingredients[1].Section);
    }

    [Fact]
    public void Without_the_tag_claim_an_edit_sends_the_reads_tags_back_unchanged()
    {
        // This API treats a null tagIds as an empty replacement, so null would clear the tags the caller cannot see.
        var read = Read();
        var draft = RecipeDraft.From(read);
        draft.TagIds = [TagB.ToString()];

        Assert.Equal([TagA], draft.ToUpdate(read, canReadTags: false).TagIds);
        Assert.Equal([TagB], draft.ToUpdate(read, canReadTags: true).TagIds);
    }

    [Fact]
    public void A_record_action_changes_only_its_own_array()
    {
        var read = Read();
        var photo = Guid.NewGuid();

        var photos = RecipeWrites.From(read, photoIds: [photo]);
        Assert.Equal([photo], photos.PhotoIds!);
        Assert.Null(photos.AttachmentFileIds);
        Assert.Equal([TagA], photos.TagIds);
        Assert.True(photos.Archived);

        var restore = RecipeWrites.From(read, archived: false);
        Assert.False(restore.Archived);
        Assert.Null(restore.PhotoIds);
        Assert.Equal(7, restore.Version);
        Assert.Equal(["tomatoes", "salt"], restore.Ingredients.Select(i => i.Name));
    }

    [Fact]
    public void Validation_mirrors_the_annotations_and_keys_errors_like_the_server()
    {
        var draft = RecipeDraft.New();
        draft.Servings = RecipeLimits.MaxServings + 1;
        draft.PrepMinutes = RecipeLimits.MaxMinutes + 1;
        ((RecipeIngredientDraft)draft.Ingredients[0]).Amount = 3;
        draft.Ingredients.Add(new RecipeIngredientDraft { Name = "salt", Unit = IngredientUnit.ToTaste, Amount = 1 });

        var keys = draft.Validate().Select(e => e.Key).ToList();

        Assert.Equal(
            ["title", "servings", "prepminutes", "ingredients[0].name", "ingredients[1].amount", "steps[0].text"],
            keys);
        Assert.Contains(draft.Validate(), e => e.Value == $"Enter a number from 1 to {RecipeLimits.MaxServings}.");
    }

    [Fact]
    public void A_valid_draft_passes_and_its_fingerprint_tracks_changes()
    {
        var draft = RecipeDraft.From(Read());
        Assert.Empty(draft.Validate());

        var before = draft.Fingerprint();
        ((RecipeIngredientDraft)draft.Ingredients[1]).Optional = true;
        Assert.NotEqual(before, draft.Fingerprint());
    }

    [Theory]
    [InlineData("Ingredients[2].Name", "ingredients[2].name")]
    [InlineData("$.steps[0].text", "steps[0].text")]
    [InlineData("Title", "title")]
    public void A_server_key_lands_on_the_dialogs_field(string server, string key)
    {
        Assert.Equal(key, RecipeDialog.NormalizeKey(server));
        Assert.Equal(RecipeDraft.FieldId(key), RecipeDraft.FieldId(server.TrimStart('$', '.')));
    }

    [Fact]
    public void A_row_error_is_listed_under_its_row()
    {
        var problem = RecipeDialog.ProblemFor("ingredients[2].name", "Name the ingredient.");
        Assert.Equal(("Name the ingredient.", "Ingredient 3", "rc-f-ingredients-2-name"), (problem.Label, problem.Section, problem.TargetId));
        Assert.Equal("Title — Give the recipe a title.", RecipeDialog.ProblemFor("title", "Give the recipe a title.").Label);
        Assert.Equal("80 of 100", RecipeDialog.CapMeta(80, 100));
        Assert.Null(RecipeDialog.CapMeta(79, 100));
        Assert.Equal(("Name the section, or remove it.", "Section 2"),
            (RecipeDialog.ProblemFor("stepsections[1].title", "Name the section, or remove it.").Label,
             RecipeDialog.ProblemFor("stepsections[1].title", "Name the section, or remove it.").Section));
    }

    [Fact]
    public void The_servings_dialog_refuses_out_of_range_numbers()
    {
        Assert.Null(RecipeServingsDialog.Check(8));
        Assert.Null(RecipeServingsDialog.Check(RecipeLimits.MaxScaleServings));
        Assert.Equal($"Enter a whole number from 1 to {RecipeLimits.MaxScaleServings}.", RecipeServingsDialog.Check(0));
        Assert.NotNull(RecipeServingsDialog.Check(null));
    }

    [Fact]
    public void The_filter_bar_maps_onto_the_list_query_and_drops_filters_without_their_claim()
    {
        var tag = Guid.NewGuid();
        var filter = RecipeFilters.Build(" soup ", [tag.ToString()], "60", ["has"], ["none"], ["Archived"],
            new Odyssey.Client.Components.OdsTableSort("total", Odyssey.Client.Components.OdsSortDirection.Desc), true, true, true);

        Assert.Equal(("soup", 60, (bool?)true, (bool?)false, (Odyssey.Dtos.ArchivalStatus?)Odyssey.Dtos.ArchivalStatus.Archived),
            (filter.Search, filter.MaxTotalMinutes, filter.HasPhotos, filter.HasAttachments, filter.Status));
        Assert.Equal([tag], filter.TagIds);
        Assert.Equal((RecipeSortBy.TotalMinutes, Odyssey.Dtos.SortDirection.Desc), (filter.SortBy!.Value, filter.SortDir!.Value));

        var guest = RecipeFilters.Build(null, [tag.ToString()], RecipeFilters.AnyTime, ["has"], ["has"], [],
            new Odyssey.Client.Components.OdsTableSort("title", Odyssey.Client.Components.OdsSortDirection.Asc), false, false, false);
        Assert.Equal(((IReadOnlyCollection<Guid>?)null, (int?)null, (bool?)null, (bool?)null), (guest.TagIds, guest.MaxTotalMinutes, guest.HasPhotos, guest.HasAttachments));
        Assert.Equal(["none"], RecipeFilters.OneAtATime(["has"], ["has", "none"]));
    }

    private static RecipeIngredientDto Line(int position, string name, string? section) =>
        new() { RecipeIngredientId = Guid.NewGuid(), Position = position, Name = name, Unit = IngredientUnit.None, Section = section };

    [Fact]
    public void Sections_become_their_own_rows_and_fold_back_onto_each_line()
    {
        var read = Read() with
        {
            Ingredients = [Line(0, "flour", "Dough"), Line(1, "water", "Dough"), Line(2, "cheese", "Topping"), Line(3, "basil", null)],
            Steps =
            [
                new() { RecipeStepId = Guid.NewGuid(), Position = 0, Text = "Preheat." },
                new() { RecipeStepId = Guid.NewGuid(), Position = 1, Text = "Knead.", Section = "Dough" },
            ],
        };

        var draft = RecipeDraft.From(read);

        // A heading wherever the section changes to a new non-empty value; a line with none after a sectioned one
        // stays under the heading above (the kit's rcToGrouped).
        Assert.Equal(["#Dough", "flour", "water", "#Topping", "cheese", "basil"], draft.Ingredients.Select(Print));
        Assert.Equal(["Preheat.", "#Dough", "Knead."], draft.Method.Select(Print));
        Assert.Equal(["section 1", "ingredient 1", "ingredient 2", "section 2", "ingredient 3", "ingredient 4"],
            draft.Ingredients.Select((_, i) => RecipeDraft.LabelOf(draft.Ingredients, i, "ingredient")));
        Assert.Equal(4, RecipeDraft.LineCount(draft.Ingredients));

        var body = draft.ToUpdate(read, canReadTags: true);
        Assert.Equal([("Dough", "flour"), ("Dough", "water"), ("Topping", "cheese"), ("Topping", "basil")],
            body.Ingredients.Select(x => (x.Section, x.Name)));
        Assert.Equal([(null, "Preheat."), ("Dough", "Knead.")], body.Steps.Select(x => (x.Section, x.Text)));
    }

    [Fact]
    public void Moving_or_removing_a_heading_only_regroups_its_lines()
    {
        var read = Read() with { Ingredients = [Line(0, "flour", "Dough"), Line(1, "cheese", "Topping"), Line(2, "basil", "Topping")] };
        var draft = RecipeDraft.From(read);

        // Move "Topping" above "cheese"'s neighbour: the heading moves alone; cheese now sits under Dough.
        var topping = draft.Ingredients[2];
        draft.Ingredients.RemoveAt(2);
        draft.Ingredients.Insert(3, topping);
        Assert.Equal([("Dough", "flour"), ("Dough", "cheese"), ("Topping", "basil")],
            draft.ToNew(true).Ingredients.Select(x => (x.Section, x.Name)));

        // Removing the first heading keeps its lines: with no heading above they have no section.
        draft.Ingredients.RemoveAt(0);
        Assert.Equal([(null, "flour"), (null, "cheese"), ("Topping", "basil")],
            draft.ToNew(true).Ingredients.Select(x => (x.Section, x.Name)));
    }

    [Fact]
    public void A_heading_needs_a_name_and_a_line_under_it_and_each_step_checks_only_its_own_rows()
    {
        var draft = RecipeDraft.New();
        ((RecipeIngredientDraft)draft.Ingredients[0]).Name = "flour";
        draft.Ingredients.Insert(0, new RecipeSectionDraft());
        draft.Ingredients.Add(new RecipeSectionDraft { Title = "Topping" });
        ((RecipeStepDraft)draft.Method[0]).Text = "Bake.";
        draft.Method.Insert(0, new RecipeSectionDraft { Title = "Oven" });
        draft.Method.Insert(0, new RecipeSectionDraft { Title = "Prep" });

        var ingredients = draft.Validate(RecipeDraft.IngredientsStep);
        Assert.Equal(
            [
                new("ingredientsections[0].title", "Name the section, or remove it."),
                new KeyValuePair<string, string>("ingredientsections[1].title", "Add an ingredient under this section, or remove it."),
            ],
            ingredients);
        Assert.Equal([new KeyValuePair<string, string>("stepsections[0].title", "Add a step under this section, or remove it.")],
            draft.Validate(RecipeDraft.MethodStep));
        Assert.Equal(["title"], draft.Validate(RecipeDraft.DetailsStep).Select(e => e.Key));
        Assert.All(ingredients, e => Assert.Equal(RecipeDraft.IngredientsStep, RecipeDraft.StepOf(e.Key)));
        Assert.Equal(RecipeDraft.MethodStep, RecipeDraft.StepOf(RecipeDraft.FieldId("steps[0].text")));
        Assert.Equal(RecipeDraft.DetailsStep, RecipeDraft.StepOf("servingslabel"));
        Assert.Equal(RecipeDraft.DetailsStep, RecipeDraft.StepOf("tagids"));
    }

    [Fact]
    public void To_taste_takes_no_amount()
    {
        var draft = RecipeDraft.New();
        draft.Title = "Soup";
        draft.Ingredients = [new RecipeIngredientDraft { Name = "salt", Unit = IngredientUnit.ToTaste, Amount = 2 }];
        ((RecipeStepDraft)draft.Method[0]).Text = "Season.";

        Assert.Equal([new KeyValuePair<string, string>("ingredients[0].amount", "Leave the amount empty for “To taste”.")],
            draft.Validate());

        ((RecipeIngredientDraft)draft.Ingredients[0]).Amount = null;
        Assert.Empty(draft.Validate());
    }

    [Fact]
    public void A_server_section_error_on_a_line_lands_on_the_heading_above_it()
    {
        var draft = RecipeDraft.From(Read() with { Ingredients = [Line(0, "flour", "Dough"), Line(1, "cheese", "Topping")] });

        Assert.Equal("ingredientsections[1].title", RecipeDialog.DraftKey(draft, "Ingredients[1].Section"));
        Assert.Equal("ingredients[1].name", RecipeDialog.DraftKey(draft, "Ingredients[1].Name"));
    }

    private static string Print(RecipeRowDraft row) => row switch
    {
        RecipeSectionDraft h => $"#{h.Title}",
        RecipeIngredientDraft i => i.Name,
        RecipeStepDraft s => s.Text,
        _ => "?",
    };
}
