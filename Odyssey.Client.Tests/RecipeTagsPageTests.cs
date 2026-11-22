using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Library;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Recipes;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <c>/recipe-tags</c> (issue #526; design system components-recipes.md · Recipe tags) is the shared
/// <see cref="OdsTagAdmin{TRow}"/> over <c>api/recipe-tags</c>. What is the page's own: its route, claims, copy and state key.
/// </summary>
public class RecipeTagsPageTests
{
    private static readonly string[] AllTagClaims =
    [
        PermissionClaims.RecipeTagsRead, PermissionClaims.RecipeTagsCreate, PermissionClaims.RecipeTagsUpdate,
        PermissionClaims.RecipeTagsDelete,
    ];

    private static BunitContext Create()
    {
        var ctx = PageTestContext.Create(AllTagClaims);
        var api = new Mock<ITagsApiClient<ExistingRecipeTag>>();
        api.Setup(t => t.ListAllAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<ExistingRecipeTag>>.Success([], HttpStatusCode.OK));
        api.Setup(t => t.ListAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<PagedResult<ExistingRecipeTag>>.Success(
                new PagedResult<ExistingRecipeTag> { Items = [], Offset = 0, Limit = 25, TotalCount = 0 }, HttpStatusCode.OK));
        ctx.Services.AddSingleton(api.Object);
        ctx.Render<MudPopoverProvider>();
        return ctx;
    }

    [Fact]
    public async Task The_page_is_the_shared_tag_admin_with_the_recipe_claims_and_copy()
    {
        await using var ctx = Create();

        var admin = ctx.Render<RecipeTagsPage>().FindComponent<OdsTagAdmin<ExistingRecipeTag>>().Instance;

        Assert.Equal(("Recipe tags", "recipe tags", "recipe-tags-page"), (admin.Title, admin.Noun, admin.PageStateKey));
        Assert.Equal(
            (PermissionClaims.RecipeTagsCreate, PermissionClaims.RecipeTagsUpdate, PermissionClaims.RecipeTagsDelete),
            (admin.CreateClaim, admin.UpdateClaim, admin.DeleteClaim));
        Assert.Equal("Tags group recipes by category.", admin.ModalSubtitle);
        Assert.Contains("every recipe carrying it", admin.IconHelp, StringComparison.Ordinal);
        var tag = new ExistingRecipeTag
        {
            RecipeTagId = Guid.NewGuid(),
            Name = "Baking",
            Description = "Bread",
            Archived = new DateTime(2026, 1, 8, 0, 0, 0, DateTimeKind.Utc),
            Icon = "bakery_dining",
            Color = "#F59E0B",
        };
        Assert.Equal(
            (tag.RecipeTagId, "Baking", (string?)"Bread", tag.Archived, (string?)"bakery_dining", (string?)"#F59E0B"),
            (admin.Id(tag), admin.Name(tag), admin.Description(tag), admin.Archived(tag), admin.IconOf!(tag), admin.ColorOf(tag)));
    }

    [Fact]
    public void The_route_and_policy_are_the_spec_ones()
    {
        var route = typeof(RecipeTagsPage).GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>().Single();
        Assert.Equal("/recipe-tags", route.Template);
        var policy = typeof(RecipeTagsPage).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Single();
        Assert.Equal(PermissionClaims.RecipeTagsRead, policy.Policy);
    }

    [Fact]
    public void The_recipes_page_route_and_policy_are_the_spec_ones()
    {
        var route = typeof(RecipesPage).GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>().Single();
        Assert.Equal("/recipes", route.Template);
        var policy = typeof(RecipesPage).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Single();
        Assert.Equal(PermissionClaims.RecipesRead, policy.Policy);
    }

    [Fact]
    public void A_recipe_tag_field_creates_recipe_tags_and_names_its_page()
    {
        var kind = Assert.Single(OdsTypeRegistries.TagCreateKinds.Recipe);
        Assert.Equal(("recipe", "Recipe tag"), (kind.Key, kind.Label));
        Assert.Equal("Recipe tags", Odyssey.Client.Services.TagNameRule.Pages.Recipe);
    }
}
