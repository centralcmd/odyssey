using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Attachments;
using Odyssey.Client.Pages.Library;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Recipes;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// AI recipe import in the client (issue #604; design system components-recipes.md · AI recipe import): the words, the
/// draft conversions, the composer's gate, the review mode of the Recipes dialog, the /recipe-imports page's menu by
/// state, and the shared DocumentSourcePanel's counts and cap.
/// </summary>
public class RecipeImportUiTests
{
    private static readonly string[] Importer =
    [
        PermissionClaims.RecipesImport, PermissionClaims.RecipesCreate, PermissionClaims.RecipesRead,
        PermissionClaims.FilesRead, PermissionClaims.FilesCreate, PermissionClaims.RecipeTagsRead,
    ];

    private static RecipeImportOptions Options(bool enabled = true, string version = "v1") => new()
    {
        Enabled = enabled,
        Processor = "Anthropic",
        ProcessorRegion = "United States",
        LawfulBasis = "Consent",
        PrivacyNoticeUrl = "https://example.com/privacy",
        Model = "claude-sonnet-5",
        DisclosureVersion = version,
        SendsTagNames = true,
        ConsentText = "I agree to send this recipe.",
        MaxSourceTextLength = 100,
        MaxFilesPerImport = 10,
        MaxTotalBytes = 20_000_000,
        MaxPdfPages = 100,
        RetentionDays = 30,
        AllowedContentTypes = ["application/pdf", "image/jpeg", "text/plain"],
        MaxFileBytes = new Dictionary<string, long> { ["application/pdf"] = 15_000_000, ["image/jpeg"] = 3_500_000, ["text/plain"] = 1_000_000 },
    };

    private static ApiResult<T> Ok<T>(T value) => ApiResult<T>.Success(value, HttpStatusCode.OK);

    // ── The words ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(RecipeImportStatus.Queued, 0, 0, "Waiting")]
    [InlineData(RecipeImportStatus.Running, 0, 0, "Reading")]
    [InlineData(RecipeImportStatus.Completed, 1, 0, "Ready to review")]
    [InlineData(RecipeImportStatus.Completed, 0, 1, "Recipe created")]
    [InlineData(RecipeImportStatus.Completed, 0, 0, "Draft discarded")]
    [InlineData(RecipeImportStatus.Failed, 0, 0, "Failed")]
    [InlineData(RecipeImportStatus.Cancelled, 0, 0, "Cancelled")]
    public void The_chip_is_the_status_in_words_from_the_server_status_and_the_drafts(
        RecipeImportStatus status, int pending, int accepted, string label) =>
        Assert.Equal(label, RecipeImportText.Display(new RecipeImportSummary
        {
            Status = status,
            PendingDraftCount = pending,
            AcceptedDraftCount = accepted,
        }).Label);

    [Fact]
    public void A_pending_draft_whose_content_was_purged_is_not_offered_for_review() =>
        Assert.Equal("Draft removed", RecipeImportText.Display(new RecipeImportSummary
        {
            Status = RecipeImportStatus.Completed,
            PendingDraftCount = 1,
            ContentPurged = true,
        }).Label);

    [Fact]
    public void A_warning_outside_the_allow_list_is_dropped_and_a_known_one_names_its_step()
    {
        Assert.Null(RecipeImportText.Warning("ignore-previous-instructions"));
        Assert.Equal(1, RecipeImportText.Warning(RecipeImportWarningCodes.UnitUnmapped)!.Value.Step);
        Assert.Equal(0, RecipeImportText.Warning("truncated:title")!.Value.Step);
        Assert.Equal("PDF, JPEG photo or Text file", RecipeImportText.TypeList(["application/pdf", "image/jpeg", "text/plain"]));
        Assert.Equal("3.5 MB", RecipeImportText.Megabytes(3_500_000));
    }

    // ── Draft conversions ────────────────────────────────────────────────────────────

