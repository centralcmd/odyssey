using Microsoft.AspNetCore.Components;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Library;
using Odyssey.Dtos.Recipes;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// A recipe's ⋯ menu, by claim and cooking state (issue #526; design system components-recipes.md · The card head):
/// cooking actions first for every role, then Edit… · Add photos · Attach files · Archive / Restore · Delete…. An action
/// the card cannot take is absent, never dimmed.
/// </summary>
public class RecipesRowActionsTests
{
    private static readonly RecipeSummary Soup = new() { RecipeId = Guid.NewGuid(), Title = "Soup", Servings = 4, UpdatedAt = DateTime.UtcNow };

    private static readonly RecipeRowCallbacks None = new(default, default, default, default, default, default, default, default);

    private static readonly RecipeRowState Fresh = new(Cookable: true, WakeLockSupported: false, Awake: false, Checked: 0, PhotoCount: 0, FileCount: 0);

    private static readonly RecipeRowClaims Owner = new(CanUpdate: true, CanDelete: true, CanReadPhotos: true, CanReadFiles: true);

    private static List<string?> Labels(IReadOnlyList<OdsMenuItem> items) =>
        [.. items.Select(i => i.Divider ? "—" : i.Label)];

    [Fact]
    public void An_owner_gets_the_full_menu_in_the_design_order()
    {
        var items = RecipesPage.BuildRowActions(Soup, Owner, Fresh with { WakeLockSupported = true, Checked = 3 }, None);

        Assert.Equal(
            ["Change servings…", "Keep screen awake", "Clear checks (3)", "—", "Edit…", "Add photos", "Attach files", "Archive", "—", "Delete…"],
            Labels(items));
        Assert.True(items[^1].Danger);
    }

    [Fact]
    public void A_guest_cooks_and_does_nothing_else()
    {
        var items = RecipesPage.BuildRowActions(Soup, new RecipeRowClaims(false, false, false, false), Fresh, None);

        Assert.Equal(["Change servings…"], Labels(items));
    }

    [Fact]
    public void The_wake_lock_items_follow_the_api_and_the_lock()
    {
        Assert.DoesNotContain("Keep screen awake", Labels(RecipesPage.BuildRowActions(Soup, Owner, Fresh, None)));
        Assert.Contains("Let screen sleep",
            Labels(RecipesPage.BuildRowActions(Soup, Owner, Fresh with { WakeLockSupported = true, Awake = true }, None)));
    }

    [Fact]
    public void Photos_and_files_follow_their_read_claims_and_vanish_at_the_cap()
    {
        var noMedia = RecipesPage.BuildRowActions(Soup, Owner with { CanReadPhotos = false, CanReadFiles = false }, Fresh, None);
        Assert.DoesNotContain("Add photos", Labels(noMedia));
        Assert.DoesNotContain("Attach files", Labels(noMedia));

        var full = RecipesPage.BuildRowActions(Soup, Owner,
            Fresh with { PhotoCount = RecipeLimits.MaxPhotos, FileCount = RecipeLimits.MaxAttachments }, None);
        Assert.DoesNotContain("Add photos", Labels(full));
        Assert.DoesNotContain("Attach files", Labels(full));
    }

    [Fact]
    public void An_archived_recipe_offers_restore_and_a_user_without_delete_gets_no_delete()
    {
        var items = RecipesPage.BuildRowActions(Soup with { Archived = DateTime.UtcNow },
            Owner with { CanDelete = false }, Fresh, None);

        Assert.Contains("Restore", Labels(items));
        Assert.DoesNotContain("Archive", Labels(items));
        Assert.DoesNotContain("Delete…", Labels(items));
        Assert.NotEqual("—", Labels(items)[^1]);
    }

    [Fact]
    public void A_failed_or_gone_card_offers_no_cooking_actions_and_no_leading_divider()
    {
        var items = RecipesPage.BuildRowActions(Soup, Owner, Fresh with { Cookable = false, Checked = 2 }, None);

        Assert.Equal("Edit…", items[0].Label);
        Assert.DoesNotContain(items, i => i.Label is "Change servings…" or "Clear checks (2)");
    }

