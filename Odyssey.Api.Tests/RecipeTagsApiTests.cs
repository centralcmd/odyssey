using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Recipes;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>The recipe-tag endpoints (issue #525 §5 items 6–10).</summary>
public class RecipeTagsApiTests(RecipeTagsApiTests.Host host) : SharedApiHostTests<RecipeTagsApiTests.Host>(host)
{
    private const string Path = "/api/recipe-tags";

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.RecipeTagsRead, PermissionClaims.RecipeTagsCreate,
        PermissionClaims.RecipeTagsUpdate, PermissionClaims.RecipeTagsDelete,
        PermissionClaims.RecipesRead, PermissionClaims.RecipesCreate,
    ];

    [Theory]
    [InlineData(PermissionClaims.RecipeTagsRead, "GET")]
    [InlineData(PermissionClaims.RecipeTagsCreate, "POST")]
    [InlineData(PermissionClaims.RecipeTagsUpdate, "PUT")]
    [InlineData(PermissionClaims.RecipeTagsDelete, "DELETE")]
    public async Task EachEndpoint_RequiresItsOwnClaim(string claim, string method)
    {
        var id = await SeedTagAsync("Gated");
        var allOthers = ReadWrite.Where(c => c != claim).ToArray();
        using var without = Factory.ClientWith(allOthers);
        using var with = Factory.ClientWith(claim);

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(without)).StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await Send(with)).StatusCode);

        Task<HttpResponseMessage> Send(HttpClient client) => method switch
        {
            "GET" => client.GetAsync(Path),
            "POST" => client.PostAsJsonAsync(Path, new NewRecipeTag { Name = $"n{Guid.NewGuid():N}" }),
            "PUT" => client.PutAsJsonAsync($"{Path}/{id}", new UpdateRecipeTag { Name = "Gated" }),
            _ => client.DeleteAsync($"{Path}/{Guid.NewGuid()}"),
        };
    }

    [Fact]
    public async Task RoundTrip_CountsRecipes_AndAnOmittedStatusListsArchivedToo()
    {
        using var client = Factory.ClientWith(ReadWrite);

        var post = await client.PostAsJsonAsync(Path, new NewRecipeTag { Name = "Weeknight", Icon = "restaurant", Color = "#aabbcc" });
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var tag = (await post.Content.ReadFromJsonAsync<ExistingRecipeTag>())!;
        Assert.Equal("#AABBCC", tag.Color);
        Assert.Equal(0, tag.RecipeCount);

        (await client.PostAsJsonAsync("/api/recipes", new NewRecipe { Title = "Soup", Servings = 2, TagIds = [tag.RecipeTagId] }))
            .EnsureSuccessStatusCode();

        var archive = await client.PutAsJsonAsync($"{Path}/{tag.RecipeTagId}", new UpdateRecipeTag { Name = "Weeknight", Archived = true });
        archive.EnsureSuccessStatusCode();
        var archived = (await archive.Content.ReadFromJsonAsync<ExistingRecipeTag>())!;
        Assert.NotNull(archived.Archived);
        Assert.Equal(1, archived.RecipeCount);

        var all = (await client.GetFromJsonAsync<Page>(Path))!.Items;
        Assert.Contains(all, t => t.RecipeTagId == tag.RecipeTagId);
        Assert.Empty((await client.GetFromJsonAsync<Page>($"{Path}?status=Active"))!.Items);
    }

    [Fact]
    public async Task Create_ANameDifferingOnlyInCase_FromAnArchivedTag_IsConflict()
    {
        await SeedTagAsync("Holiday", archived: true);
        using var client = Factory.ClientWith(ReadWrite);

        var response = await client.PostAsJsonAsync(Path, new NewRecipeTag { Name = "holiday" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Delete_InUse_IsConflict_AndUnused_IsNoContent()
    {
        var used = await SeedTagAsync("Used");
        var unused = await SeedTagAsync("Unused");
        using var client = Factory.ClientWith(ReadWrite);
        (await client.PostAsJsonAsync("/api/recipes", new NewRecipe { Title = "Stew", Servings = 4, TagIds = [used] }))
            .EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"{Path}/{used}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Path}/{unused}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Path}/{unused}")).StatusCode);
    }

    [Fact]
    public async Task List_SortsByRecipeCount()
    {
        var popular = await SeedTagAsync("Popular");
        var rare = await SeedTagAsync("Rare");
        using var client = Factory.ClientWith(ReadWrite);
        foreach (var tags in new[] { new[] { popular }, new[] { popular, rare } })
        {
            (await client.PostAsJsonAsync("/api/recipes", new NewRecipe { Title = "R", Servings = 1, TagIds = tags })).EnsureSuccessStatusCode();
        }

        var sorted = (await client.GetFromJsonAsync<Page>($"{Path}?sortBy=RecipeCount"))!.Items;

        Assert.Equal([popular, rare], sorted.Select(t => t.RecipeTagId));
    }

    private async Task<Guid> SeedTagAsync(string name, bool archived = false)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();
        var tag = new RecipeTag { RecipeTagId = Guid.NewGuid(), Name = name, Archived = archived ? DateTime.UtcNow : null };
        context.RecipeTags.Add(tag);
        await context.SaveChangesAsync();
        return tag.RecipeTagId;
    }

    private sealed record Page(List<ExistingRecipeTag> Items);

    /// <summary>The class's shared host: every caller's claims travel per request.</summary>
    public sealed class Host() : OdysseyApiFactory([], "recipe-tags-actor-id");
}
