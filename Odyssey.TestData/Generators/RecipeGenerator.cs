using System.Text;
using Odyssey.Context;
using Odyssey.Core.FileStorage.Files;
using Odyssey.Dtos;
using Odyssey.Dtos.Recipes;

namespace Odyssey.TestData.Generators;

/// <summary>
/// Deterministic recipes for the Library module (issue #525 §11): six recipe tags (one archived), five recipes
/// (one archived) and their ingredients, steps and tag links — the same household cookbook the design system shows.
/// </summary>
/// <remarks>
/// <para>
/// The recipes exercise the read surfaces: sections on ingredients and steps, an unquantified "to taste" line, Pinch
/// (never scaled), an optional line, a servings label ("Makes 12 pieces"), a web-address source, a recipe with no
/// times, an edited recipe and an archived one. Ingredient names are plain ("wheat flour"); how a thing is prepared
/// is said in the method step that does it (issue #595).
/// </para>
/// <para>
/// Like the journal, the recipe rows keep only photo and file <em>references</em>, so this generator also provisions
/// what they point at: library photos (gradient PNGs) and Library-area files (a PDF, a PNG scan, a text note). They are
/// returned separately because only the worker's demo-file seeder, which has the file volumes, can write them and the
/// links with them (real foreign keys). Nav collections are left empty so nothing double-inserts.
/// </para>
/// </remarks>
public static class RecipeGenerator
{
    public sealed record Result(
        IReadOnlyList<RecipeTag> Tags,
        IReadOnlyList<Recipe> Recipes,
        IReadOnlyList<RecipeIngredient> Ingredients,
        IReadOnlyList<RecipeStep> Steps,
        IReadOnlyList<RecipeTagLink> TagLinks,
        IReadOnlyList<RecipePhoto> Photos,
        IReadOnlyList<RecipeAttachment> Attachments,
        IReadOnlyList<DemoFile> Files,
        IReadOnlyList<DemoPhoto> LibraryPhotos);

    private sealed record TagSpec(string Name, string? Description, string? Icon, string? Color, bool Archived);

    private static readonly TagSpec[] TagDefinitions =
    [
        new("Weeknight", "Done in under an hour on a work day.", "schedule", Swatch("teal"), false),
        new("Baking", "Bread, buns and cakes.", "bakery_dining", Swatch("amber"), false),
        new("Vegetarian", null, "local_florist", Swatch("green"), false),
        new("Family", "Handed down — keep the notes.", "family_restroom", Swatch("violet"), false),
        new("Soup", null, null, null, false),
        new("Christmas", null, "celebration", Swatch("coral"), true),
    ];

    private sealed record Line(decimal? Amount, IngredientUnit Unit, string Name, string? Section = null, bool Optional = false);

    private sealed record Step(string Text, string? Section = null);

    private sealed record FileSpec(string FileName, string ContentType, Func<string, byte[]> Content);

    private sealed record RecipeSpec(
        string Key,
        string Title,
        string? Description,
        int Servings,
        string? ServingsLabel,
        int? PrepMinutes,
        int? CookMinutes,
        string? Source,
        string? Notes,
        string[] TagNames,
        Line[] Ingredients,
        Step[] Steps,
        int PhotoCount,
        FileSpec[] Files,
        string AuthorRole,
        string? EditorRole,
        int CreatedDaysAgo,
        bool Archived);

    // The palette is declared once (ColorPalette); a tag names a swatch by key.
    private static string Swatch(string key) => ColorPalette.All.Single(s => s.Key == key).Hex;

    public static Guid TagIdFor(string name) => DeterministicGuid.From($"recipe-tag::{name}");

    public static Guid IdFor(string key) => DeterministicGuid.From($"recipe::{key}");

