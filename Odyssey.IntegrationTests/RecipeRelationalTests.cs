// EF1002 flags interpolation into raw SQL. Every value interpolated below is a Guid this test generated
// itself; the deletes are issued outside the services on purpose, so the engine resolves the keys.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Core;
using Odyssey.Core.Journal;
using Odyssey.Core.Recipes;
using Odyssey.Dtos.Recipes;
using Odyssey.Testing.FileStorage;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The relational half of the Recipes module (issue #525 AC 10, 12–14, 18, 19): what the EF InMemory tier cannot
/// show — unique indexes during a replace, the foreign keys' on-delete rules, and LIKE escaping.
/// </summary>
[Collection(MariaDbSharedDatabaseCollection.Name)]
public class RecipeRelationalTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_recipes";

    [SkippableFact]
    public async Task The_foreign_keys_carry_the_declared_on_delete_rules()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);
        await using var context = NewContext();

        var rules = await context.Database.SqlQuery<ForeignKeyRule>($"""
                SELECT CONCAT(k.TABLE_NAME, '.', k.COLUMN_NAME) AS ColumnName, r.DELETE_RULE AS DeleteRule
                FROM information_schema.KEY_COLUMN_USAGE k
                JOIN information_schema.REFERENTIAL_CONSTRAINTS r
                  ON r.CONSTRAINT_SCHEMA = k.CONSTRAINT_SCHEMA AND r.CONSTRAINT_NAME = k.CONSTRAINT_NAME
                WHERE k.CONSTRAINT_SCHEMA = DATABASE() AND k.TABLE_NAME LIKE 'Recipe%'
                """)
            .ToDictionaryAsync(row => row.ColumnName, row => row.DeleteRule);

        Assert.Equal("CASCADE", rules["RecipeIngredients.RecipeId"]);
        Assert.Equal("CASCADE", rules["RecipeSteps.RecipeId"]);
        Assert.Equal("CASCADE", rules["RecipePhotos.RecipeId"]);
        Assert.Equal("CASCADE", rules["RecipePhotos.PhotoId"]);
        Assert.Equal("CASCADE", rules["RecipeTagLinks.RecipeId"]);
        Assert.Equal("RESTRICT", rules["RecipeTagLinks.RecipeTagId"]);
        Assert.Equal("SET NULL", rules["Recipes.CreatedByUserId"]);
        Assert.Equal("SET NULL", rules["Recipes.UpdatedByUserId"]);
    }

    [SkippableFact]
    public async Task A_put_that_reorders_everything_and_resends_the_same_links_hits_no_unique_index()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);
        var photoA = await SeedPhotoAsync();
        var photoB = await SeedPhotoAsync();
        var tag = await SeedTagAsync("Reorder");

        ExistingRecipe created;
        await using (var context = NewContext())
        {
            created = (await Service(context).CreateAsync(new NewRecipe
            {
                Title = "Stew",
                Servings = 4,
                Ingredients = [Line("a"), Line("b"), Line("c")],
                Steps = [new() { Text = "one" }, new() { Text = "two" }],
                PhotoIds = [photoA, photoB],
                TagIds = [tag],
            }, userId: null!, canReadPhotos: true, canReadTags: true)).Recipe;
        }

        await using (var context = NewContext())
        {
            var updated = (await Service(context).UpdateAsync(created.RecipeId, new UpdateRecipe
            {
                Title = "Stew",
                Servings = 4,
                Ingredients = [Line("c"), Line("a"), Line("b")],
                Steps = [new() { Text = "two" }, new() { Text = "one" }],
                PhotoIds = [photoB, photoA],
                TagIds = [tag],
                Version = created.Version,
            }, userId: null!, canReadPhotos: true, canReadTags: true))!.Recipe;

            Assert.Equal(["c", "a", "b"], updated.Ingredients.Select(i => i.Name));
            Assert.Equal(["two", "one"], updated.Steps.Select(s => s.Text));
            Assert.Equal([photoB, photoA], updated.Photos!.Select(p => p.PhotoId));
            Assert.Equal([tag], updated.TagIds);
        }
    }

    [SkippableFact]
    public async Task Deleting_a_recipe_cascades_its_rows_and_keeps_photos_and_tags()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);
        var photo = await SeedPhotoAsync();
        var tag = await SeedTagAsync("Keep");
        var recipeId = await CreateAsync(photo, tag);

        await using (var context = NewContext())
        {
            await context.Database.ExecuteSqlRawAsync($"DELETE FROM `Recipes` WHERE `RecipeId` = '{recipeId}'");
        }

        await using var verify = NewContext();
        Assert.False(await verify.RecipeIngredients.AnyAsync(i => i.RecipeId == recipeId));
        Assert.False(await verify.RecipeSteps.AnyAsync(s => s.RecipeId == recipeId));
        Assert.False(await verify.RecipePhotos.AnyAsync(p => p.RecipeId == recipeId));
        Assert.False(await verify.RecipeTagLinks.AnyAsync(l => l.RecipeId == recipeId));
        Assert.True(await verify.Photos.AnyAsync(p => p.PhotoId == photo));
        Assert.True(await verify.RecipeTags.AnyAsync(t => t.RecipeTagId == tag));
    }

    [SkippableFact]
    public async Task Deleting_a_library_photo_removes_its_recipe_links()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);
        var photo = await SeedPhotoAsync();
        var recipeId = await CreateAsync(photo, tag: null);

        await using (var context = NewContext())
        {
            await context.Database.ExecuteSqlRawAsync($"DELETE FROM `Photos` WHERE `PhotoId` = '{photo}'");
        }

        await using var verify = NewContext();
        Assert.False(await verify.RecipePhotos.AnyAsync(p => p.RecipeId == recipeId));
        Assert.True(await verify.Recipes.AnyAsync(r => r.RecipeId == recipeId));
    }

    [SkippableFact]
    public async Task A_linked_tag_cannot_be_deleted_and_the_service_answers_conflict()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);
        var tag = await SeedTagAsync("Linked");
        await CreateAsync(photo: null, tag);

        await using (var context = NewContext())
        {
            await Assert.ThrowsAsync<MySqlConnector.MySqlException>(
                () => context.Database.ExecuteSqlRawAsync($"DELETE FROM `RecipeTags` WHERE `RecipeTagId` = '{tag}'"));
        }

        await using var service = NewContext();
        await Assert.ThrowsAsync<DomainConflictException>(() => new RecipeTagService(service).DeleteAsync(tag));
    }

    [SkippableFact]
    public async Task A_search_for_a_percent_sign_matches_it_literally()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);
        await using (var context = NewContext())
        {
            foreach (var title in new[] { "Bread 50% rye", "Bread 500 g" })
            {
                await Service(context).CreateAsync(
                    new NewRecipe { Title = title, Servings = 1 }, userId: null!, canReadPhotos: false, canReadTags: false);
            }
        }

        await using var read = NewContext();
        var page = await Service(read).ListAsync(new RecipesQueryParams { Search = "50%" }, canReadPhotos: false);

        Assert.Equal(["Bread 50% rye"], page.Items.Select(i => i.Title));
    }

    private async Task<Guid> CreateAsync(Guid? photo, Guid? tag)
    {
        await using var context = NewContext();
        var result = await Service(context).CreateAsync(new NewRecipe
        {
            Title = "Recipe",
            Servings = 2,
            Ingredients = [Line("flour")],
            Steps = [new() { Text = "Mix." }],
            PhotoIds = photo is { } p ? [p] : [],
            TagIds = tag is { } t ? [t] : [],
        }, userId: null!, canReadPhotos: true, canReadTags: true);
        return result.Recipe.RecipeId;
    }

    private static RecipeIngredientRequest Line(string name) => new() { Name = name, Unit = IngredientUnit.Gram, Amount = 100 };

    private static RecipeService Service(OdysseyContext context) =>
        new(context, new PhotoLookup(context), NullLogger<RecipeService>.Instance);

    private async Task<Guid> SeedPhotoAsync()
    {
        await using var context = NewContext();
        var photo = new Photo
        {
            PhotoId = Guid.NewGuid(),
            StoredObjectId = StoredFileFixtures.AddStoredObjectRow(context, featureKey: "PhotoOriginals"),
            FileName = "p.jpg",
            ContentType = "image/jpeg",
            SizeBytes = 3,
            Sha256Hash = new string('a', 64),
        };
        context.Photos.Add(photo);
        await context.SaveChangesAsync();
        return photo.PhotoId;
    }

    private async Task<Guid> SeedTagAsync(string name)
    {
        await using var context = NewContext();
        var tag = new RecipeTag { RecipeTagId = Guid.NewGuid(), Name = $"{name}-{Guid.NewGuid():N}" };
        context.RecipeTags.Add(tag);
        await context.SaveChangesAsync();
        return tag.RecipeTagId;
    }

    private OdysseyContext NewContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);

    private sealed record ForeignKeyRule(string ColumnName, string DeleteRule);
}
