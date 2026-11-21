using Microsoft.Extensions.Logging;
using Odyssey.Core.Journal;
using Odyssey.Core.Recipes;
using Odyssey.Dtos.Recipes;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// <see cref="RecipeService"/> called directly, as a non-HTTP caller would: the checks model validation runs first on
/// the HTTP path (§8.1), and what the service logs (§7.7, §7.10; AC 12, 21).
/// </summary>
public class RecipeServiceTests
{
    private const string Secret = "Grandmothers-secret-dumplings";

    public static TheoryData<Action<NewRecipe>, string> BlankFields => new()
    {
        { r => r.Title = "   ", "title" },
        { r => r.Ingredients = [new() { Name = "  ", Unit = IngredientUnit.None }], "ingredients[0].name" },
        { r => r.Steps = [new() { Text = "\t" }], "steps[0].text" },
        { r => r.Ingredients = [new() { Name = "salt", Unit = IngredientUnit.ToTaste, AmountMax = 2 }], "ingredients[0].amount" },
        { r => r.Ingredients = [new() { Name = "egg", Unit = (IngredientUnit)99 }], "ingredients[0].unit" },
        { r => r.Servings = 0, "servings" },
        { r => r.PrepMinutes = -1, "prepMinutes" },
    };

    [Theory]
    [MemberData(nameof(BlankFields))]
    public async Task A_rule_the_annotations_also_hold_is_still_enforced_for_a_direct_caller(Action<NewRecipe> mutate, string key)
    {
        using var context = TestContextFactory.Create();
        var service = new RecipeService(context, new PhotoLookup(context), new ListLogger());
        var request = new NewRecipe { Title = "Soup", Servings = 2 };
        mutate(request);

        var error = await Assert.ThrowsAsync<DomainUnprocessableException>(
            () => service.CreateAsync(request, "u", canReadPhotos: false, canReadTags: false));

        Assert.Equal([key], error.Errors!.Keys);
    }

    [Fact]
    public async Task Strings_are_trimmed_and_an_empty_optional_is_stored_as_null()
    {
        using var context = TestContextFactory.Create();
        var service = new RecipeService(context, new PhotoLookup(context), new ListLogger());

        var created = (await service.CreateAsync(
            new NewRecipe { Title = "  Soup  ", Servings = 2, Notes = "   ", Steps = [new() { Section = " ", Text = " Stir. " }] },
            "u", canReadPhotos: false, canReadTags: false)).Recipe;

        Assert.Equal("Soup", created.Title);
        Assert.Null(created.Notes);
        Assert.Null(created.Steps[0].Section);
        Assert.Equal("Stir.", created.Steps[0].Text);
    }

    [Fact]
    public async Task Delete_logs_the_recipe_and_the_user_and_no_write_ever_logs_its_content()
    {
        using var context = TestContextFactory.Create();
        var logger = new ListLogger();
        var service = new RecipeService(context, new PhotoLookup(context), logger);
        var request = new NewRecipe
        {
            Title = Secret,
            Servings = 2,
            Notes = Secret,
            Source = Secret,
            Ingredients = [new() { Name = Secret, Unit = IngredientUnit.None }],
        };

        var created = (await service.CreateAsync(request, "cook-1", canReadPhotos: false, canReadTags: false)).Recipe;
        await service.UpdateAsync(created.RecipeId, new UpdateRecipe
        {
            Title = Secret,
            Servings = 3,
            Notes = Secret,
            Source = Secret,
            Version = 1,
            Ingredients = [new() { Name = Secret, Unit = IngredientUnit.None }],
        }, "cook-1", canReadPhotos: false, canReadTags: false);
        Assert.True(await service.DeleteAsync(created.RecipeId, "cook-2"));

        var line = Assert.Single(logger.Lines, l => l.Level == LogLevel.Information);
        Assert.Contains(created.RecipeId.ToString(), line.Message, StringComparison.Ordinal);
        Assert.Contains("cook-2", line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Lines, l => l.Message.Contains(Secret, StringComparison.Ordinal));
    }

    private sealed class ListLogger : ILogger<RecipeService>
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add((logLevel, formatter(state, exception)));
    }
}