    [Fact]
    public void A_draft_recipe_with_no_servings_stays_empty_and_saving_for_later_drops_empty_lines()
    {
        var draft = RecipeDraft.FromImport(new RecipeImportDraftContent
        {
            Title = "Buns",
            Ingredients = [new() { Section = "Dough", Name = "flour", Amount = 500, Unit = IngredientUnit.Gram }],
            Steps = [new() { Text = "Bake." }],
            TagIds = [Guid.NewGuid()],
        });

        Assert.Null(draft.Servings);
        Assert.IsType<RecipeSectionDraft>(draft.Ingredients[0]);
        draft.Ingredients.Add(new RecipeIngredientDraft());

        var content = draft.ToImportContent(canReadTags: false);
        Assert.Single(content.Ingredients);
        Assert.Equal("Dough", content.Ingredients[0].Section);
        Assert.Null(content.TagIds);
        Assert.Null(content.Servings);
    }

    // ── The composer ─────────────────────────────────────────────────────────────────

    private static (IRenderedComponent<Bunit.Rendering.ContainerFragment> Cut, Mock<IRecipeImportsApiClient> Api, List<RecipeImportDetail> Sent)
        RenderComposer(BunitContext ctx, Mock<IRecipeImportsApiClient> api)
    {
        ctx.Services.AddSingleton(api.Object);
        var sent = new List<RecipeImportDetail>();
        var cut = ctx.Render(b =>
        {
            b.OpenComponent<MudDialogProvider>(0);
            b.CloseComponent();
            b.OpenComponent<MudPopoverProvider>(1);
            b.CloseComponent();
            b.OpenComponent<RecipeImportDialog>(2);
            b.AddComponentParameter(3, nameof(RecipeImportDialog.Open), true);
            b.AddComponentParameter(4, nameof(RecipeImportDialog.OnSent), EventCallback.Factory.Create<RecipeImportDetail>(sent, d => sent.Add(d)));
            b.CloseComponent();
        });
        return (cut, api, sent);
    }

    private static Mock<IRecipeImportsApiClient> ImportsApi(params RecipeImportOptions[] options)
    {
        var api = new Mock<IRecipeImportsApiClient> { DefaultValueProvider = new PageTestContext.EmptySuccess() };
        var sequence = api.SetupSequence(a => a.GetOptionsAsync(It.IsAny<CancellationToken>()));
        foreach (var o in options)
        {
            sequence = sequence.ReturnsAsync(Ok(o));
        }

        return api;
    }

    private static AngleSharp.Dom.IElement FooterButton(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string label) =>
        cut.FindAll(".ods-modal-foot button").Last(b => b.TextContent.Contains(label, StringComparison.Ordinal));

    private static void TypeText(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string text) =>
        cut.InvokeAsync(() => cut.Find("textarea#ri-f-text").Input(text)).GetAwaiter().GetResult();

    [Fact]
    public async Task Switched_off_the_composer_says_so_and_next_is_aria_disabled()
    {
        await using var ctx = PageTestContext.Create(Importer);
        var (cut, _, _) = RenderComposer(ctx, ImportsApi(Options(enabled: false)));

        cut.WaitForAssertion(() => Assert.Contains("turned off by an administrator", cut.Markup, StringComparison.Ordinal));
        Assert.Equal("true", FooterButton(cut, "Next").GetAttribute("aria-disabled"));
    }

