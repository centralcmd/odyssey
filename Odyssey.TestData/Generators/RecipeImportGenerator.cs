using System.Text.Json;
using Odyssey.Context;
using Odyssey.Dtos.Recipes;

namespace Odyssey.TestData.Generators;

/// <summary>
/// The AI recipe import demo set (issue #602 §14 step 5): one finished import whose draft recipe waits for review and
/// one whose draft became the demo wheat loaf. Text sources only, so nothing waits on <c>DemoFileSeeder</c>, and
/// nothing is ever sent — the rows are what a finished import leaves behind.
/// </summary>
public static class RecipeImportGenerator
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record Result(IReadOnlyList<RecipeImport> Imports);

    public static Guid IdFor(string key) => DeterministicGuid.From($"recipe-import::{key}");

    public static Result Generate(DateTime anchor)
    {
        var owner = DemoUsers.All.First(user => user.Role == "Owner").Id;
        var user = DemoUsers.All.First(u => u.Role == "User").Id;

        var pending = Import("cardamom-buns", owner, anchor.AddHours(-3),
            "Mormor's cardamom buns\n500 g wheat flour\n3 dl milk\n50 g fresh yeast\n2 tsp ground cardamom\n"
            + "Filling: 150 g soft butter, 1 dl sugar\nWarm the milk to 37 °C, dissolve the yeast, work in the flour. "
            + "Rise 45 min, roll out, fill, roll up, cut and bake at 225 °C for 10–12 min.");
        pending.Drafts.Add(new RecipeImportDraft
        {
            RecipeImportDraftId = DeterministicGuid.From("recipe-import-draft::cardamom-buns"),
            Ordinal = 0,
            Status = RecipeImportDraftStatus.Pending,
            Title = "Cardamom buns",
            DraftJson = JsonSerializer.Serialize(new RecipeImportDraftContent
            {
                Title = "Cardamom buns",
                Description = "Soft, cardamom-scented buns with a butter-sugar filling.",
                Source = "Mormor's recipe card",
                ServingsLabel = "buns",
                PrepMinutes = 40,
                CookMinutes = 12,
                Ingredients =
                [
                    new() { Section = "Dough", Amount = 500, Unit = IngredientUnit.Gram, Name = "wheat flour" },
                    new() { Section = "Dough", Amount = 3, Unit = IngredientUnit.Decilitre, Name = "milk" },
                    new() { Section = "Dough", Amount = 50, Unit = IngredientUnit.Gram, Name = "fresh yeast" },
                    new() { Section = "Dough", Amount = 2, Unit = IngredientUnit.Teaspoon, Name = "ground cardamom" },
                    new() { Section = "Filling", Amount = 150, Unit = IngredientUnit.Gram, Name = "butter, soft" },
                    new() { Section = "Filling", Amount = 1, Unit = IngredientUnit.Decilitre, Name = "sugar" },
                ],
                Steps =
                [
                    new() { Section = "Dough", Text = "Warm the milk to 37 °C and dissolve the yeast in it." },
                    new() { Section = "Dough", Text = "Work in the flour and cardamom; knead for 10 minutes and let it rise for 45 minutes." },
                    new() { Section = "Shaping and baking", Text = "Roll out, spread the filling, roll up and cut into 2 cm slices." },
                    new() { Section = "Shaping and baking", Text = "Bake at 225 °C for 10–12 minutes." },
                ],
                TagIds = [RecipeGenerator.TagIdFor("Baking")],
            }, Json),
            Warnings = JsonSerializer.Serialize(new[] { RecipeImportWarningCodes.ServingsMissing }, Json),
            Version = 1,
        });

        var accepted = Import("wheat-loaf", user, anchor.AddDays(-20),
            "Weekend wheat loaf — 500 g flour, 3.5 dl water, 12 g yeast, salt, oil. Knead, prove 2 h, bake 40 min.");
        accepted.Drafts.Add(new RecipeImportDraft
        {
            RecipeImportDraftId = DeterministicGuid.From("recipe-import-draft::wheat-loaf"),
            Ordinal = 0,
            Status = RecipeImportDraftStatus.Accepted,
            Title = "Weekend wheat loaf",
            DraftJson = JsonSerializer.Serialize(new RecipeImportDraftContent { Title = "Weekend wheat loaf", Servings = 1, TagIds = [] }, Json),
            Warnings = "[]",
            AcceptedRecipeId = RecipeGenerator.IdFor("wheat-loaf"),
            AcceptedByUserId = user,
            AcceptedAt = anchor.AddDays(-20).AddMinutes(10),
            Version = 2,
        });

        return new Result([pending, accepted]);
    }

    private static RecipeImport Import(string key, string requester, DateTime finished, string text)
    {
        var import = new RecipeImport
        {
            RecipeImportId = IdFor(key),
            Status = RecipeImportStatus.Completed,
            ClientRequestId = DeterministicGuid.From($"recipe-import-request::{key}"),
            RequestedByUserId = requester,
            QueuedAt = finished.AddMinutes(-1),
            ClaimedAt = finished.AddSeconds(-58),
            TransferStartedAt = finished.AddSeconds(-57),
            CompletedAt = finished,
            FinishedAt = finished,
            Generation = 4,
            AnalyzerModel = "claude-sonnet-5",
            PromptVersion = RecipeImportLimits.PromptVersion,
            AnalyzerBaseUrlOrigin = "https://api.anthropic.com",
            AnalyzerBaseUrlHost = "api.anthropic.com",
            ProcessorInForce = "Anthropic",
            ConsentRecorded = true,
            ConsentMethod = "composer-checkbox",
            ConsentText = "Demo data: no source was sent.",
        };
        import.Sources.Add(new RecipeImportSource
        {
            RecipeImportSourceId = DeterministicGuid.From($"recipe-import-source::{key}#0"),
            Ordinal = 0,
            Kind = RecipeImportSourceKind.Text,
            Text = text,
        });
        return import;
    }
}
