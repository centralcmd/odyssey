using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Odyssey.Client.Services;
using Odyssey.Client.Theme;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The Edit-transaction dialog's attachments (design system · AddTransactionModal): a removed file and
/// a new upload are both STAGED, and nothing reaches the server until Save. The same rig also pins the
/// dialog's required date, the one Save-blocking rule the form adds on top of the DTO.
/// </summary>
/// <remarks>
/// <para>
/// The ordering is the contract. The update goes first and the file writes only follow a successful
/// one, so a rejected update leaves the attachments exactly as they were, and a Cancel writes nothing
/// at all. Every assertion here is on the sequence of API calls rather than on the rendered table,
/// because a regression here does not look wrong on screen — the table re-renders from the server
/// either way — it silently detaches a user's file, or keeps one they removed.
/// </para>
/// <para>
/// The row menu and the dialog are driven through the real markup, so the staged-removal wiring
/// between <c>TransactionFilesSection</c> and the dialog is exercised, not assumed.
/// </para>
/// </remarks>
[Collection(TransactionDialogCollection.Name)]
public sealed class TransactionDialogAttachmentTests : IAsyncLifetime
{
    private static readonly Guid AccountId = Guid.NewGuid();
    private static readonly Guid TransactionId = Guid.NewGuid();
    private static readonly Guid FileId = Guid.NewGuid();
    private static readonly Guid UploadedId = Guid.NewGuid();

    private readonly BunitContext ctx = new();
    private readonly List<string> calls = [];
    private readonly Mock<ITransactionsApiClient> transactions = new();
    private readonly Mock<IFilesApiClient> files = new();

    static TransactionDialogAttachmentTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    public TransactionDialogAttachmentTests()
    {
        // A bUnit host is not a browser, so both the dialog's reference-data load (which resolves the
        // transaction's account — without it Save stops at validation) and the section's file load
        // would be skipped. Restored on teardown.
        CreateTransactionDialog.InteractiveCheck = static () => true;
        FilesSectionBase<ExistingTransactionFile>.InteractiveCheck = static () => true;

        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();

        transactions.Setup(t => t.ListFilesAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<List<ExistingTransactionFile>> { Status = HttpStatusCode.OK, Value = [AttachedFile()] });
        transactions.Setup(t => t.DetachFileAsync(TransactionId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid, CancellationToken>((_, fileId, _) => calls.Add($"detach:{fileId}"))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.NoContent));
        files.Setup(f => f.UploadAsync(It.IsAny<ApiUpload>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("upload"))
            .ReturnsAsync(new FileUploadResponse(UploadedId, "receipt.pdf", "application/pdf", 5, "hash", DateTime.UtcNow, null));
        files.Setup(f => f.AttachToTransactionAsync(TransactionId, It.IsAny<Guid>(), It.IsAny<TransactionFileType>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid, TransactionFileType, CancellationToken>((_, fileId, _, _) => calls.Add($"attach:{fileId}"))
            .Returns(Task.CompletedTask);

        var accounts = new Mock<IAccountsApiClient>();
        accounts.Setup(a => a.ListAllAsync(It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<List<ExistingAccount>>
            {
                Status = HttpStatusCode.OK,
                Value = [new ExistingAccount { AccountId = AccountId, Name = "Everyday Checking", Description = "", Opened = DateTime.UtcNow }],
            });

        var uploadLimits = new Mock<IUploadLimitsCache>();
        uploadLimits.Setup(u => u.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UploadLimitsCache.Fallback);

        ctx.Services.AddSingleton(transactions.Object);
        ctx.Services.AddSingleton(files.Object);
        ctx.Services.AddSingleton(accounts.Object);
        ctx.Services.AddSingleton(uploadLimits.Object);
        var referenceData = new Mock<IReferenceDataCache>();
        referenceData.Setup(r => r.TransactionTagsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        referenceData.Setup(r => r.ContactsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        referenceData.Setup(r => r.ActiveCurrenciesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        referenceData.Setup(r => r.CurrencyOptionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        ctx.Services.AddSingleton(referenceData.Object);
        ctx.Services.AddSingleton(Mock.Of<IUserPreferenceService>());
        ctx.Services.AddSingleton(Mock.Of<IContactQuickCreate>());
        ctx.Services.AddSingleton(Mock.Of<ITagQuickCreate<ExistingTransactionTag>>());
        ctx.Services.AddSingleton(Mock.Of<IClipboardService>());
        ctx.Services.AddSingleton<AuthenticationStateProvider>(new SignedOut());
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        CreateTransactionDialog.InteractiveCheck = static () => OperatingSystem.IsBrowser();
        FilesSectionBase<ExistingTransactionFile>.InteractiveCheck = static () => OperatingSystem.IsBrowser();
        await ctx.DisposeAsync();
    }

    private static ExistingTransactionFile AttachedFile() => new()
    {
        Id = Guid.NewGuid(),
        TransactionId = TransactionId,
        Type = TransactionFileType.Receipt,
        AttachedAtUtc = new DateTime(2026, 6, 17, 12, 0, 0, DateTimeKind.Utc),
        FileMetadata = new ExistingFileMetadata
        {
            Id = FileId,
            FileName = "groceries-receipt.pdf",
            ContentType = "application/pdf",
            SizeBytes = 1_024,
            FileBlobId = Guid.NewGuid(),
            UploadedAtUtc = new DateTime(2026, 6, 17, 12, 0, 0, DateTimeKind.Utc),
        },
    };

    private static ExistingTransaction Transaction() => new()
    {
        TransactionId = TransactionId,
        AccountId = AccountId,
        Description = "Weekly groceries",
        Amount = -211.04m,
        TimeStamp = new DateTime(2026, 6, 17, 12, 0, 0, DateTimeKind.Utc),
        CurrencyCode = "USD",
        Status = TransactionStatus.New,
        TransactionFiles = [AttachedFile()],
    };

    private void UpdateReturns(ApiResult result) =>
        transactions.Setup(t => t.UpdateAsync(TransactionId, It.IsAny<NewTransaction>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("update"))
            .ReturnsAsync(result);

    private IRenderedComponent<DialogHost> RenderEdit()
    {
        var cut = ctx.Render<DialogHost>(p => p.Add(h => h.Transaction, Transaction()));
        cut.WaitForElement(".atm-dialog .odc-rec tbody tr");
        return cut;
    }

    private static IReadOnlyList<AngleSharp.Dom.IElement> FileRows(IRenderedComponent<DialogHost> cut) =>
        cut.FindAll(".atm-dialog .odc-rec tbody tr");

    /// <summary>Remove the one attached file through its row menu, as a user would.</summary>
    private static void StageRemoval(IRenderedComponent<DialogHost> cut)
    {
        cut.Find(".atm-dialog button[aria-label='Row actions']").Click();
        cut.WaitForElement("div.mud-menu-item");
        cut.FindAll("div.mud-menu-item").Single(i => i.TextContent.Contains("Delete", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.Empty(FileRows(cut)));
    }

    private static void ClickFooter(IRenderedComponent<DialogHost> cut, string label) =>
        cut.FindAll(".atm-dialog button").Single(b => b.TextContent.Contains(label, StringComparison.OrdinalIgnoreCase)).Click();

    [Fact]
    public void A_removed_file_leaves_the_table_without_being_detached()
    {
        var cut = RenderEdit();

        StageRemoval(cut);

        Assert.Empty(calls);
    }

    [Fact]
    public void Save_updates_the_transaction_first_then_detaches_the_staged_removal()
    {
        UpdateReturns(ApiResult.Success(HttpStatusCode.NoContent));
        var cut = RenderEdit();
        StageRemoval(cut);

        ClickFooter(cut, "Save changes");

        cut.WaitForAssertion(() => Assert.Equal(["update", $"detach:{FileId}"], calls));
        Assert.True(cut.Instance.Closed);
    }

    [Fact]
    public void A_rejected_update_leaves_the_attachments_untouched()
    {
        UpdateReturns(ApiResult.Failure(HttpStatusCode.BadRequest, new ApiProblem { Detail = "Rejected" }));
        var cut = RenderEdit();
        StageRemoval(cut);

        ClickFooter(cut, "Save changes");

        cut.WaitForAssertion(() => Assert.Equal(["update"], calls));
        transactions.Verify(t => t.DetachFileAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(cut.Instance.Closed);
    }

    [Fact]
    public void Cancel_after_staging_a_removal_writes_nothing()
    {
        UpdateReturns(ApiResult.Success(HttpStatusCode.NoContent));
        var cut = RenderEdit();
        StageRemoval(cut);

        ClickFooter(cut, "Cancel");

        cut.WaitForAssertion(() => Assert.True(cut.Instance.Closed));
        Assert.Empty(calls);
        transactions.Verify(t => t.DetachFileAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Save_attaches_a_staged_upload_only_after_the_update_and_the_removals()
    {
        UpdateReturns(ApiResult.Success(HttpStatusCode.NoContent));
        var cut = RenderEdit();
        StageRemoval(cut);

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("%PDF", "receipt.pdf", contentType: "application/pdf"));
        cut.WaitForElement(".atm-dialog .odc-upload-file");
        Assert.Empty(calls);

        ClickFooter(cut, "Save changes");

        cut.WaitForAssertion(() => Assert.Equal(["update", $"detach:{FileId}", "upload", $"attach:{UploadedId}"], calls));
    }

    /// <summary>
    /// Without the upload claim and with nothing attached, both of the field's children render nothing,
    /// so the field says so rather than leaving a bare "Attachments" label.
    /// </summary>
    [Fact]
    public void Without_the_upload_claim_an_empty_attachments_field_says_there_are_no_files()
    {
        transactions.Setup(t => t.ListFilesAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<List<ExistingTransactionFile>> { Status = HttpStatusCode.OK, Value = [] });
        var transaction = Transaction();
        transaction.TransactionFiles = [];

        var cut = ctx.Render<DialogHost>(p => p
            .Add(h => h.Transaction, transaction)
            .Add(h => h.CanUploadFiles, false));

        cut.WaitForAssertion(() => Assert.Contains(
            cut.FindAll(".atm-dialog .odc-field-help"),
            h => h.TextContent.Trim() == "No files attached to this transaction."));
        Assert.Empty(cut.FindAll(".atm-dialog .odc-upload-drop"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  The required date (design system · "forms mark required only")
    // ─────────────────────────────────────────────────────────────────────────

    // NewTransaction.TimeStamp is nullable, so the server would accept the omission; the form refuses
    // it, because the field is marked required and a marker that validation ignores is a lie.

    private static IRenderedComponent<OdsDateField> DateField(IRenderedComponent<DialogHost> cut) =>
        cut.FindComponents<OdsDateField>().Single(f => f.Instance.Label == "Date");

    private static void SetDate(IRenderedComponent<DialogHost> cut, DateTime? value)
    {
        var field = DateField(cut);
        cut.InvokeAsync(() => field.Instance.ValueChanged.InvokeAsync(value)).GetAwaiter().GetResult();
    }

    [Fact]
    public void The_date_is_marked_required()
    {
        var cut = RenderEdit();

        Assert.True(DateField(cut).Instance.Required);
    }

    [Fact]
    public void Saving_with_the_date_cleared_is_refused_on_the_field_and_sends_nothing()
    {
        UpdateReturns(ApiResult.Success(HttpStatusCode.NoContent));
        var cut = RenderEdit();

        SetDate(cut, null);
        ClickFooter(cut, "Save changes");

        cut.WaitForAssertion(() => Assert.Equal("Pick the transaction date.", DateField(cut).Instance.Error));
        Assert.Empty(calls);
        Assert.False(cut.Instance.Closed);
    }

    [Fact]
    public void Picking_a_date_again_clears_the_error_and_lets_the_save_through()
    {
        UpdateReturns(ApiResult.Success(HttpStatusCode.NoContent));
        var cut = RenderEdit();
        SetDate(cut, null);
        ClickFooter(cut, "Save changes");
        cut.WaitForAssertion(() => Assert.NotNull(DateField(cut).Instance.Error));

        SetDate(cut, new DateTime(2026, 6, 18));
        cut.WaitForAssertion(() => Assert.Null(DateField(cut).Instance.Error));

        ClickFooter(cut, "Save changes");

        cut.WaitForAssertion(() => Assert.Equal(["update"], calls));
        transactions.Verify(t => t.UpdateAsync(TransactionId,
            It.Is<NewTransaction>(n => n.TimeStamp != null && n.TimeStamp.Value.Date == new DateTime(2026, 6, 18)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>A signed-out principal — the dialog's inline-create claims are not under test.</summary>
    private sealed class SignedOut : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity())));
    }

    /// <summary>The dialog beside MudBlazor's providers, which host its modal and the row menu.</summary>
    public sealed class DialogHost : ComponentBase
    {
        [Parameter] public ExistingTransaction Transaction { get; set; } = default!;

        [Parameter] public bool CanUploadFiles { get; set; } = true;

        public bool Closed { get; private set; }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<CreateTransactionDialog>(2);
            builder.AddComponentParameter(3, nameof(CreateTransactionDialog.Transaction), Transaction);
            builder.AddComponentParameter(4, nameof(CreateTransactionDialog.Open), !Closed);
            builder.AddComponentParameter(5, nameof(CreateTransactionDialog.OpenChanged),
                EventCallback.Factory.Create<bool>(this, open => Closed = !open));
            builder.AddComponentParameter(6, nameof(CreateTransactionDialog.CanDeleteFiles), true);
            builder.AddComponentParameter(7, nameof(CreateTransactionDialog.CanUploadFiles), CanUploadFiles);
            builder.CloseComponent();
        }
    }
}
