using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Pages.Library;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Recipes;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <c>/recipes</c> rendered whole (issue #526): the list's card head, the open card's lines and check-offs, and the
/// claim-gated sections. The menu, format and payload rules have their own unit tests.
/// </summary>
public class RecipesPageTests
{
    private static readonly Guid SoupId = Guid.NewGuid();

    private static RecipeSummary Summary() => new()
    {
        RecipeId = SoupId,
        Title = "Roasted tomato soup",
        Servings = 4,
        PrepMinutes = 15,
        CookMinutes = 50,
        TotalMinutes = 65,
        IngredientCount = 2,
        StepCount = 2,
        AttachmentCount = 1,
        UpdatedAt = DateTime.UtcNow,
    };

    private static ExistingRecipe Detail(int servings = 4) => new()
    {
        RecipeId = SoupId,
        Version = 2,
        Title = "Roasted tomato soup",
        Servings = 4,
        DisplayedServings = servings,
        Scaled = servings != 4,
        PrepMinutes = 15,
        CookMinutes = 50,
        TotalMinutes = 65,
        Source = "https://example.org/soup",
        CreatedAt = new DateTime(2026, 3, 14, 9, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 3, 14, 9, 0, 0, DateTimeKind.Utc),
        CreatedByName = null,
        Ingredients =
        [
            new() { RecipeIngredientId = Guid.NewGuid(), Position = 0, Amount = servings * 300, Unit = IngredientUnit.Gram, Name = "ripe tomatoes", Section = "Roasting tray" },
            new() { RecipeIngredientId = Guid.NewGuid(), Position = 1, Unit = IngredientUnit.ToTaste, Name = "salt" },
        ],
        Steps =
        [
            new() { RecipeStepId = Guid.NewGuid(), Position = 0, Text = "Roast.", Section = "Roast" },
            new() { RecipeStepId = Guid.NewGuid(), Position = 1, Text = "Blend.", Section = "Pot" },
        ],
        Photos = [],
        Attachments = [new() { RecipeAttachmentId = Guid.NewGuid(), FileId = Guid.NewGuid(), FileName = "original.pdf", ContentType = "application/pdf", CreatedAt = DateTime.UtcNow }],
    };

    private static (BunitContext Ctx, Mock<IRecipesApiClient> Api) Create(params string[] claims)
    {
        var ctx = PageTestContext.Create(claims);
        var api = new Mock<IRecipesApiClient>();
        api.Setup(a => a.ListAsync(It.IsAny<RecipeListFilter?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<PagedResult<RecipeSummary>>.Success(
                new PagedResult<RecipeSummary> { Items = [Summary()], Offset = 0, Limit = 25, TotalCount = 1 }, HttpStatusCode.OK));
        api.Setup(a => a.GetAsync(SoupId, It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, int? n, CancellationToken _) => ApiResult<ExistingRecipe>.Success(Detail(n ?? 4), HttpStatusCode.OK));
        ctx.Services.AddSingleton(api.Object);
        ctx.Render<MudPopoverProvider>();
        return (ctx, api);
    }

    private static readonly string[] Owner =
    [
        PermissionClaims.RecipesRead, PermissionClaims.RecipesCreate, PermissionClaims.RecipesUpdate, PermissionClaims.RecipesDelete,
        PermissionClaims.RecipeTagsRead, PermissionClaims.PhotosRead, PermissionClaims.FilesRead,
    ];

    [Fact]
    public async Task The_card_head_reads_yield_times_and_counts()
    {
        var (ctx, _) = Create(Owner);
        await using var _ctx = ctx;

        var page = ctx.Render<RecipesPage>();

        page.WaitForAssertion(() => Assert.Contains("Roasted tomato soup", page.Markup, StringComparison.Ordinal));
        Assert.Contains("Serves 4", page.Markup, StringComparison.Ordinal);
        Assert.Contains("Prep 15 min · Cook 50 min", page.Markup, StringComparison.Ordinal);
        Assert.Contains("Total 1 h 5 min", page.Markup, StringComparison.Ordinal);
        Assert.Contains("restaurant_menu", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_open_card_lists_grouped_lines_with_spoken_units_and_runs_the_step_numbers_on()
    {
        var (ctx, _) = Create(Owner);
        await using var _ctx = ctx;
        var page = ctx.Render<RecipesPage>();
        page.WaitForAssertion(() => page.Find($"#{RecipesPage.CardId(SoupId)} button[aria-expanded]"));

        page.Find($"#{RecipesPage.CardId(SoupId)} button[aria-expanded]").Click();

        page.WaitForAssertion(() => Assert.Contains("ripe tomatoes", page.Markup, StringComparison.Ordinal));
        Assert.Contains("Roasting tray", page.Find("h4.rc-h4").TextContent, StringComparison.Ordinal);
        var amount = page.Find(".rc-amt");
        Assert.Equal("g", amount.QuerySelector("[aria-hidden=true]")!.TextContent);
        Assert.Equal("grams", amount.QuerySelector(".odc-sr-only")!.TextContent);
        Assert.Contains(", to taste", page.Markup, StringComparison.Ordinal);
        Assert.Equal(["1", "2"], page.FindAll("ol.rc-step-list").Select(o => o.GetAttribute("start")));
        Assert.Contains("A removed user", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Updated by", page.Markup, StringComparison.Ordinal);
        Assert.Contains("No photos yet — add one", page.Markup, StringComparison.Ordinal);
        Assert.Contains("original.pdf", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_check_off_strikes_the_line_through()
    {
        var (ctx, _) = Create(Owner);
        await using var _ctx = ctx;
        var page = ctx.Render<RecipesPage>();
        page.WaitForAssertion(() => page.Find($"#{RecipesPage.CardId(SoupId)} button[aria-expanded]"));
        page.Find($"#{RecipesPage.CardId(SoupId)} button[aria-expanded]").Click();
        page.WaitForAssertion(() => page.Find(".rc-ing-list input[type=checkbox]"));

        page.Find(".rc-ing-list input[type=checkbox]").Change(true);

        page.WaitForAssertion(() => Assert.Equal("done", page.Find(".rc-ing-list li").GetAttribute("class")));
    }

    [Fact]
    public async Task A_guest_sees_no_photo_or_file_surfaces_and_no_create()
    {
        var (ctx, _) = Create(PermissionClaims.RecipesRead, PermissionClaims.RecipeTagsRead);
        await using var _ctx = ctx;
        var page = ctx.Render<RecipesPage>();
        page.WaitForAssertion(() => page.Find($"#{RecipesPage.CardId(SoupId)} button[aria-expanded]"));
        page.Find($"#{RecipesPage.CardId(SoupId)} button[aria-expanded]").Click();
        page.WaitForAssertion(() => Assert.Contains("ripe tomatoes", page.Markup, StringComparison.Ordinal));

        Assert.DoesNotContain("No photos yet", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("original.pdf", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("New recipe", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("attach_file", page.Markup, StringComparison.Ordinal);
    }
}