    public static Result Generate(DateTime anchor)
    {
        var tags = TagDefinitions
            .Select(spec => new RecipeTag
            {
                RecipeTagId = TagIdFor(spec.Name),
                Name = spec.Name,
                Description = spec.Description,
                Icon = spec.Icon,
                Color = spec.Color,
                Archived = spec.Archived ? anchor.AddMonths(-10) : null,
            })
            .ToList();

        var recipes = new List<Recipe>();
        var ingredients = new List<RecipeIngredient>();
        var steps = new List<RecipeStep>();
        var tagLinks = new List<RecipeTagLink>();
        var photos = new List<RecipePhoto>();
        var libraryPhotos = new List<DemoPhoto>();
        var attachments = new List<RecipeAttachment>();
        var files = new List<DemoFile>();

        foreach (var spec in Specs)
        {
            var recipeId = IdFor(spec.Key);
            var authorId = UserId(spec.AuthorRole);
            var created = anchor.AddDays(-spec.CreatedDaysAgo);
            var updated = spec.EditorRole is null ? created : created.AddDays(12);

            recipes.Add(new Recipe
            {
                RecipeId = recipeId,
                Title = spec.Title,
                Description = spec.Description,
                Notes = spec.Notes,
                Source = spec.Source,
                Servings = spec.Servings,
                ServingsLabel = spec.ServingsLabel,
                PrepMinutes = spec.PrepMinutes,
                CookMinutes = spec.CookMinutes,
                Version = spec.EditorRole is null ? 1 : 2,
                CreatedByUserId = authorId,
                UpdatedByUserId = spec.EditorRole is null ? authorId : UserId(spec.EditorRole),
                CreatedAt = created,
                UpdatedAt = updated,
                Archived = spec.Archived ? updated.AddDays(30) : null,
            });

            for (var i = 0; i < spec.Ingredients.Length; i++)
            {
                var line = spec.Ingredients[i];
                ingredients.Add(new RecipeIngredient
                {
                    RecipeIngredientId = DeterministicGuid.From($"recipe-ingredient::{spec.Key}#{i}"),
                    RecipeId = recipeId,
                    Position = i,
                    Section = line.Section,
                    Amount = line.Amount,
                    Unit = line.Unit,
                    Name = line.Name,
                    Optional = line.Optional,
                });
            }

            for (var i = 0; i < spec.Steps.Length; i++)
            {
                steps.Add(new RecipeStep
                {
                    RecipeStepId = DeterministicGuid.From($"recipe-step::{spec.Key}#{i}"),
                    RecipeId = recipeId,
                    Position = i,
                    Section = spec.Steps[i].Section,
                    Text = spec.Steps[i].Text,
                });
            }

            foreach (var tagName in spec.TagNames.Distinct())
            {
                tagLinks.Add(new RecipeTagLink
                {
                    RecipeTagLinkId = DeterministicGuid.From($"recipe-tag-link::{spec.Key}::{tagName}"),
                    RecipeId = recipeId,
                    RecipeTagId = TagIdFor(tagName),
                });
            }

            for (var i = 0; i < spec.PhotoCount; i++)
            {
                var photoId = DeterministicGuid.From($"photo::recipe::{spec.Key}#{i}");
                var seed = spec.Key.Aggregate(i + 7, (acc, ch) => acc * 31 + ch);
                libraryPhotos.Add(DemoFileFactory.Photo(new Photo
                {
                    PhotoId = photoId,
                    Title = $"{spec.Title} — photo {i + 1}",
                    TakenAt = DateTime.SpecifyKind(created, DateTimeKind.Unspecified),
                    CreatedByUserId = authorId,
                    CreatedAt = created,
                    UpdatedAt = created,
                },
                    $"{spec.Key}-photo-{i + 1}.png", "image/png", DemoImages.GradientPng(DemoImages.PhotoSize, seed),
                    // Never seeded as a Files-store file; a stable id keeps the worker's former-id match inert.
                    DeterministicGuid.From($"recipe-photo-file::{spec.Key}#{i}")));

                photos.Add(new RecipePhoto
                {
                    RecipePhotoId = DeterministicGuid.From($"recipe-photo::{spec.Key}#{i}"),
                    RecipeId = recipeId,
                    PhotoId = photoId,
                    Position = i,
                    CreatedAt = created,
                });
            }

            for (var i = 0; i < spec.Files.Length; i++)
            {
                var fileSpec = spec.Files[i];
                var file = DemoFileFactory.File(
                    DeterministicGuid.From($"recipe-attachment-file::{spec.Key}#{i}"), StoredFileArea.Library, authorId,
                    fileSpec.FileName, fileSpec.ContentType, "Demo recipe file.", created, fileSpec.Content(spec.Title));
                files.Add(file);
                attachments.Add(new RecipeAttachment
                {
                    RecipeAttachmentId = DeterministicGuid.From($"recipe-attachment::{spec.Key}#{i}"),
                    RecipeId = recipeId,
                    FileId = file.Metadata.Id,
                    CreatedAt = created,
                });
            }
        }

        return new Result(tags, recipes, ingredients, steps, tagLinks, photos, attachments, files, libraryPhotos);
    }

