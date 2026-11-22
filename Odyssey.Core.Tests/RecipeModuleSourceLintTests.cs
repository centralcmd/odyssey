using System.Text.RegularExpressions;
using Odyssey.Testing;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// Recipes depend on Journal (for library photos) and never the other way round with Finance (issue #525 §3, AC 26):
/// nothing under <c>Odyssey.Core/Finance/</c> may reference <c>Odyssey.Core.Recipes</c>. One project, so this lint is
/// the only thing that keeps the direction.
/// </summary>
public partial class RecipeModuleSourceLintTests
{
    [Fact]
    public void Finance_takes_no_dependency_on_recipes()
    {
        var root = SourceTree.Root;
        var files = SourceTree.Files(Path.Combine("Odyssey.Core", "Finance"))
            .Select(path => (Path: Path.GetRelativePath(root, path).Replace('\\', '/'), Code: SourceLint.StripComments(File.ReadAllText(path))))
            .ToList();

        // A scan set that silently empties would keep passing after a move.
        Assert.NotEmpty(files);
        var offenders = files.Where(f => RecipesReference().IsMatch(f.Code)).Select(f => f.Path).ToList();
        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("using Odyssey.Core.Recipes;", true)]
    [InlineData("var s = new Recipes.RecipeService(a, b, c);", true)]
    [InlineData("context.Recipes.CountAsync()", false)]
    [InlineData("using Odyssey.Dtos.Recipes;", false)]
    public void The_pattern_matches_a_reference_and_skips_its_look_alikes(string code, bool matches) =>
        Assert.Equal(matches, RecipesReference().IsMatch(code));

    [GeneratedRegex(@"\bOdyssey\.Core\.Recipes\b|(?<![\w.])Recipes\.[A-Z]")]
    private static partial Regex RecipesReference();
}