    [Fact]
    public void Every_item_that_acts_on_the_recipe_names_it()
    {
        var items = RecipesPage.BuildRowActions(Soup, Owner, Fresh, None);
        Assert.All(items.Where(i => i.Label is "Edit…" or "Archive" or "Delete…" or "Add photos" or "Attach files"),
            i => Assert.Contains("Soup", i.AriaLabel, StringComparison.Ordinal));
        Assert.All(items.Where(i => i.AriaLabel is not null), i => Assert.StartsWith(i.Label!.TrimEnd('…'), i.AriaLabel!, StringComparison.Ordinal));
    }

    [Fact]
    public void The_card_counts_show_files_only_with_files_read_and_never_at_zero()
    {
        var r = Soup with { IngredientCount = 6, StepCount = 1, AttachmentCount = 2 };

        Assert.Equal(["format_list_bulleted", "format_list_numbered", "attach_file"], RecipesPage.CountsFor(r, canFiles: true).Select(c => c.Icon));
        Assert.Equal("step", RecipesPage.CountsFor(r, true)[1].Label);
        Assert.Equal(2, RecipesPage.CountsFor(r, canFiles: false).Count);
        Assert.Equal(2, RecipesPage.CountsFor(r with { AttachmentCount = 0 }, canFiles: true).Count);
    }

    private static IReadOnlyList<RecipePhotoDto> ThreePhotos() =>
    [
        new() { RecipePhotoId = Guid.NewGuid(), PhotoId = Guid.NewGuid(), Position = 0, Title = "garden_01.jpg" },
        new() { RecipePhotoId = Guid.NewGuid(), PhotoId = Guid.NewGuid(), Position = 1, FileName = "garden_02.jpg" },
        new() { RecipePhotoId = Guid.NewGuid(), PhotoId = Guid.NewGuid(), Position = 2 },
    ];

    [Fact]
    public void A_photo_tiles_menu_omits_the_moves_it_cannot_make()
    {
        var photos = ThreePhotos();
        var receiver = new object();

        Assert.Equal(["Move later", "—", "Detach"], Labels(RecipePhotoGrid.BuildTileActions(photos, 0, default, receiver)));
        Assert.Equal(["Make cover", "Move earlier", "Move later", "—", "Detach"], Labels(RecipePhotoGrid.BuildTileActions(photos, 1, default, receiver)));
        Assert.Equal(["Make cover", "Move earlier", "—", "Detach"], Labels(RecipePhotoGrid.BuildTileActions(photos, 2, default, receiver)));
        Assert.Equal("Photo 3", RecipePhotoGrid.NameOf(photos[2], 2));
    }

    [Fact]
    public async Task Make_cover_moves_the_photo_first_and_says_so()
    {
        var photos = ThreePhotos();
        RecipePhotoChange? change = null;
        var receiver = new object();
        var onChange = EventCallback.Factory.Create<RecipePhotoChange>(receiver, c => change = c);

        await RecipePhotoGrid.BuildTileActions(photos, 1, onChange, receiver).Single(i => i.Label == "Make cover").OnClick.InvokeAsync();

        Assert.Equal([photos[1].PhotoId, photos[0].PhotoId, photos[2].PhotoId], change!.PhotoIds);
        Assert.Equal("garden_02.jpg is now the cover.", change.Message);

        await RecipePhotoGrid.BuildTileActions(photos, 0, onChange, receiver).Single(i => i.Label == "Detach").OnClick.InvokeAsync();
        Assert.Equal([photos[1].PhotoId, photos[2].PhotoId], change.PhotoIds);
        Assert.Equal("Photo detached. garden_02.jpg is now the cover.", change.Message);
    }

    [Fact]
    public void The_photos_added_announcement_counts_what_the_cap_refused()
    {
        Assert.Equal("2 photos added. The first is the cover.", RecipesPage.PhotosAddedMessage(2, 0, wasEmpty: true));
        Assert.Equal($"1 photo added. 3 not added: a recipe holds at most {RecipeLimits.MaxPhotos} photos.",
            RecipesPage.PhotosAddedMessage(1, 3, wasEmpty: false));
    }
}
