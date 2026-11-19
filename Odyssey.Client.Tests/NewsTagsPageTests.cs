using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Internet;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Internet;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <c>/news-tags</c> is the shared <see cref="OdsTagAdmin{TRow}"/> over the <c>api/news-tags</c> tag contract, like the
/// journal and photo tag pages. What is the page's own: its route, claims and state key, and that every write drops the
/// news tag cache behind the feed and article pickers.
/// </summary>
public class NewsTagsPageTests
{
    private static readonly string[] AllTagClaims =
        [PermissionClaims.NewsRead, PermissionClaims.NewsTagsCreate, PermissionClaims.NewsTagsUpdate, PermissionClaims.NewsTagsDelete];

    private static ExistingNewsTag Tag(string name, DateTime? archived = null) =>
        new() { NewsTagId = Guid.NewGuid(), Name = name, Archived = archived };

    private static ApiResult<PagedResult<ExistingNewsTag>> Page(params ExistingNewsTag[] tags) =>
        ApiResult<PagedResult<ExistingNewsTag>>.Success(
            new PagedResult<ExistingNewsTag> { Items = [.. tags], Offset = 0, Limit = 25, TotalCount = tags.Length }, HttpStatusCode.OK);

    private static (BunitContext Ctx, Mock<ITagsApiClient<ExistingNewsTag>> Tags, Mock<INewsCatalogueCache> Catalogue) Create(params ExistingNewsTag[] tags)
    {
        var ctx = PageTestContext.Create(AllTagClaims);
        var api = new Mock<ITagsApiClient<ExistingNewsTag>>();
        api.Setup(t => t.ListAllAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<ExistingNewsTag>>.Success([.. tags], HttpStatusCode.OK));
        api.Setup(t => t.ListAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(tags));
        var catalogue = new Mock<INewsCatalogueCache>();
        ctx.Services.AddSingleton(api.Object);
        ctx.Services.AddSingleton(catalogue.Object);
        ctx.Render<MudPopoverProvider>();
        return (ctx, api, catalogue);
    }

    // OdsTagAdmin loads only in a browser (OperatingSystem.IsBrowser), so — as for the other four tag pages — what is
    // pinned here is the wiring the page hands it; the admin's own behaviour is covered where the component is.

    [Fact]
    public async Task The_page_is_the_shared_tag_admin_with_the_news_claims_and_copy()
    {
        var (ctx, _, _) = Create();
        await using var _ctx = ctx;

        var admin = ctx.Render<NewsTagsPage>().FindComponent<OdsTagAdmin<ExistingNewsTag>>().Instance;

        Assert.Equal(("News tags", "news tags", "news-tags-page"), (admin.Title, admin.Noun, admin.PageStateKey));
        Assert.Equal(
            (PermissionClaims.NewsTagsCreate, PermissionClaims.NewsTagsUpdate, PermissionClaims.NewsTagsDelete),
            (admin.CreateClaim, admin.UpdateClaim, admin.DeleteClaim));
        var tag = Tag("Tech", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)) with { Description = "Gadgets", Icon = "computer", Color = "#A1B2C3" };
        Assert.Equal(
            (tag.NewsTagId, "Tech", (string?)"Gadgets", tag.Archived, (string?)"computer", (string?)"#A1B2C3"),
            (admin.Id(tag), admin.Name(tag), admin.Description(tag), admin.Archived(tag), admin.IconOf!(tag), admin.ColorOf(tag)));
    }

    [Fact]
    public async Task Every_write_drops_the_news_tag_cache()
    {
        var (ctx, _, catalogue) = Create();
        await using var _ctx = ctx;

        var admin = ctx.Render<NewsTagsPage>().FindComponent<OdsTagAdmin<ExistingNewsTag>>().Instance;
        admin.OnTagsChanged!.Invoke();

        catalogue.Verify(c => c.InvalidateTags(), Times.Once);
        var code = File.ReadAllText(Path.Combine(ClientSource.Root, "Components", "OdsTagAdmin.razor.cs"));
        Assert.Contains("OnTagsChanged?.Invoke();", code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_route_policy_and_state_key_are_the_spec_ones()
    {
        var route = typeof(NewsTagsPage).GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>().Single();
        Assert.Equal("/news-tags", route.Template);
        var policy = typeof(NewsTagsPage).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Single();
        Assert.Equal(PermissionClaims.NewsRead, policy.Policy);
    }
}