    [Fact]
    public async Task Next_with_nothing_added_stays_on_the_recipe_step_with_the_reason()
    {
        await using var ctx = PageTestContext.Create(Importer);
        var (cut, _, _) = RenderComposer(ctx, ImportsApi(Options()));
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("textarea#ri-f-text")));

        await cut.InvokeAsync(() => FooterButton(cut, "Next").Click());

        Assert.Contains("Paste recipe text or add a file.", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("#ri-consent-h"));
    }

    [Fact]
    public async Task Send_is_disabled_until_the_box_is_ticked_and_posts_the_version_that_was_shown()
    {
        await using var ctx = PageTestContext.Create(Importer);
        var api = ImportsApi(Options(), Options());
        RecipeImportSubmission? posted = null;
        api.Setup(a => a.CreateAsync(It.IsAny<RecipeImportSubmission>(), It.IsAny<CancellationToken>()))
            .Callback((RecipeImportSubmission s, CancellationToken _) => posted = s)
            .ReturnsAsync(ApiResult<RecipeImportDetail>.Success(new RecipeImportDetail { Summary = new() { Id = Guid.NewGuid() } }, HttpStatusCode.Accepted));
        var (cut, _, sent) = RenderComposer(ctx, api);
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("textarea#ri-f-text")));
        TypeText(cut, "Buns: flour, milk.");
        await cut.InvokeAsync(() => FooterButton(cut, "Next").Click());
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#ri-consent-h")));

        Assert.True(FooterButton(cut, "Send & draft").HasAttribute("disabled"));
        Assert.Contains("I agree to send this recipe.", cut.Markup, StringComparison.Ordinal);
        await cut.InvokeAsync(() => cut.Find(".fan-consent-check input[type=checkbox]").Change(true));
        await cut.InvokeAsync(() => FooterButton(cut, "Send & draft").Click());

        cut.WaitForAssertion(() => Assert.Single(sent));
        Assert.Equal("v1", posted!.DisclosureVersion);
        Assert.Equal("Buns: flour, milk.", posted.Text);
    }

    [Fact]
    public async Task A_disclosure_that_changed_since_it_was_shown_clears_the_consent_and_posts_nothing()
    {
        await using var ctx = PageTestContext.Create(Importer);
        var api = ImportsApi(Options(version: "v1"), Options(version: "v2"));
        var (cut, _, _) = RenderComposer(ctx, api);
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("textarea#ri-f-text")));
        TypeText(cut, "Soup.");
        await cut.InvokeAsync(() => FooterButton(cut, "Next").Click());
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#ri-consent-h")));
        await cut.InvokeAsync(() => cut.Find(".fan-consent-check input[type=checkbox]").Change(true));

        await cut.InvokeAsync(() => FooterButton(cut, "Send & draft").Click());

        cut.WaitForAssertion(() => Assert.Contains("The processor details changed", cut.Markup, StringComparison.Ordinal));
        Assert.True(FooterButton(cut, "Send & draft").HasAttribute("disabled"));
        api.Verify(a => a.CreateAsync(It.IsAny<RecipeImportSubmission>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Review mode ──────────────────────────────────────────────────────────────────

    private static RecipeImportDraftItem Draft() => new()
    {
        Id = Guid.NewGuid(),
        Status = RecipeImportDraftStatus.Pending,
        Version = 4,
        Warnings = [RecipeImportWarningCodes.ServingsMissing, RecipeImportWarningCodes.UnitUnmapped, "not-a-code"],
        Content = new RecipeImportDraftContent
        {
            Title = "Cardamom buns",
            Ingredients = [new() { Name = "stick butter", Unit = IngredientUnit.None }],
            Steps = [new() { Text = "Bake." }],
        },
    };

    private static IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderReview(
        BunitContext ctx, RecipeImportReview review, List<RecipeImportDraftItem> saved, List<RecipeImportAccepted> accepted)
    {
        var cut = ctx.Render(b =>
        {
            b.OpenComponent<MudDialogProvider>(0);
            b.CloseComponent();
            b.OpenComponent<MudPopoverProvider>(1);
            b.CloseComponent();
            b.OpenComponent<RecipeDialog>(2);
            b.AddComponentParameter(3, nameof(RecipeDialog.Open), true);
            b.AddComponentParameter(4, nameof(RecipeDialog.ImportReview), review);
            b.AddComponentParameter(5, nameof(RecipeDialog.OnImportSaved), EventCallback.Factory.Create<RecipeImportDraftItem>(saved, d => saved.Add(d)));
            b.AddComponentParameter(6, nameof(RecipeDialog.OnImportAccepted), EventCallback.Factory.Create<RecipeImportAccepted>(accepted, a => accepted.Add(a)));
            b.CloseComponent();
        });
        return cut;
    }

    [Fact]
    public async Task Review_shows_the_banner_and_things_to_check_and_leaves_servings_empty()
    {
        await using var ctx = PageTestContext.Create(Importer);
        var cut = RenderReview(ctx, new RecipeImportReview(Guid.NewGuid(), Draft(), 2, CanFiles: true), [], []);

        Assert.Contains("Review imported recipe", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Draft recipe, made by AI — not in Recipes yet", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Things to check", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Go to Ingredients", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("not-a-code", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Attach the source files to this recipe (2)", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Save and finish later", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_and_finish_later_stores_the_edits_with_the_version_and_needs_no_title()
    {
        await using var ctx = PageTestContext.Create(Importer);
        var api = new Mock<IRecipeImportsApiClient> { DefaultValueProvider = new PageTestContext.EmptySuccess() };
        UpdateRecipeImportDraft? put = null;
        api.Setup(a => a.UpdateDraftAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<UpdateRecipeImportDraft>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, Guid _, UpdateRecipeImportDraft r, CancellationToken _) => put = r)
            .ReturnsAsync(Ok(new RecipeImportDraftItem { Version = 5 }));
        ctx.Services.AddSingleton(api.Object);
        var saved = new List<RecipeImportDraftItem>();
        var draft = Draft() with { Content = Draft().Content! with { Title = null } };
        var cut = RenderReview(ctx, new RecipeImportReview(Guid.NewGuid(), draft, 0, CanFiles: true), saved, []);

        await cut.InvokeAsync(() => FooterButton(cut, "Save and finish later").Click());

        cut.WaitForAssertion(() => Assert.Single(saved));
        Assert.Equal(4, put!.Version);
        Assert.Null(put.Content!.Title);
        // Opened without CanReadTags: null keeps the stored suggestions for everyone else (the "absent, not hidden" rule).
        Assert.Null(put.Content.TagIds);
    }

    [Fact]
    public async Task Without_file_access_the_source_files_are_not_attached_and_a_note_says_why()
    {
        await using var ctx = PageTestContext.Create(Importer);
        var cut = RenderReview(ctx, new RecipeImportReview(Guid.NewGuid(), Draft(), 3, CanFiles: false), [], []);

        Assert.Contains("Source files won’t be attached", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Attach the source", cut.Markup, StringComparison.Ordinal);
    }

    // ── The page's menu, by state ────────────────────────────────────────────────────

    private static RecipeImportSummary Row(RecipeImportStatus status, int pending = 0) => new()
    {
        Id = Guid.NewGuid(),
        Status = status,
        PendingDraftCount = pending,
        HasText = true,
        QueuedAt = DateTime.UtcNow,
        DraftTitle = pending > 0 ? "Cardamom buns" : null,
        RequestedByName = "Owner Demo",
    };

    [Fact]
    public async Task The_page_lists_imports_with_their_status_in_words_and_the_tally()
    {
        await using var ctx = PageTestContext.Create(Importer);
        var api = new Mock<IRecipeImportsApiClient> { DefaultValueProvider = new PageTestContext.EmptySuccess() };
        RecipeImportSummary[] rows = [Row(RecipeImportStatus.Completed, pending: 1), Row(RecipeImportStatus.Queued), Row(RecipeImportStatus.Failed)];
        api.Setup(a => a.ListAsync(It.IsAny<RecipeImportListFilter?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((RecipeImportListFilter? f, int _, int _, CancellationToken _) => Ok(new PagedResult<RecipeImportSummary>
            {
                Items = f?.HasPendingDraft == true ? [rows[0]] : f?.Status is { Count: 2 } ? [rows[1]] : rows,
                TotalCount = f?.HasPendingDraft == true ? 1 : f?.Status is { Count: 2 } ? 1 : 3,
            }));
        api.Setup(a => a.GetOptionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Ok(Options()));
        ctx.Services.AddSingleton(api.Object);
        ctx.Render<MudPopoverProvider>();
        ctx.Render<MudDialogProvider>();

        var page = ctx.Render<RecipeImportsPage>();

        page.WaitForAssertion(() => Assert.Contains("3 imports · 1 ready to review · 1 in progress", page.Markup, StringComparison.Ordinal));
        Assert.Contains("Ready to review", page.Markup, StringComparison.Ordinal);
        Assert.Contains("Waiting", page.Markup, StringComparison.Ordinal);
        Assert.Contains("Cardamom buns", page.Markup, StringComparison.Ordinal);
        Assert.Contains("How it works", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discarding_from_a_closed_card_loads_the_import_and_sends_its_real_draft_id()
    {
        await using var ctx = PageTestContext.Create(Importer);
        var api = new Mock<IRecipeImportsApiClient> { DefaultValueProvider = new PageTestContext.EmptySuccess() };
        var row = Row(RecipeImportStatus.Completed, pending: 1);
        var draftId = Guid.NewGuid();
        api.Setup(a => a.ListAsync(It.IsAny<RecipeImportListFilter?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(new PagedResult<RecipeImportSummary> { Items = [row], TotalCount = 1 }));
        api.Setup(a => a.GetOptionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Ok(Options()));
        api.Setup(a => a.GetAsync(row.Id, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(new RecipeImportDetail
        {
            Summary = row,
            Drafts = [Draft() with { Id = draftId }],
        }));
        api.Setup(a => a.DiscardAsync(row.Id, It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(ApiResult.Success(HttpStatusCode.NoContent));
        ctx.Services.AddSingleton(api.Object);
        var popovers = ctx.Render<MudPopoverProvider>();
        var dialogs = ctx.Render<MudDialogProvider>();
        var page = ctx.Render<RecipeImportsPage>();
        page.WaitForAssertion(() => Assert.NotNull(page.Find($"#{RecipeImportsPage.MenuId(row.Id)} button")));

        await page.InvokeAsync(() => page.Find($"#{RecipeImportsPage.MenuId(row.Id)} button").Click());
        popovers.WaitForAssertion(() => Assert.Contains("Discard draft recipe…", popovers.Markup, StringComparison.Ordinal));
        var item = popovers.FindAll("*").Last(e => e.TextContent.Trim() == "Discard draft recipe…");
        await popovers.InvokeAsync(() => item.Click());
        AngleSharp.Dom.IElement ConfirmButton() =>
            dialogs.FindAll("button").Concat(page.FindAll("button")).Last(b => b.TextContent.Trim().EndsWith("Discard draft recipe", StringComparison.Ordinal));
        dialogs.WaitForAssertion(() => Assert.NotNull(ConfirmButton()));
        await dialogs.InvokeAsync(() => ConfirmButton().Click());

        dialogs.WaitForAssertion(() => api.Verify(a => a.DiscardAsync(row.Id, It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once));
        api.Verify(a => a.DiscardAsync(row.Id, draftId, It.IsAny<CancellationToken>()), Times.Once);
        api.Verify(a => a.DiscardAsync(It.IsAny<Guid>(), Guid.Empty, It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── DocumentSourcePanel ──────────────────────────────────────────────────────────

    [Fact]
    public async Task The_panel_counts_each_tab_and_holds_rows_past_the_cap_with_the_reason()
    {
        await using var ctx = PageTestContext.Create(Importer);
        var a = new DocumentSourceFile(Guid.NewGuid(), "a.pdf", "application/pdf", 10, DateTime.UtcNow);
        var b = new DocumentSourceFile(Guid.NewGuid(), "b.pdf", "application/pdf", 10, DateTime.UtcNow);
        var panel = ctx.Render<DocumentSourcePanel>(p => p
            .Add(x => x.Tab, DocumentSourcePanel.LibraryTab)
            .Add(x => x.Counts, true)
            .Add(x => x.Uploads, [new OdsUploadFile { Uid = "u", Name = "x.pdf", Kind = "Other" }])
            .Add(x => x.Pool, [a, b])
            .Add(x => x.PickedIds, new HashSet<Guid> { a.Id })
            .Add(x => x.Cap, 1)
            .Add(x => x.CapReason, "At most 2 files"));

        Assert.Contains("Upload new (1)", panel.Markup, StringComparison.Ordinal);
        Assert.Contains("From Files (1)", panel.Markup, StringComparison.Ordinal);
        var rows = panel.FindAll(".prop-lib-main");
        Assert.False(rows[0].HasAttribute("disabled"));
        Assert.True(rows[1].HasAttribute("disabled"));
        Assert.Contains("At most 2 files", rows[1].TextContent, StringComparison.Ordinal);
    }
}