    private static string UserId(string role) => DemoUsers.All.First(user => user.Role == role).Id;

    private static byte[] Pdf(string title) =>
        MinimalPdf.Create(title, "Odyssey demo data — the original recipe.", "Scanned from the family notebook.");

    private static byte[] Text(string body) => Encoding.UTF8.GetBytes(body);

    private static readonly RecipeSpec[] Specs =
    [
        new(
            "wheat-loaf", "Weekend wheat loaf",
            "A plain, reliable sandwich loaf. Start it after breakfast, slice it for lunch.",
            1, "loaf", 25, 40, "https://example.org/recipes/weekend-wheat-loaf",
            "Proving time is not in the totals: allow about 2 h at room temperature.\nThe dough is ready when a floured finger leaves a dent that springs back slowly.",
            ["Baking", "Vegetarian"],
            [
                new(500m, IngredientUnit.Gram, "wheat flour"),
                new(3.5m, IngredientUnit.Decilitre, "lukewarm water"),
                new(12m, IngredientUnit.Gram, "fresh yeast"),
                new(1.5m, IngredientUnit.Teaspoon, "salt"),
                new(2m, IngredientUnit.Tablespoon, "rapeseed oil"),
                new(1m, IngredientUnit.Tablespoon, "honey", Optional: true),
            ],
            [
                new("Crumble the yeast into the lukewarm water and stir until it dissolves."),
                new("Add the oil, salt and honey, then sift in the flour a little at a time. Knead for 10 minutes until smooth and elastic."),
                new("Cover and leave to rise until doubled, about 1 hour."),
                new("Knock back, shape into a loaf and place in a greased 1.5 litre tin. Rise again for 45 minutes."),
                new("Bake at 220 °C for 10 minutes, then lower to 200 °C for 30 minutes. Cool on a rack before slicing."),
            ],
            2,
            [
                new("wheat_loaf_recipe_card.png", "image/png", _ => DemoImages.GradientPng(DemoImages.PhotoSize, 4242)),
                new("proving_times.txt", "text/plain", _ => Text("Proving times\n\n20 °C: about 2 h\n24 °C: about 1 h 30 min\nFridge: overnight, 10–12 h\n")),
            ],
            "Owner", "User", 250, false),
        new(
            "tomato-soup", "Roasted tomato soup",
            "Garden tomatoes, roasted with garlic, blended smooth.",
            4, null, 15, 50, "Grandma’s kitchen notebook",
            "Freezes well for 3 months. Leave out the cream if freezing; add it when reheating.",
            ["Vegetarian", "Soup", "Family"],
            [
                new(1.2m, IngredientUnit.Kilogram, "ripe tomatoes", "Roasting tray"),
                new(1m, IngredientUnit.None, "red onion", "Roasting tray"),
                new(4m, IngredientUnit.Clove, "garlic", "Roasting tray"),
                new(3m, IngredientUnit.Tablespoon, "olive oil", Section: "Roasting tray"),
                new(7.5m, IngredientUnit.Decilitre, "vegetable stock", Section: "Pot"),
                new(1m, IngredientUnit.Bunch, "basil", Section: "Pot"),
                new(1m, IngredientUnit.Decilitre, "cream", Section: "Pot", Optional: true),
                new(1m, IngredientUnit.Pinch, "sugar", Section: "Pot"),
                new(null, IngredientUnit.ToTaste, "salt and pepper", Section: "Pot"),
            ],
            [
                new("Heat the oven to 200 °C.", "Roasting tray"),
                new("Halve the tomatoes and cut the onion into wedges. Spread them on a tray with the unpeeled garlic cloves, drizzle with oil and season.", "Roasting tray"),
                new("Roast for 40 minutes until the tomatoes collapse and catch at the edges.", "Roasting tray"),
                new("Squeeze the garlic from its skins into a pot. Add the roasted vegetables and the stock and simmer for 10 minutes.", "Pot"),
                new("Add the basil and blend until smooth. Stir in the cream and the sugar.", "Pot"),
                new("Season to taste and serve hot.", "Pot"),
            ],
            3,
            [new("Grandmas_tomato_soup.pdf", "application/pdf", Pdf)],
            "Owner", null, 95, false),
        new(
            "cardamom-buns", "Cardamom buns", null,
            12, "pieces", 40, 12, null, null,
            ["Baking", "Family", "Christmas"],
            [
                new(5m, IngredientUnit.Decilitre, "milk", Section: "Dough"),
                new(50m, IngredientUnit.Gram, "fresh yeast", Section: "Dough"),
                new(150m, IngredientUnit.Gram, "butter", "Dough"),
                new(1m, IngredientUnit.Decilitre, "sugar", Section: "Dough"),
                new(2m, IngredientUnit.Teaspoon, "ground cardamom", Section: "Dough"),
                new(850m, IngredientUnit.Gram, "wheat flour", Section: "Dough"),
                new(100m, IngredientUnit.Gram, "butter", "Filling"),
                new(0.5m, IngredientUnit.Decilitre, "sugar", Section: "Filling"),
                new(1m, IngredientUnit.Tablespoon, "ground cardamom", Section: "Filling"),
                new(1m, IngredientUnit.None, "egg", "Filling"),
            ],
            [
                new("Warm the milk to 37 °C and dissolve the yeast in it.", "Dough"),
                new("Mix in the melted butter, sugar, cardamom and most of the flour. Knead for 8 minutes and rise for 30 minutes.", "Dough"),
                new("Beat the soft butter, sugar and cardamom together.", "Filling"),
                new("Roll the dough out, spread with the filling, fold in three and cut into 12 strips. Twist each strip into a knot.", "Shaping and baking"),
                new("Rise for 30 minutes, brush with beaten egg and bake at 225 °C for 10–12 minutes.", "Shaping and baking"),
            ],
            0, [], "User", null, 20, false),
        new(
            "garlic-pasta", "Ten-minute garlic pasta", "For the evenings nobody wants to cook.",
            2, null, 5, 10, null, null,
            ["Weeknight", "Vegetarian"],
            [
                new(200m, IngredientUnit.Gram, "spaghetti"),
                new(3m, IngredientUnit.Clove, "garlic"),
                new(4m, IngredientUnit.Tablespoon, "olive oil"),
                new(1m, IngredientUnit.Pinch, "chilli flakes"),
                new(1m, IngredientUnit.Bunch, "parsley"),
                new(null, IngredientUnit.ToTaste, "parmesan", Optional: true),
            ],
            [
                new("Cook the spaghetti in well-salted water."),
                new("Meanwhile, slice the garlic thinly and warm it gently in the oil until pale gold. Add the chilli."),
                new("Toss the drained pasta in the pan with a splash of the cooking water and the chopped parsley."),
            ],
            0, [], "Admin", "Owner", 200, false),
        new(
            "lamb-cabbage-stew", "Lamb and cabbage stew", "The autumn classic. Simmer slowly.",
            6, null, null, null, null, "Better on day two.",
            ["Family"],
            [
                new(1.5m, IngredientUnit.Kilogram, "lamb shoulder on the bone"),
                new(1.5m, IngredientUnit.Kilogram, "white cabbage"),
                new(1m, IngredientUnit.Tablespoon, "whole black peppercorns"),
                new(2m, IngredientUnit.Teaspoon, "salt"),
                new(3m, IngredientUnit.Decilitre, "water"),
            ],
            [
                new("Cut the lamb into pieces and the cabbage into wedges. Layer them in a heavy pot, sprinkling salt and peppercorns between the layers."),
                new("Pour in the water, bring to the boil, then simmer covered for 2½ hours until the meat falls from the bone."),
            ],
            0, [], "Owner", null, 400, true),
    ];
}
