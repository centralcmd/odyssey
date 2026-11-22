// EF1002 flags interpolation into raw SQL. Every value interpolated below is a Guid this test generated
// itself; the deletes are issued outside the services on purpose, so the engine resolves the keys.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Core;
using Odyssey.Core.FileStorage.Files;
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
        Assert.Equal("CASCADE", rules["RecipeAttachments.RecipeId"]);
        Assert.Equal("CASCADE", rules["RecipeAttachments.FileId"]);
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
            }, userId: null!, canReadPhotos: true, canReadTags: true, canReadFiles: true)).Recipe;
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
            }, userId: null!, canReadPhotos: true, canReadTags: true, canReadFiles: true))!.Recipe;

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
    public async Task Deleting_a_files_store_file_removes_its_recipe_links_and_deleting_a_recipe_keeps_the_file()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);
        var gone = await SeedFileAsync(StoredFileArea.Library);
        var kept = await SeedFileAsync(StoredFileArea.Finance);
        Guid recipeId;
        await using (var context = NewContext())
        {
            recipeId = (await Service(context).CreateAsync(
                new NewRecipe { Title = "With files", Servings = 1, AttachmentFileIds = [gone, kept] },
                userId: null!, canReadPhotos: false, canReadTags: false, canReadFiles: true)).Recipe.RecipeId;
        }

        await using (var context = NewContext())
        {
            await context.Database.ExecuteSqlRawAsync($"DELETE FROM `FileMetadata` WHERE `Id` = '{gone}'");
        }

        await using (var verify = NewContext())
        {
            Assert.Equal([kept], await verify.RecipeAttachments.Where(a => a.RecipeId == recipeId).Select(a => a.FileId).ToListAsync());
            await verify.Database.ExecuteSqlRawAsync($"DELETE FROM `Recipes` WHERE `RecipeId` = '{recipeId}'");
        }

        await using var after = NewContext();
        Assert.False(await after.RecipeAttachments.AnyAsync(a => a.RecipeId == recipeId));
        Assert.True(await after.FileMetadata.AnyAsync(f => f.Id == kept));
    }

    [SkippableFact]
    public async Task A_library_file_satisfies_the_one_source_check_and_two_sources_do_not()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);
        var library = await SeedFileAsync(StoredFileArea.Library);

        await using var context = NewContext();
        var row = await context.FileMetadata.AsNoTracking().SingleAsync(f => f.Id == library);
        Assert.NotNull(row.LibraryStoredObjectId);
        Assert.Null(row.FinanceStoredObjectId);

        var second = StoredFileFixtures.AddStoredObjectRow(context, featureKey: "FinanceFiles");
        await context.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<MySqlConnector.MySqlException>(() => context.Database.ExecuteSqlRawAsync(
            $"UPDATE `FileMetadata` SET `FinanceStoredObjectId` = '{second}' WHERE `Id` = '{library}'"));
        Assert.Contains("CK_FileMetadata_ExactlyOneSource", error.Message, StringComparison.Ordinal);
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
                    new NewRecipe { Title = title, Servings = 1 }, userId: null!, canReadPhotos: false, canReadTags: false, canReadFiles: false);
            }
        }

        await using var read = NewContext();
        var page = await Service(read).ListAsync(new RecipesQueryParams { Search = "50%" }, canReadPhotos: false, canReadFiles: false);

        Assert.Equal(["Bread 50% rye"], page.Items.Select(i => i.Title));
    }

    // ── §9 race and failure paths (the InMemory tier enforces no key, no unique index and no token) ──

    [SkippableFact]
    public async Task A_save_that_loses_the_version_race_maps_to_the_version_conflict()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);
        var recipeId = await CreateAsync(photo: null, tag: null);

        // The interceptor bumps the stored version between the service's check and its save: exactly a concurrent PUT.
        await using var context = NewContext(new BeforeSave(() => BumpVersionAsync(recipeId)));
        await Assert.ThrowsAsync<RecipeVersionConflictException>(() =>
            Service(context).UpdateAsync(recipeId, Update("Late"), userId: null!, canReadPhotos: true, canReadTags: true, canReadFiles: true));
    }

    [SkippableFact]
    public async Task A_delete_that_loses_to_an_edit_is_a_conflict_and_one_that_loses_to_a_delete_is_not_found()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);
        var edited = await CreateAsync(photo: null, tag: null);
        var deleted = await CreateAsync(photo: null, tag: null);

        await using (var context = NewContext(new BeforeSave(() => BumpVersionAsync(edited))))
        {
            await Assert.ThrowsAsync<RecipeVersionConflictException>(() => Service(context).DeleteAsync(edited, "u"));
        }

        await using (var context = NewContext(new BeforeSave(() => DeleteRowAsync(deleted))))
        {
            Assert.False(await Service(context).DeleteAsync(deleted, "u"));
        }

        await using var verify = NewContext();
        Assert.True(await verify.Recipes.AnyAsync(r => r.RecipeId == edited));
    }

    [SkippableFact]
    public async Task A_photo_deleted_after_validation_is_unprocessable_on_photo_ids()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);

        // A lookup that vouches for any id stands in for a photo deleted between the check and the save (1452).
        await using var context = NewContext();
        var service = new RecipeService(context, new EveryPhotoExists(), NullLogger<RecipeService>.Instance);
        var error = await Assert.ThrowsAsync<DomainUnprocessableException>(() => service.CreateAsync(
            new NewRecipe { Title = "Ghost", Servings = 1, PhotoIds = [Guid.NewGuid()] },
            userId: null!, canReadPhotos: true, canReadTags: true, canReadFiles: true));

        Assert.Equal(["photoIds"], error.Errors!.Keys);
    }

    [SkippableFact]
    public async Task A_file_deleted_after_validation_is_unprocessable_on_attachment_file_ids()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);
        var file = await SeedFileAsync(StoredFileArea.Library);

        // The file passes EnsureFilesLinkable, then disappears before the save (1452 on RecipeAttachments.FileId).
        await using var context = NewContext(new BeforeSave(async () =>
        {
            await using var other = NewContext();
            await other.Database.ExecuteSqlRawAsync($"DELETE FROM `FileMetadata` WHERE `Id` = '{file}'");
        }));
        var error = await Assert.ThrowsAsync<DomainUnprocessableException>(() => Service(context).CreateAsync(
            new NewRecipe { Title = "Ghost file", Servings = 1, AttachmentFileIds = [file] },
            userId: null!, canReadPhotos: false, canReadTags: false, canReadFiles: true));

        Assert.Equal(["attachmentFileIds"], error.Errors!.Keys);
    }

    [SkippableFact]
    public async Task The_library_column_and_the_attachment_pair_carry_their_keys_and_unique_indexes()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);
        await using var context = NewContext();

        var rule = await context.Database.SqlQuery<ForeignKeyRule>($"""
                SELECT CONCAT(k.TABLE_NAME, '.', k.COLUMN_NAME) AS ColumnName, r.DELETE_RULE AS DeleteRule
                FROM information_schema.KEY_COLUMN_USAGE k
                JOIN information_schema.REFERENTIAL_CONSTRAINTS r
                  ON r.CONSTRAINT_SCHEMA = k.CONSTRAINT_SCHEMA AND r.CONSTRAINT_NAME = k.CONSTRAINT_NAME
                WHERE k.CONSTRAINT_SCHEMA = DATABASE() AND k.TABLE_NAME = 'FileMetadata' AND k.COLUMN_NAME = 'LibraryStoredObjectId'
                """).SingleAsync();
        Assert.Equal("RESTRICT", rule.DeleteRule);

        var unique = await context.Database.SqlQuery<UniqueIndexColumns>($"""
                SELECT CONCAT(TABLE_NAME, ':', GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX SEPARATOR ',')) AS Columns
                FROM information_schema.STATISTICS
                WHERE TABLE_SCHEMA = DATABASE() AND NON_UNIQUE = 0 AND INDEX_NAME <> 'PRIMARY'
                  AND TABLE_NAME IN ('FileMetadata', 'RecipeAttachments')
                GROUP BY TABLE_NAME, INDEX_NAME
                """).Select(row => row.Columns).ToListAsync();
        Assert.Contains("FileMetadata:LibraryStoredObjectId", unique);
        Assert.Contains("RecipeAttachments:RecipeId,FileId", unique);
    }

    [SkippableFact]
    public async Task A_file_with_no_source_fails_the_one_source_check()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);
        var library = await SeedFileAsync(StoredFileArea.Library);

        await using var context = NewContext();
        var error = await Assert.ThrowsAsync<MySqlConnector.MySqlException>(() => context.Database.ExecuteSqlRawAsync(
            $"UPDATE `FileMetadata` SET `LibraryStoredObjectId` = NULL WHERE `Id` = '{library}'"));
        Assert.Contains("CK_FileMetadata_ExactlyOneSource", error.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task A_tag_linked_between_the_check_and_the_delete_is_still_a_conflict()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);
        var tag = await SeedTagAsync("Racing");
        var recipeId = await CreateAsync(photo: null, tag: null);

        // The link lands after the in-use check and before the DELETE, so only the RESTRICT key (1451) refuses it.
        await using var context = NewContext(new BeforeSave(async () =>
        {
            await using var other = NewContext();
            await other.Database.ExecuteSqlRawAsync(
                $"INSERT INTO `RecipeTagLinks` (`RecipeTagLinkId`, `RecipeId`, `RecipeTagId`) VALUES ('{Guid.NewGuid()}', '{recipeId}', '{tag}')");
        }));

        await Assert.ThrowsAsync<DomainConflictException>(() => new RecipeTagService(context).DeleteAsync(tag));
    }

    [SkippableFact]
    public async Task A_tag_name_taken_in_another_case_by_a_racing_create_is_a_conflict()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);
        var name = $"Brunch{Guid.NewGuid():N}";

        // The racing row differs only in case: the *_ci collation's unique index (1062) is what refuses it.
        await using var context = NewContext(new BeforeSave(async () =>
        {
            await using var other = NewContext();
            other.RecipeTags.Add(new RecipeTag { RecipeTagId = Guid.NewGuid(), Name = name.ToUpperInvariant() });
            await other.SaveChangesAsync();
        }));

        await Assert.ThrowsAsync<DomainConflictException>(() => new RecipeTagService(context).CreateAsync(new NewRecipeTag { Name = name }));
    }

    [SkippableFact]
    public async Task Deleting_the_author_nulls_the_attribution_and_keeps_the_recipe()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);
        var userId = $"cook-{Guid.NewGuid():N}";
        Guid recipeId;
        await using (var context = NewContext())
        {
            await AttributionUsers.EnsureAsync(context, userId);
            recipeId = (await Service(context).CreateAsync(
                new NewRecipe { Title = "Kept", Servings = 1 }, userId, canReadPhotos: false, canReadTags: false, canReadFiles: false)).Recipe.RecipeId;
            await context.Database.ExecuteSqlRawAsync($"DELETE FROM `AspNetUsers` WHERE `Id` = '{userId}'");
        }

        await using var verify = NewContext();
        var recipe = await verify.Recipes.AsNoTracking().SingleAsync(r => r.RecipeId == recipeId);
        Assert.Null(recipe.CreatedByUserId);
        Assert.Null(recipe.UpdatedByUserId);
    }

    private static UpdateRecipe Update(string title, int version = 1) => new()
    {
        Title = title,
        Servings = 2,
        Ingredients = [Line("flour")],
        Steps = [new() { Text = "Mix." }],
        Version = version,
    };

    private async Task BumpVersionAsync(Guid recipeId)
    {
        await using var other = NewContext();
        await other.Database.ExecuteSqlRawAsync($"UPDATE `Recipes` SET `Version` = `Version` + 1 WHERE `RecipeId` = '{recipeId}'");
    }

    private async Task DeleteRowAsync(Guid recipeId)
    {
        await using var other = NewContext();
        await other.Database.ExecuteSqlRawAsync($"DELETE FROM `Recipes` WHERE `RecipeId` = '{recipeId}'");
    }

    /// <summary>Runs <paramref name="race"/> once, just before the context's first save.</summary>
    private sealed class BeforeSave(Func<Task> race) : SaveChangesInterceptor
    {
        private bool done;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!done)
            {
                done = true;
                await race();
            }

            return result;
        }
    }

    private sealed class EveryPhotoExists : IPhotoLookup
    {
        public Task<IReadOnlySet<Guid>> ExistingIdsAsync(IReadOnlyCollection<Guid> photoIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<Guid>>(photoIds.ToHashSet());

        public Task<IReadOnlyDictionary<Guid, PhotoLinkInfo>> ResolveAsync(IReadOnlyCollection<Guid> photoIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, PhotoLinkInfo>>(new Dictionary<Guid, PhotoLinkInfo>());

        public Task<IReadOnlyDictionary<Guid, Guid>> ResolveReferencesAsync(IReadOnlyCollection<Guid> referenceIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, Guid>>(new Dictionary<Guid, Guid>());
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
        }, userId: null!, canReadPhotos: true, canReadTags: true, canReadFiles: true);
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

    /// <summary>A Files-store row whose stored object exists (the key is real here), in <paramref name="area"/>.</summary>
    private async Task<Guid> SeedFileAsync(StoredFileArea area)
    {
        await using var context = NewContext();
        var file = StoredFileFixtures.Row(area: area);
        StoredFileAreas.Assign(file, area, StoredFileFixtures.AddStoredObjectRow(context, featureKey: StoredFileAreas.FeatureKey(area)));
        context.FileMetadata.Add(file);
        await context.SaveChangesAsync();
        return file.Id;
    }

    private async Task<Guid> SeedTagAsync(string name)
    {
        await using var context = NewContext();
        var tag = new RecipeTag { RecipeTagId = Guid.NewGuid(), Name = $"{name}-{Guid.NewGuid():N}" };
        context.RecipeTags.Add(tag);
        await context.SaveChangesAsync();
        return tag.RecipeTagId;
    }

    private OdysseyContext NewContext(IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString));
        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }

        return new OdysseyContext(builder.Options);
    }

    private sealed record ForeignKeyRule(string ColumnName, string DeleteRule);

    private sealed record UniqueIndexColumns(string Columns);
}
