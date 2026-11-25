using Odyssey.Client.Layout;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The Library module (issue #526; design system components-recipes.md · Navigation): between Journal and Internet,
/// collections first (Recipes), tag pages under Reference (Recipe Tags), each gated by its page's claim.
/// </summary>
public class LibraryNavigationTests
{
    private static Func<NavPage, bool> Holding(params string[] claims) => page => page.Claim is null || claims.Contains(page.Claim);

    [Fact]
    public void The_library_module_holds_books_recipes_and_recipe_imports_then_their_tag_pages()
    {
        var module = NavModel.All.Single(m => m.Key == "library");

        Assert.Equal(("Library", "local_library"), (module.Label, module.Icon));
        Assert.Equal([null, "Reference"], module.Groups.Select(g => g.Label));
        Assert.Equal(
            [("books", "Books", "auto_stories", "books", PermissionClaims.BooksRead),
                ("recipes", "Recipes", "soup_kitchen", "recipes", PermissionClaims.RecipesRead),
                ("recipe-imports", "Recipe imports", "auto_awesome", "recipe-imports", PermissionClaims.RecipesImport),
                ("book-tags", "Book Tags", "local_offer", "book-tags", PermissionClaims.BookTagsRead),
                ("recipe-tags", "Recipe Tags", "local_offer", "recipe-tags", PermissionClaims.RecipeTagsRead)],
            module.Groups.SelectMany(g => g.Items).Select(p => (p.Key, p.Label, p.Icon, p.Href, p.Claim)));
    }

    [Fact]
    public void The_module_sits_between_journal_and_internet()
    {
        var keys = NavModel.All.Select(m => m.Key).ToList();
        Assert.Equal(keys.IndexOf("journal") + 1, keys.IndexOf("library"));
        Assert.Equal(keys.IndexOf("library") + 1, keys.IndexOf("internet"));
    }

    [Fact]
    public void The_module_is_gone_without_either_claim_and_each_claim_shows_its_own_page()
    {
        Assert.DoesNotContain(NavModel.VisibleModules(Holding()), m => m.Key == "library");

        var tagsOnly = NavModel.AllVisiblePages(Holding(PermissionClaims.RecipeTagsRead)).Where(h => h.ModuleKey == "library");
        Assert.Equal(["recipe-tags"], tagsOnly.Select(h => h.Page.Key));

        var both = NavModel.AllVisiblePages(Holding(PermissionClaims.RecipesRead, PermissionClaims.RecipeTagsRead))
            .Where(h => h.ModuleKey == "library");
        Assert.Equal(["recipes", "recipe-tags"], both.Select(h => h.Page.Key));
    }

    [Fact]
    public void Recipe_tags_does_not_highlight_recipes() =>
        Assert.Equal("recipe-tags", NavModel.All.Single(m => m.Key == "library").Groups.SelectMany(g => g.Items)
            .Single(p => NavModel.IsActive(p, "recipe-tags")).Href);
}
