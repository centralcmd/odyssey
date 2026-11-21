using Odyssey.ApiClient.Resources;
using Odyssey.Dtos;
using Odyssey.Dtos.Recipes;
using Odyssey.Testing.Http;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>The recipe client's wire shape (issue #525 §5): every filter reaches the query string, and an unset one stays off.</summary>
public class RecipesApiClientTests
{
    private static (RecipesApiClient Client, RecordingHttpHandler Handler) Create(string body = """{"items":[],"offset":0,"limit":25,"totalCount":0}""")
    {
        var handler = new RecordingHttpHandler(body);
        var api = new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });
        return (new RecipesApiClient(api), handler);
    }

    [Fact]
    public async Task List_SendsEveryFilter_AndTheWindow()
    {
        var (client, handler) = Create();
        var tagA = Guid.NewGuid();
        var tagB = Guid.NewGuid();

        await client.ListAsync(new RecipeListFilter
        {
            Search = "flour",
            TagIds = [tagA, tagB],
            MaxTotalMinutes = 30,
            HasPhotos = false,
            Status = ArchivalStatus.Archived,
            SortBy = RecipeSortBy.TotalMinutes,
            SortDir = SortDirection.Desc,
        }, page: 3, pageSize: 25);

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Equal("/api/recipes", handler.LastRequest.RequestUri.AbsolutePath);
        foreach (var expected in new[]
        {
            "search=flour", $"tagIds={tagA}", $"tagIds={tagB}", "maxTotalMinutes=30", "hasPhotos=false",
            "status=Archived", "sortBy=TotalMinutes", "sortDir=Desc", "offset=50", "limit=25",
        })
        {
            Assert.Contains(expected, query, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task List_WithoutFilters_SendsOnlyTheWindow()
    {
        var (client, handler) = Create();

        await client.ListAsync();

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.DoesNotContain("hasPhotos", query, StringComparison.Ordinal);
        Assert.DoesNotContain("tagIds", query, StringComparison.Ordinal);
        Assert.DoesNotContain("status", query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData(8, "?servings=8")]
    public async Task Get_AddsServingsOnlyWhenAsked(int? servings, string expectedQuery)
    {
        var (client, handler) = Create("{}");
        var id = Guid.NewGuid();

        await client.GetAsync(id, servings);

        Assert.Equal($"/api/recipes/{id}", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal(expectedQuery, handler.LastRequest.RequestUri.Query);
    }

    [Fact]
    public async Task Update_PutsToTheRecipe_AndSendsANullPhotoIdsAsNull()
    {
        var (client, handler) = Create("{}");
        var id = Guid.NewGuid();

        await client.UpdateAsync(id, new UpdateRecipe { Title = "t", Servings = 1, Version = 2, PhotoIds = null });

        Assert.Equal(HttpMethod.Put, handler.LastRequest!.Method);
        Assert.Equal($"/api/recipes/{id}", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Contains("\"photoIds\":null", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"version\":2", handler.LastBody, StringComparison.Ordinal);
    }
}
