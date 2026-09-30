using System.Net;
using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages;
using Odyssey.Client.Pages.Finance;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The host half of issue #252. <c>OdsFilesTable</c> closes its Edit dialog and flashes "Saved" only
/// when the host's <c>OnSave</c> handler does not call <see cref="OdsRecordSaveEventArgs.Fail"/>
/// (<see cref="TableSaveOutcomeTests"/> pins the table half). These tests drive each host's real
/// handler through the open dialog's commit and stub a refused write, so removing any host's
/// <c>args.Fail()</c> leaves the dialog closing under a "Saved" chip and fails a test here.
/// </summary>
/// <remarks>
/// The commit is raised through the mounted dialog's <c>OnSave</c> — the exact seam the dialog's own
/// submit calls — rather than by typing into its fields, because the link surfaces (contract,
/// property) edit only a type picker and dates. The dialog closing itself on a <c>true</c> answer is
/// <c>OdsFormDialog</c>'s contract and is covered end to end in <see cref="TableSaveOutcomeTests"/>.
/// </remarks>
internal static class FileHostSaveHarness
{
    public const string ServerReason = "A file with that name already exists.";

    public static ApiResult Refused(HttpStatusCode status = HttpStatusCode.Conflict) =>
        ApiResult.Failure(status, new ApiProblem { Detail = ServerReason });

    public static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(Mock.Of<IClipboardService>());
        ctx.Services.AddSingleton(Mock.Of<IReferenceDataCache>());
        ctx.Services.AddSingleton(Mock.Of<IContactQuickCreate>());
        return ctx;
    }

    public static AuthenticationStateProvider SignedInWith(params string[] permissions) =>
        new FixedAuth(new ClaimsPrincipal(new ClaimsIdentity(
            permissions.Select(p => new Claim(PermissionClaims.Type, p)), "test")));

    private sealed class FixedAuth(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(user));
    }

    public static IEnumerable<string> Toasts(BunitContext ctx) =>
        ctx.Services.GetRequiredService<ISnackbar>().ShownSnackbars.Select(s => s.Message ?? string.Empty);

    /// <summary>Opens the only row's Edit dialog through its overflow menu.</summary>
    public static void OpenEdit<T>(BunitContext ctx, IRenderedComponent<T> host)
        where T : class, IComponent
    {
        var popover = ctx.Render<MudPopoverProvider>();
        host.Find("button[aria-label='Row actions']").Click();
        popover.WaitForElement("div.mud-menu-item");
        var labels = popover.FindAll(".odc-menu-item-body > span:first-child").Select(e => e.TextContent.Trim()).ToList();
        Assert.Contains("Edit", labels);
        popover.FindAll("div.mud-menu-item")[labels.IndexOf("Edit")].Click();
    }

    public static bool ShowsSaved<T>(IRenderedComponent<T> host)
        where T : class, IComponent =>
        host.FindAll("tbody").Any(t => t.TextContent.Contains("Saved", StringComparison.Ordinal));

    /// <summary>Raises the host's handler directly, for the branches the dialog cannot reach.</summary>
    public static async Task<OdsRecordSaveEventArgs> RaiseSaveAsync<T>(
        IRenderedComponent<T> host, object key, object? patch)
        where T : class, IComponent
    {
        var table = host.FindComponent<OdsFilesTable>();
        var args = new OdsRecordSaveEventArgs(key, patch);
        await host.InvokeAsync(() => table.Instance.OnSave.InvokeAsync(args));
        return args;
    }
}

// ── /files ───────────────────────────────────────────────────────────────────

