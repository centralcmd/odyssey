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

    private static (BunitContext Ctx, Mock<IRecipesApiClient> Api, IRenderedComponent<MudDialogProvider> Dialogs) Create(params string[] claims)
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
        var dialogs = ctx.Render<MudDialogProvider>();
        return (ctx, api, dialogs);
    }

    private static readonly string[] Owner =
    [
        PermissionClaims.RecipesRead, PermissionClaims.RecipesCreate, PermissionClaims.RecipesUpdate, PermissionClaims.RecipesDelete,
        PermissionClaims.RecipeTagsRead, PermissionClaims.PhotosRead, PermissionClaims.FilesRead,
    ];

    [Fact]
    public async Task The_card_head_reads_yield_times_and_counts()
    {
        var (ctx, _, dialogs) = Create(Owner);
        await using var _ctx = ctx;

        var page = ctx.Render<RecipesPage>();

        page.WaitForAssertion(() => Assert.Contains("Roasted tomato soup", page.Markup, StringComparison.Ordinal));
        Assert.Contains("Serves 4", page.Markup, StringComparison.Ordinal);
        Assert.Contains("Prep 15 min · Cook 50 min", page.Markup, StringComparison.Ordinal);
        Assert.Contains("Total 1 h 5 min", page.Markup, StringComparison.Ordinal);
        Assert.Contains("soup_kitchen", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("restaurant_menu", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_open_card_lists_grouped_lines_with_spoken_units_and_runs_the_step_numbers_on()
    {
        var (ctx, _, dialogs) = Create(Owner);
        await using var _ctx = ctx;
        var page = ctx.Render<RecipesPage>();
        page.WaitForAssertion(() => page.Find($"#{RecipesPage.CardId(SoupId)} button[aria-expanded]"));

        page.Find($"#{RecipesPage.CardId(SoupId)} button[aria-expanded]").Click();

        page.WaitForAssertion(() => Assert.Contains("ripe tomatoes", page.Markup, StringComparison.Ordinal));
        // A section is the shared divider — name · rule · count — as a level-4 heading.
        var divider = page.Find(".rc-ing-groups .odc-sectiondivider");
        Assert.Equal("Roasting tray", divider.QuerySelector(".odc-sectiondivider-l")!.TextContent);
        Assert.Equal("4", divider.QuerySelector("[role=heading]")!.GetAttribute("aria-level"));
        Assert.Equal("1 ingredient", divider.QuerySelector(".odc-sectiondivider-meta")!.TextContent);
        Assert.Equal(["Roast", "Pot"], page.FindAll(".rc-cook-card section[aria-labelledby$='-method'] .odc-sectiondivider-l").Select(d => d.TextContent));
        Assert.Equal(["1 step", "1 step"], page.FindAll(".rc-cook-card section[aria-labelledby$='-method'] .odc-sectiondivider-meta").Select(d => d.TextContent));
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
        var (ctx, _, dialogs) = Create(Owner);
        await using var _ctx = ctx;
        var page = ctx.Render<RecipesPage>();
        page.WaitForAssertion(() => page.Find($"#{RecipesPage.CardId(SoupId)} button[aria-expanded]"));
        page.Find($"#{RecipesPage.CardId(SoupId)} button[aria-expanded]").Click();
        page.WaitForAssertion(() => page.Find(".rc-ing-list input[type=checkbox]"));

        page.Find(".rc-ing-list input[type=checkbox]").Change(true);

        page.WaitForAssertion(() => Assert.Equal("done", page.Find(".rc-ing-list li").GetAttribute("class")));
    }

    [Fact]
    public async Task A_reader_without_photo_file_or_create_claims_sees_none_of_those_surfaces()
    {
        var (ctx, _, dialogs) = Create(PermissionClaims.RecipesRead, PermissionClaims.RecipeTagsRead);
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
    private static IRenderedComponent<RecipesPage> RenderOpen(BunitContext ctx)
    {
        var page = ctx.Render<RecipesPage>();
        page.WaitForAssertion(() => page.Find($"#{RecipesPage.CardId(SoupId)} button[aria-expanded]"));
        page.Find($"#{RecipesPage.CardId(SoupId)} button[aria-expanded]").Click();
        return page;
    }

    private static string Region(IRenderedComponent<RecipesPage> page) => page.Find("[role=status].sr-only").TextContent;

    private static Task Choose(IRenderedComponent<RecipesPage> page, string label) =>
        page.InvokeAsync(() => page.Instance.RowActions(Summary()).Single(i => i.Label == label).OnClick.InvokeAsync());

    [Fact]
    public async Task An_owner_sees_new_recipe()
    {
        var (ctx, _, dialogs) = Create(Owner);
        await using var _ctx = ctx;
        var page = ctx.Render<RecipesPage>();

        page.WaitForAssertion(() => Assert.Contains("New recipe", page.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_list_load_shows_the_error_state_not_the_empty_one()
    {
        var (ctx, api, dialogs) = Create(Owner);
        await using var _ctx = ctx;
        api.Setup(a => a.ListAsync(It.IsAny<RecipeListFilter?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<PagedResult<RecipeSummary>>.Failure(HttpStatusCode.InternalServerError, new ApiProblem { Status = 500, Title = "Boom" }));

        var page = ctx.Render<RecipesPage>();

        page.WaitForAssertion(() => Assert.Contains("Something went wrong reaching Odyssey.", page.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("No recipes yet", page.Markup, StringComparison.Ordinal);
        page.WaitForAssertion(() => Assert.Equal("Recipes couldn’t be loaded.", Region(page)));
    }

    [Fact]
    public async Task A_card_whose_recipe_is_gone_says_so()
    {
        var (ctx, api, dialogs) = Create(Owner);
        await using var _ctx = ctx;
        api.Setup(a => a.GetAsync(SoupId, It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingRecipe>.Failure(HttpStatusCode.NotFound, new ApiProblem { Status = 404 }));

        var page = RenderOpen(ctx);

        page.WaitForAssertion(() => Assert.Contains("This recipe no longer exists", page.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("Change servings…", page.Instance.RowActions(Summary()).Select(i => i.Label));
    }

    [Fact]
    public async Task A_card_whose_read_fails_offers_try_again()
    {
        var (ctx, api, dialogs) = Create(Owner);
        await using var _ctx = ctx;
        api.Setup(a => a.GetAsync(SoupId, It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingRecipe>.Failure(HttpStatusCode.InternalServerError, new ApiProblem { Status = 500 }));

        var page = RenderOpen(ctx);

        page.WaitForAssertion(() => Assert.Contains("This recipe couldn’t be loaded", page.Markup, StringComparison.Ordinal));
        Assert.Contains("Try again", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Change_servings_rereads_with_servings_and_shows_the_scaled_yield()
    {
        var (ctx, api, dialogs) = Create(Owner);
        await using var _ctx = ctx;
        var page = RenderOpen(ctx);
        page.WaitForAssertion(() => Assert.Contains("ripe tomatoes", page.Markup, StringComparison.Ordinal));

        await page.InvokeAsync(() => page.Instance.ScaleAsync(SoupId, 8));

        api.Verify(a => a.GetAsync(SoupId, 8, It.IsAny<CancellationToken>()), Times.Once);
        page.WaitForAssertion(() => Assert.Contains("Serves 8 (written for 4)", page.Markup, StringComparison.Ordinal));
        Assert.Contains("scaled from 4", page.Markup, StringComparison.Ordinal);
        Assert.Contains("2,400", page.Markup, StringComparison.Ordinal);
        page.WaitForAssertion(() => Assert.Equal("Amounts scaled to 8.", Region(page)));
    }

    [Fact]
    public async Task A_failed_scale_keeps_the_amounts_and_try_again_repeats_it()
    {
        var (ctx, api, dialogs) = Create(Owner);
        await using var _ctx = ctx;
        api.Setup(a => a.GetAsync(SoupId, 8, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingRecipe>.Failure(HttpStatusCode.InternalServerError, new ApiProblem { Status = 500 }));
        var page = RenderOpen(ctx);
        page.WaitForAssertion(() => Assert.Contains("ripe tomatoes", page.Markup, StringComparison.Ordinal));

        await page.InvokeAsync(() => page.Instance.ScaleAsync(SoupId, 8));

        page.WaitForAssertion(() => Assert.Contains("The amounts couldn’t be scaled. They show for 4 as before.", page.Markup, StringComparison.Ordinal));
        Assert.Contains("1,200", page.Markup, StringComparison.Ordinal);
        page.FindAll("button").Single(b => b.TextContent.Trim() == "Try again").Click();
        page.WaitForAssertion(() => api.Verify(a => a.GetAsync(SoupId, 8, It.IsAny<CancellationToken>()), Times.Exactly(2)));
    }

    [Fact]
    public async Task Clear_checks_appears_only_once_something_is_checked()
    {
        var (ctx, _, dialogs) = Create(Owner);
        await using var _ctx = ctx;
        var page = RenderOpen(ctx);
        page.WaitForAssertion(() => page.Find(".rc-ing-list input[type=checkbox]"));
        Assert.DoesNotContain(page.Instance.RowActions(Summary()), i => i.Label?.StartsWith("Clear checks", StringComparison.Ordinal) == true);

        page.Find(".rc-ing-list input[type=checkbox]").Change(true);

        page.WaitForAssertion(() => Assert.Contains("Clear checks (1)", page.Instance.RowActions(Summary()).Select(i => i.Label)));
        await Choose(page, "Clear checks (1)");
        page.WaitForAssertion(() => Assert.Null(page.Find(".rc-ing-list li").GetAttribute("class")));
    }

    [Fact]
    public async Task An_archive_that_loses_a_race_rereads_the_card_and_says_so()
    {
        var (ctx, api, dialogs) = Create(Owner);
        await using var _ctx = ctx;
        api.Setup(a => a.UpdateAsync(SoupId, It.IsAny<UpdateRecipe>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingRecipe>.Failure(HttpStatusCode.Conflict,
                new ApiProblem { Status = 409, Code = RecipeErrorCodes.VersionConflict }));
        var page = RenderOpen(ctx);
        page.WaitForAssertion(() => Assert.Contains("ripe tomatoes", page.Markup, StringComparison.Ordinal));
        api.Invocations.Clear();

        await Choose(page, "Archive");

        // The fresh read before the PUT, then the re-read after the 409.
        page.WaitForAssertion(() => api.Verify(a => a.GetAsync(SoupId, null, It.IsAny<CancellationToken>()), Times.Exactly(2)));
        api.Verify(a => a.UpdateAsync(SoupId, It.Is<UpdateRecipe>(u => u.Archived && u.Version == 2 && u.PhotoIds == null), It.IsAny<CancellationToken>()), Times.Once);
        page.WaitForAssertion(() => Assert.StartsWith("Someone else changed this recipe just now.", Region(page), StringComparison.Ordinal));
        Assert.DoesNotContain("Archived", page.Find($"#{RecipesPage.CardId(SoupId)}").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_delete_that_finds_the_recipe_already_gone_is_a_success()
    {
        var (ctx, api, dialogs) = Create(Owner);
        await using var _ctx = ctx;
        api.Setup(a => a.DeleteAsync(SoupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Failure(HttpStatusCode.NotFound, new ApiProblem { Status = 404 }));
        var page = ctx.Render<RecipesPage>();
        page.WaitForAssertion(() => page.Find($"#{RecipesPage.CardId(SoupId)}"));

        await Choose(page, "Delete…");
        page.WaitForAssertion(() => Assert.Contains("Delete Roasted tomato soup?", dialogs.Markup, StringComparison.Ordinal));
        dialogs.FindAll("button").Single(b => b.TextContent.Trim().EndsWith("Delete recipe", StringComparison.Ordinal)).Click();

        page.WaitForAssertion(() => Assert.Empty(page.FindAll($"#{RecipesPage.CardId(SoupId)}")));
        page.WaitForAssertion(() => Assert.Equal("Recipe Roasted tomato soup deleted.", Region(page)));
    }

    [Fact]
    public async Task A_delete_that_loses_a_race_keeps_the_dialog_open_and_says_why()
    {
        var (ctx, api, dialogs) = Create(Owner);
        await using var _ctx = ctx;
        api.Setup(a => a.DeleteAsync(SoupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Failure(HttpStatusCode.Conflict, new ApiProblem { Status = 409, Code = RecipeErrorCodes.VersionConflict }));
        var page = ctx.Render<RecipesPage>();
        page.WaitForAssertion(() => page.Find($"#{RecipesPage.CardId(SoupId)}"));

        await Choose(page, "Delete…");
        page.WaitForAssertion(() => Assert.Contains("Delete Roasted tomato soup?", dialogs.Markup, StringComparison.Ordinal));
        dialogs.FindAll("button").Single(b => b.TextContent.Trim().EndsWith("Delete recipe", StringComparison.Ordinal)).Click();

        page.WaitForAssertion(() => Assert.Contains("Someone else changed this recipe just now.", dialogs.Markup, StringComparison.Ordinal));
        Assert.NotEmpty(page.FindAll($"#{RecipesPage.CardId(SoupId)}"));
    }

    [Fact]
    public async Task The_photo_grid_is_the_library_tile_grid_with_cover_and_archived_badges()
    {
        await using var ctx = PageTestContext.Create(PermissionClaims.PhotosRead, PermissionClaims.PhotosUpdate);
        var bowl = Guid.NewGuid();
        var tray = Guid.NewGuid();
        var photosApi = new Mock<IPhotosApiClient>();
        photosApi.Setup(a => a.GetAsync(tray, It.IsAny<CancellationToken>())).ReturnsAsync(new Odyssey.Dtos.Journal.ExistingPhoto
        {
            PhotoId = tray,
            Title = "Tray",
            Archived = DateTime.UtcNow,
            Favourited = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        ctx.Services.AddSingleton(photosApi.Object);
        ctx.Render<MudPopoverProvider>();
        ctx.Render<MudDialogProvider>();
        IReadOnlyList<RecipePhotoDto> photos =
        [
            new() { RecipePhotoId = Guid.NewGuid(), PhotoId = bowl, Position = 0, Title = "Bowl", ThumbnailsReady = true },
            new() { RecipePhotoId = Guid.NewGuid(), PhotoId = tray, Position = 1, Title = "Tray" },
        ];

        var cut = ctx.Render<RecipePhotoGrid>(p => p
            .Add(x => x.Photos, photos)
            .Add(x => x.Access, new Odyssey.Client.Services.PhotoImageAccess(true))
            .Add(x => x.CanUpdate, true));

        var grid = cut.Find("ul.odc-photogrid2");
        Assert.Contains("132px", grid.GetAttribute("style"), StringComparison.Ordinal);
        Assert.Equal("2 photos", grid.GetAttribute("aria-label"));
        var tiles = cut.FindAll(".odc-phototile");
        Assert.Equal(2, tiles.Count);
        Assert.Equal(["Cover"], tiles[0].QuerySelectorAll(".odc-phototile-badge").Select(b => b.TextContent));
        cut.WaitForAssertion(() => Assert.Equal(["Archived"], cut.FindAll(".odc-phototile")[1].QuerySelectorAll(".odc-phototile-badge").Select(b => b.TextContent)));
        // The heart is the library favourite.
        Assert.Equal("true", cut.Find("button[aria-label='Unfavourite Tray']").GetAttribute("aria-pressed"));
        Assert.NotNull(cut.Find("button[aria-label='Favourite Bowl']"));
        Assert.NotNull(cut.Find("button[aria-label='Open Bowl']"));
        Assert.NotNull(cut.Find("button[aria-label='Actions for photo: Bowl']"));
        Assert.Equal(RecipePhotoGrid.TileId(bowl), tiles[0].ParentElement!.Id);
        Assert.Empty(cut.FindAll(".rc-images"));
    }

    [Fact]
    public async Task The_open_card_puts_description_above_notes_leads_each_card_with_its_icon_and_labels_a_step_by_its_text()
    {
        var (ctx, api, dialogs) = Create(Owner);
        await using var _ctx = ctx;
        api.Setup(a => a.GetAsync(SoupId, It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingRecipe>.Success(Detail() with
            {
                Description = "A summer soup.",
                Notes = "Freeze half.",
                UpdatedAt = new DateTime(2026, 4, 1, 9, 0, 0, DateTimeKind.Utc),
                UpdatedByName = "Kari",
                Ingredients =
                [
                    new() { RecipeIngredientId = Guid.NewGuid(), Position = 0, Amount = 1, Unit = IngredientUnit.Gram, Name = "cream", Optional = true },
                    new() { RecipeIngredientId = Guid.NewGuid(), Position = 1, Unit = IngredientUnit.ToTaste, Name = "salt" },
                ],
            }, HttpStatusCode.OK));
        var page = RenderOpen(ctx);
        page.WaitForAssertion(() => Assert.Contains("A summer soup.", page.Markup, StringComparison.Ordinal));

        var labels = page.FindAll(".odc-infotile-label, .odc-infotile .odc-infotile-l").Select(l => l.TextContent.Trim()).ToList();
        var tileText = page.FindAll(".odc-infotile").Select(t => t.TextContent).ToList();
        var description = tileText.FindIndex(t => t.Contains("A summer soup.", StringComparison.Ordinal));
        var notes = tileText.FindIndex(t => t.Contains("Freeze half.", StringComparison.Ordinal));
        var updated = tileText.FindIndex(t => t.Contains("Kari", StringComparison.Ordinal));
        Assert.Equal(description + 1, notes);
        Assert.True(updated < description);

        Assert.Equal(["egg_alt", "menu_book"], page.FindAll(".rc-card-h .rc-card-ic").Select(i => i.TextContent));
        Assert.All(page.FindAll(".rc-card-h .rc-card-ic"), i => Assert.Equal("true", i.GetAttribute("aria-hidden")));

        var cream = page.FindAll(".rc-ing-list li")[0];
        Assert.Equal(" (optional)", cream.QuerySelector(".rc-ing-optional")!.TextContent);
        Assert.Equal("rc-ing-name", cream.QuerySelector(".rc-ing-optional")!.PreviousElementSibling!.ClassName);
        Assert.Empty(page.FindAll(".rc-cook .rc-ing-opt"));
        Assert.Equal(", to taste", page.Find(".rc-ing-totaste").TextContent);

        var step = page.Find(".rc-step-list li .odc-check-label");
        Assert.Equal("Step 1 of 2: ", step.QuerySelector(".odc-sr-only")!.TextContent);
        Assert.Equal("Step 1 of 2: Roast.", step.TextContent);
        Assert.Empty(page.FindAll(".rc-step-n"));
        Assert.DoesNotContain("aria-describedby", page.Find(".rc-step-list li input").OuterHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_files_row_menu_is_preview_download_copy_id_then_detach()
    {
        await using var ctx = PageTestContext.Create(Owner);
        var api = new Mock<IRecipesApiClient>();
        api.Setup(a => a.ListAsync(It.IsAny<RecipeListFilter?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<PagedResult<RecipeSummary>>.Success(
                new PagedResult<RecipeSummary> { Items = [Summary()], Offset = 0, Limit = 25, TotalCount = 1 }, HttpStatusCode.OK));
        api.Setup(a => a.GetAsync(SoupId, It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingRecipe>.Success(Detail(), HttpStatusCode.OK));
        ctx.Services.AddSingleton(api.Object);
        var popovers = ctx.Render<MudPopoverProvider>();
        ctx.Render<MudDialogProvider>();
        var page = RenderOpen(ctx);
        page.WaitForAssertion(() => Assert.Contains("original.pdf", page.Markup, StringComparison.Ordinal));

        page.Find(".con-files button[aria-label='Row actions']").Click();

        popovers.WaitForAssertion(() => Assert.NotEmpty(popovers.FindAll("[role=menuitem]")));
        var items = popovers.FindAll("[role=menuitem]").Select(i => i.TextContent).ToList();
        Assert.Equal(4, items.Count);
        Assert.Contains("Preview", items[0], StringComparison.Ordinal);
        Assert.Contains("Download", items[1], StringComparison.Ordinal);
        Assert.Contains("Copy ID", items[2], StringComparison.Ordinal);
        Assert.Contains("Detach", items[3], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attach_files_is_in_the_record_menu_until_the_servers_attachment_cap()
    {
        var (ctx, api, dialogs) = Create(Owner);
        await using var _ctx = ctx;
        var page = RenderOpen(ctx);
        page.WaitForAssertion(() => Assert.Contains("original.pdf", page.Markup, StringComparison.Ordinal));
        Assert.Contains("Attach files", page.Instance.RowActions(Summary()).Select(i => i.Label));

        var full = Detail() with
        {
            Attachments = [.. Enumerable.Range(0, RecipeLimits.MaxAttachments).Select(i => new RecipeAttachmentDto
            {
                RecipeAttachmentId = Guid.NewGuid(),
                FileId = Guid.NewGuid(),
                FileName = $"f{i}.pdf",
                ContentType = "application/pdf",
                CreatedAt = DateTime.UtcNow,
            })],
        };
        api.Setup(a => a.GetAsync(SoupId, It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingRecipe>.Success(full, HttpStatusCode.OK));
        page.Find($"#{RecipesPage.CardId(SoupId)} button[aria-expanded]").Click();
        page.Find($"#{RecipesPage.CardId(SoupId)} button[aria-expanded]").Click();

        page.WaitForAssertion(() => Assert.DoesNotContain("Attach files", page.Instance.RowActions(Summary()).Select(i => i.Label)));
    }

    [Fact]
    public void A_new_recipe_is_inserted_locally_only_under_a_newest_first_sort_without_filters()
    {
        Assert.False(RecipesPage.CreateNeedsRefetch(false, new("updated", Odyssey.Client.Components.OdsSortDirection.Desc)));
        Assert.False(RecipesPage.CreateNeedsRefetch(false, new("created", Odyssey.Client.Components.OdsSortDirection.Desc)));
        Assert.True(RecipesPage.CreateNeedsRefetch(false, new("title", Odyssey.Client.Components.OdsSortDirection.Asc)));
        Assert.True(RecipesPage.CreateNeedsRefetch(false, new("created", Odyssey.Client.Components.OdsSortDirection.Asc)));
        Assert.True(RecipesPage.CreateNeedsRefetch(true, new("updated", Odyssey.Client.Components.OdsSortDirection.Desc)));
    }

    [Fact]
    public void The_sort_fields_carry_no_client_projection_and_map_onto_the_server_sort()
    {
        Assert.All(RecipesPage.SortFields, f => Assert.Null(f.SortValue));
        Assert.Equal(
            [RecipeSortBy.Title, RecipeSortBy.UpdatedAt, RecipeSortBy.CreatedAt, RecipeSortBy.TotalMinutes],
            RecipesPage.SortFields.Select(f => RecipeFilters.SortByFor(f.Key)));
    }

    [Fact]
    public void A_lost_wake_lock_is_handled_in_js_and_reported_back()
    {
        var js = File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "js", "wake-lock.js"));
        Assert.Contains("visibilitychange", js, StringComparison.Ordinal);
        Assert.Contains("addEventListener('release'", js, StringComparison.Ordinal);
        Assert.Contains("invokeMethodAsync('OnWakeLockLost')", js, StringComparison.Ordinal);
        Assert.Contains("removeEventListener('visibilitychange'", js, StringComparison.Ordinal);
        Assert.NotNull(typeof(RecipesPage).GetMethod(nameof(RecipesPage.OnWakeLockLost))!
            .GetCustomAttributes(typeof(Microsoft.JSInterop.JSInvokableAttribute), false).SingleOrDefault());
    }

    [Fact]
    public async Task A_lost_wake_lock_drops_the_chip_and_says_so()
    {
        var (ctx, _, dialogs) = Create(Owner);
        await using var _ctx = ctx;
        var page = RenderOpen(ctx);
        page.WaitForAssertion(() => Assert.Contains("ripe tomatoes", page.Markup, StringComparison.Ordinal));
        var awake = typeof(RecipesPage).GetField("_awakeId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await page.InvokeAsync(() => { awake.SetValue(page.Instance, (Guid?)SoupId); page.Render(); });
        page.WaitForAssertion(() => Assert.Contains("Screen stays awake", page.Markup, StringComparison.Ordinal));

        await page.Instance.OnWakeLockLost();

        page.WaitForAssertion(() => Assert.DoesNotContain("Screen stays awake", page.Markup, StringComparison.Ordinal));
        page.WaitForAssertion(() => Assert.Equal(RecipesPage.WakeLockRefused, Region(page)));
    }
}
