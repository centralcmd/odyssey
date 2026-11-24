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
        draft.Ingredients[0].Name = "flour";
        draft.Steps[0].Text = "Knead.";
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
        Assert.Null(body.Ingredients[1].Section);
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
        draft.Ingredients[0].Amount = 3;
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
        draft.Ingredients[0].Optional = true;
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
        Assert.Equal("80 of 100 ingredients", RecipeDialog.RowsMeta(80, 100, "ingredient", "line"));
        Assert.Equal("2 lines", RecipeDialog.RowsMeta(2, 100, "ingredient", "line"));
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
}
