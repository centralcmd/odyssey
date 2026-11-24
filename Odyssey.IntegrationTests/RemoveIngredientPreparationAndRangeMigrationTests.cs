// EF1002 flags interpolation into raw SQL. Every value interpolated below is a Guid, a column name or a constant this
// test generated itself; the rows are written outside the services on purpose, in columns the model no longer has.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Context.Migrations;
using Odyssey.Dtos.Recipes;
using Odyssey.MigrationService;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// <c>RemoveIngredientPreparationAndRange</c> (issue #595) on real MariaDB: before the columns are dropped, a line's
/// preparation is appended to its name and a range is noted after it, the lower amount kept. These are shapes the demo
/// seed never produces (it seeds plain names), so this is where the rewrite is proved.
/// </summary>
/// <remarks>A data migration's seam test: remove it in the first PR after the release containing the migration reaches
/// production, keeping only the schema assertion (see the README).</remarks>
[Collection(MariaDbCollection.Name)]
public class RemoveIngredientPreparationAndRangeMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_ingredient_prep_range";

    [SkippableFact]
    public async Task Preparation_and_range_are_folded_into_the_name_before_the_columns_are_dropped()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateDatabaseAtMigrationAsync(Database, MigrationBaseline.Current.BaselineMigrationId);

        try
        {
            await using var context = NewContext();
            Assert.False(await MigrationSeam.HasRunAsync(context, CoupledMigrationNames.RemoveIngredientPreparationAndRange));

            var recipeId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            await context.Database.ExecuteSqlAsync(
                $"INSERT INTO `Recipes` (`RecipeId`, `Title`, `Servings`, `Version`, `CreatedAt`, `UpdatedAt`) VALUES ({recipeId}, {"Migrated"}, {4}, {1}, {now}, {now})");

            var atLimit = new string('a', RecipeLimits.MaxIngredientNameLength);
            var sifted = await InsertAsync(context, recipeId, "wheat flour", 500m, null, IngredientUnit.Gram, "sifted");
            var blank = await InsertAsync(context, recipeId, "lukewarm water", 3.5m, null, IngredientUnit.Decilitre, "   ");
            var both = await InsertAsync(context, recipeId, "garlic", 4m, 6m, IngredientUnit.Clove, "unpeeled");
            var full = await InsertAsync(context, recipeId, atLimit, 1m, null, IngredientUnit.None, "chopped");
            var toTaste = await InsertAsync(context, recipeId, "black pepper", null, null, IngredientUnit.ToTaste, "freshly ground");
            var pinch = await InsertAsync(context, recipeId, "salt", 1m, null, IngredientUnit.Pinch, null);
            var decimalRange = await InsertAsync(context, recipeId, "chilli flakes", 0.5m, 1.5m, IngredientUnit.Teaspoon, null);
            var plain = await InsertAsync(context, recipeId, "honey", 1m, null, IngredientUnit.Tablespoon, null);

            await MigrationRunner.MigrateAsync(context, CancellationToken.None);

            Assert.True(await MigrationSeam.HasRunAsync(context, CoupledMigrationNames.RemoveIngredientPreparationAndRange));
            Assert.Equal(0L, await MigrationSeam.CountAsync(context,
                "SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
                + "AND TABLE_NAME = 'RecipeIngredients' AND COLUMN_NAME IN ('Preparation', 'AmountMax')"));

            Assert.Equal(("wheat flour, sifted", 500m), await LineAsync(context, sifted));
            Assert.Equal(("lukewarm water", 3.5m), await LineAsync(context, blank));
            Assert.Equal(("garlic, unpeeled (4–6)", 4m), await LineAsync(context, both));
            Assert.Equal(("black pepper, freshly ground", (decimal?)null), await LineAsync(context, toTaste));
            Assert.Equal(("salt", 1m), await LineAsync(context, pinch));
            Assert.Equal(("chilli flakes (0.5–1.5)", 0.5m), await LineAsync(context, decimalRange));
            Assert.Equal(("honey", 1m), await LineAsync(context, plain));

            // A name already at the limit keeps it: cut to 199 of its own characters and the ellipsis.
            var (truncated, _) = await LineAsync(context, full);
            Assert.Equal(RecipeLimits.MaxIngredientNameLength, truncated.Length);
            Assert.Equal(atLimit[..(RecipeLimits.MaxIngredientNameLength - 1)] + "…", truncated);

            // The rows still read through the model, units untouched.
            var units = await context.RecipeIngredients.Where(i => i.RecipeId == recipeId)
                .ToDictionaryAsync(i => i.RecipeIngredientId, i => i.Unit);
            Assert.Equal(IngredientUnit.ToTaste, units[toTaste]);
            Assert.Equal(IngredientUnit.Pinch, units[pinch]);

            // Down restores the columns, empty, and leaves the rewritten names: only a backup undoes the data.
            await MigrationSeam.MigrateToAsync(context, MigrationBaseline.Current.BaselineMigrationId);
            var restored = await MigrationSeam.RowAsync(context,
                $"SELECT `Name`, `Preparation`, `AmountMax` FROM `RecipeIngredients` WHERE `RecipeIngredientId` = '{both}'");
            Assert.NotNull(restored);
            Assert.Equal("garlic, unpeeled (4–6)", restored!["Name"]);
            Assert.Null(restored["Preparation"]);
            Assert.Null(restored["AmountMax"]);
        }
        finally
        {
            await DropAsync();
        }
    }

    private static async Task<Guid> InsertAsync(
        OdysseyContext context, Guid recipeId, string name, decimal? amount, decimal? amountMax, IngredientUnit unit, string? preparation)
    {
        var id = Guid.NewGuid();
        var position = (int)await MigrationSeam.CountAsync(context, $"SELECT COUNT(*) FROM `RecipeIngredients` WHERE `RecipeId` = '{recipeId}'");
        await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO `RecipeIngredients`
                (`RecipeIngredientId`, `RecipeId`, `Position`, `Amount`, `AmountMax`, `Unit`, `Name`, `Preparation`, `Optional`)
            VALUES ({id}, {recipeId}, {position}, {amount}, {amountMax}, {(int)unit}, {name}, {preparation}, {false})
            """);
        return id;
    }

    private static async Task<(string Name, decimal? Amount)> LineAsync(OdysseyContext context, Guid id)
    {
        var row = await MigrationSeam.RowAsync(context,
            $"SELECT `Name`, `Amount` FROM `RecipeIngredients` WHERE `RecipeIngredientId` = '{id}'");
        Assert.NotNull(row);
        // decimal equality ignores scale, so the column's 500.000 equals 500.
        return ((string)row!["Name"]!, row["Amount"] as decimal?);
    }

    private OdysseyContext NewContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);

    private async Task DropAsync()
    {
        await using var server = fixture.CreateOdysseyContext();
        await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
    }
}