[Collection(FilesPageCollection.Name)]
public sealed class FilesPageSaveOutcomeTests : IAsyncDisposable
{
    static FilesPageSaveOutcomeTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private static readonly FileListItem Stored = new(
        Guid.Parse("41414141-0000-0000-0000-000000000001"), "statement.pdf", "application/pdf", 25_800,
        new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc), "April statement");

    private readonly BunitContext ctx = FileHostSaveHarness.NewContext();
    private readonly Mock<IFilesApiClient> files = new();

    public FilesPageSaveOutcomeTests()
    {
        Files.InteractiveCheck = static () => true;
        files.Setup(f => f.ListAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<FileListItem>>.Success([Stored], HttpStatusCode.OK));
        files.Setup(f => f.ListAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<PagedResult<FileListItem>>.Success(
                new PagedResult<FileListItem> { Items = [Stored], TotalCount = 1 }, HttpStatusCode.OK));
        ctx.Services.AddSingleton(files.Object);
        ctx.Services.AddSingleton(Mock.Of<IFileExportApiClient>());
        ctx.Services.AddSingleton(Mock.Of<IPageStateService>());
        ctx.Services.AddSingleton(FileHostSaveHarness.SignedInWith(PermissionClaims.FilesRead, PermissionClaims.FilesUpdate));
    }

    public async ValueTask DisposeAsync()
    {
        Files.InteractiveCheck = static () => OperatingSystem.IsBrowser();
        await ctx.DisposeAsync();
    }

    private IRenderedComponent<Files> RenderPage()
    {
        ctx.Render<MudDialogProvider>();
        var page = ctx.Render<Files>();
        page.WaitForAssertion(() => Assert.Contains("statement.pdf", page.Markup, StringComparison.Ordinal));
        return page;
    }

    [Fact]
    public async Task A_refused_rename_keeps_the_dialog_open_says_why_and_does_not_flash()
    {
        files.Setup(f => f.UpdateMetadataAsync(Stored.Id, "April statement", "renamed.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<FileMetadataResponse>.Failure(
                HttpStatusCode.Conflict, new ApiProblem { Detail = FileHostSaveHarness.ServerReason }));
        var page = RenderPage();
        FileHostSaveHarness.OpenEdit(ctx, page);
        var dialog = page.WaitForComponent<FilesMetaEditDialog>();

        var saved = await page.InvokeAsync(() => dialog.Instance.OnSave!(new FilesMetaEditDialog.Patch("renamed.pdf", "April statement")));

        Assert.False(saved);
        Assert.True(page.FindComponent<FilesMetaEditDialog>().Instance.Open);
        Assert.False(FileHostSaveHarness.ShowsSaved(page));
        Assert.Contains(FileHostSaveHarness.Toasts(ctx), t => t.Contains(FileHostSaveHarness.ServerReason, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unchanged_patch_is_a_success_that_writes_nothing()
    {
        var page = RenderPage();

        var args = await FileHostSaveHarness.RaiseSaveAsync(
            page, Stored.Id.ToString(), new FilesMetaEditDialog.Patch(Stored.FileName, Stored.Description));

        Assert.False(args.Failed);
        files.Verify(f => f.UpdateMetadataAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_row_no_longer_listed_fails_and_says_so()
    {
        var page = RenderPage();

        var args = await FileHostSaveHarness.RaiseSaveAsync(
            page, Guid.NewGuid().ToString(), new FilesMetaEditDialog.Patch("renamed.pdf", null));

        Assert.True(args.Failed);
        Assert.Contains(FileHostSaveHarness.Toasts(ctx), t => t.Contains("no longer listed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_patch_of_the_wrong_shape_is_never_reported_as_saved()
    {
        var page = RenderPage();

        var args = await FileHostSaveHarness.RaiseSaveAsync(page, Stored.Id.ToString(), new OdsFileEdit("renamed.pdf", "Other"));

        Assert.True(args.Failed);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FilesPageCollection
{
    public const string Name = "files-page";
}

// ── Account files ────────────────────────────────────────────────────────────

[Collection(FilesPageCollection.Name)]
public sealed class AccountFilesSaveOutcomeTests : IAsyncDisposable
{
    static AccountFilesSaveOutcomeTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private static readonly Guid AccountId = Guid.Parse("42424242-0000-0000-0000-000000000001");
    private static readonly Guid FileId = Guid.Parse("42424242-0000-0000-0000-000000000002");

    private static ExistingAccountFile Stored(string name = "statement.pdf") => new()
    {
        Id = Guid.Parse("42424242-0000-0000-0000-000000000003"),
        AccountId = AccountId,
        AttachedAtUtc = new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc),
        FileType = AccountFileType.Statement,
        FileMetadata = new ExistingFileMetadata
        {
            Id = FileId,
            FileName = name,
            ContentType = "application/pdf",
            SizeBytes = 25_800,
            FileBlobId = Guid.NewGuid(),
            UploadedAtUtc = new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc),
        },
    };

    private readonly BunitContext ctx = FileHostSaveHarness.NewContext();
    private readonly Mock<IFilesApiClient> files = new();
    private readonly Mock<IAccountsApiClient> accounts = new();
    private ExistingAccountFile listed = Stored();

    public AccountFilesSaveOutcomeTests()
    {
        FilesSectionBase<ExistingAccountFile>.InteractiveCheck = static () => true;
        accounts.Setup(a => a.ListFilesAsync(AccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ApiResult<List<ExistingAccountFile>>.Success([listed], HttpStatusCode.OK));
        ctx.Services.AddSingleton(files.Object);
        ctx.Services.AddSingleton(accounts.Object);
        ctx.Services.AddSingleton(Mock.Of<IFileAnalysisDisclosureCache>());
        var uploadLimits = new Mock<IUploadLimitsCache>();
        uploadLimits.Setup(u => u.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UploadLimitsCache.Fallback);
        ctx.Services.AddSingleton(uploadLimits.Object);
        ctx.Services.AddSingleton(FileHostSaveHarness.SignedInWith());
    }

    public async ValueTask DisposeAsync()
    {
        FilesSectionBase<ExistingAccountFile>.InteractiveCheck = static () => OperatingSystem.IsBrowser();
        await ctx.DisposeAsync();
    }

    private IRenderedComponent<AccountFilesSection> RenderSection()
    {
        ctx.Render<MudDialogProvider>();
        var section = ctx.Render<AccountFilesSection>(p => p
            .Add(s => s.AccountId, AccountId)
            .Add(s => s.Chrome, false)
            .Add(s => s.CanEdit, true)
            .Add(s => s.AccountName, "Checking"));
        section.WaitForAssertion(() => Assert.Contains("statement.pdf", section.Markup, StringComparison.Ordinal));
        return section;
    }

    private async Task<bool> CommitThroughDialogAsync(IRenderedComponent<AccountFilesSection> host, OdsFileEdit patch)
    {
        FileHostSaveHarness.OpenEdit(ctx, host);
        var dialog = host.WaitForComponent<OdsFilesEditDialog>();
        return await host.InvokeAsync(() => dialog.Instance.OnSave!(patch));
    }

    private void RenameReturns(ApiResult<FileMetadataResponse> result) =>
        files.Setup(f => f.UpdateMetadataAsync(FileId, It.IsAny<string?>(), "renamed.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    [Fact]
    public async Task A_refused_rename_keeps_the_dialog_open_says_why_and_does_not_flash()
    {
        RenameReturns(ApiResult<FileMetadataResponse>.Failure(
            HttpStatusCode.Conflict, new ApiProblem { Detail = FileHostSaveHarness.ServerReason }));
        var section = RenderSection();

        var saved = await CommitThroughDialogAsync(section, new OdsFileEdit("renamed.pdf", nameof(AccountFileType.Statement)));

        Assert.False(saved);
        Assert.True(section.FindComponent<OdsFilesEditDialog>().Instance.Open);
        Assert.False(FileHostSaveHarness.ShowsSaved(section));
        Assert.Contains(FileHostSaveHarness.Toasts(ctx), t => t.Contains(FileHostSaveHarness.ServerReason, StringComparison.Ordinal));
        accounts.Verify(a => a.UpdateFileAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<UpdateAccountFileRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_refused_type_change_keeps_the_dialog_open_says_why_and_does_not_flash()
    {
        accounts.Setup(a => a.UpdateFileAsync(AccountId, FileId, It.IsAny<UpdateAccountFileRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FileHostSaveHarness.Refused(HttpStatusCode.BadRequest));
        var section = RenderSection();

        var saved = await CommitThroughDialogAsync(section, new OdsFileEdit("statement.pdf", nameof(AccountFileType.Other)));

        Assert.False(saved);
        Assert.True(section.FindComponent<OdsFilesEditDialog>().Instance.Open);
        Assert.False(FileHostSaveHarness.ShowsSaved(section));
        Assert.Contains(FileHostSaveHarness.Toasts(ctx), t => t.Contains(FileHostSaveHarness.ServerReason, StringComparison.Ordinal));
    }

    /// <summary>
    /// The rename lands and the type update is refused: the save as a whole failed, but the row must
    /// be re-read so it shows the name the server now holds rather than the stale one.
    /// </summary>
    [Fact]
    public async Task A_partial_failure_fails_the_save_but_re_reads_the_list()
    {
        RenameReturns(ApiResult<FileMetadataResponse>.Success(
            new FileMetadataResponse(FileId, "renamed.pdf", "application/pdf", 25_800, "hash", DateTime.UtcNow, null),
            HttpStatusCode.OK));
        accounts.Setup(a => a.UpdateFileAsync(AccountId, FileId, It.IsAny<UpdateAccountFileRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                listed = Stored("renamed.pdf");
                return FileHostSaveHarness.Refused(HttpStatusCode.BadRequest);
            });
        var section = RenderSection();
        accounts.Invocations.Clear();

        var saved = await CommitThroughDialogAsync(section, new OdsFileEdit("renamed.pdf", nameof(AccountFileType.Other)));

        Assert.False(saved);
        Assert.True(section.FindComponent<OdsFilesEditDialog>().Instance.Open);
        accounts.Verify(a => a.ListFilesAsync(AccountId, It.IsAny<CancellationToken>()), Times.Once);
        section.WaitForAssertion(() => Assert.Contains("renamed.pdf", section.Find("tbody").TextContent, StringComparison.Ordinal));
        Assert.False(FileHostSaveHarness.ShowsSaved(section));
    }

    [Fact]
    public async Task A_row_no_longer_listed_fails_and_says_so()
    {
        var section = RenderSection();

        var args = await FileHostSaveHarness.RaiseSaveAsync(section, Guid.NewGuid().ToString(), new OdsFileEdit("x.pdf", "Other"));

        Assert.True(args.Failed);
        Assert.Contains(FileHostSaveHarness.Toasts(ctx), t => t.Contains("no longer listed", StringComparison.Ordinal));
    }
}

// ── Contract documents ───────────────────────────────────────────────────────

public sealed class ContractFilesSaveOutcomeTests : IAsyncDisposable
{
    static ContractFilesSaveOutcomeTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private static readonly Guid ContractId = Guid.Parse("43434343-0000-0000-0000-000000000001");

    private static readonly ContractFileItem Attachment = new(
        Guid.Parse("43434343-0000-0000-0000-000000000002"), "lease-signed.pdf", "application/pdf", 25_800,
        new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc), ContractFileType.Signed);

    private readonly BunitContext ctx = FileHostSaveHarness.NewContext();
    private readonly Mock<IContractsApiClient> contracts = new();
    private int changed;

    public ContractFilesSaveOutcomeTests()
    {
        ctx.Services.AddSingleton(contracts.Object);
        ctx.Services.AddSingleton(FileHostSaveHarness.SignedInWith());
    }

    public ValueTask DisposeAsync() => ctx.DisposeAsync();

    private IRenderedComponent<ContractFilesTable> RenderTable()
    {
        ctx.Render<MudDialogProvider>();
        var table = ctx.Render<ContractFilesTable>(p => p
            .Add(t => t.ContractId, ContractId)
            .Add(t => t.Files, new[] { Attachment })
            .Add(t => t.CanUpdate, true)
            .Add(t => t.OnChanged, () => changed++));
        return table;
    }

    [Fact]
    public async Task A_refused_update_keeps_the_dialog_open_says_why_and_does_not_flash()
    {
        contracts.Setup(c => c.UpdateFileAsync(ContractId, Attachment.FileId, It.IsAny<UpdateContractFileRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FileHostSaveHarness.Refused(HttpStatusCode.UnprocessableEntity));
        var table = RenderTable();
        FileHostSaveHarness.OpenEdit(ctx, table);
        var dialog = table.WaitForComponent<OdsFilesEditDialog>();

        var saved = await table.InvokeAsync(() =>
            dialog.Instance.OnSave!(new OdsFileEdit(Attachment.FileName, nameof(ContractFileType.Amendment))));

        Assert.False(saved);
        Assert.True(table.FindComponent<OdsFilesEditDialog>().Instance.Open);
        Assert.False(FileHostSaveHarness.ShowsSaved(table));
        Assert.Contains(FileHostSaveHarness.Toasts(ctx), t => t.Contains(FileHostSaveHarness.ServerReason, StringComparison.Ordinal));
        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task A_row_no_longer_listed_fails_and_says_so()
    {
        var table = RenderTable();

        var args = await FileHostSaveHarness.RaiseSaveAsync(table, Guid.NewGuid().ToString(), new OdsFileEdit("x.pdf", "Amendment"));

        Assert.True(args.Failed);
        Assert.Contains(FileHostSaveHarness.Toasts(ctx), t => t.Contains("no longer listed", StringComparison.Ordinal));
        contracts.Verify(c => c.UpdateFileAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<UpdateContractFileRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
