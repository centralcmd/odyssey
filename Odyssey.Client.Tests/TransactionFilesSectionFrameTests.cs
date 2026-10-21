using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The bare (<c>Chrome="false"</c>) transaction files section frames its table and nothing else, so an
/// empty Documents section shows its line unframed — as a contract's does — including once the last
/// file is detached. The disclosure form (<c>Chrome="true"</c>) never takes the frame.
/// </summary>
[Collection(TransactionDialogCollection.Name)]
public sealed class TransactionFilesSectionFrameTests : IAsyncLifetime
{
    private readonly BunitContext ctx = new();
    private readonly Mock<ITransactionsApiClient> transactions = new();
    private readonly Mock<IDialogService> dialogs = new();
    private readonly Guid transactionId = Guid.NewGuid();

    public TransactionFilesSectionFrameTests()
    {
        FilesSectionBase<ExistingTransactionFile>.InteractiveCheck = static () => true;
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var uploadLimits = new Mock<IUploadLimitsCache>();
        uploadLimits.Setup(u => u.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UploadLimitsCache.Fallback);
        ctx.Services.AddSingleton(uploadLimits.Object);
        ctx.Services.AddSingleton(transactions.Object);
        ctx.Services.AddSingleton(Mock.Of<IFilesApiClient>());
        ctx.Services.AddSingleton(Mock.Of<IClipboardService>());
        ctx.Services.AddSingleton(dialogs.Object);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        FilesSectionBase<ExistingTransactionFile>.InteractiveCheck = static () => OperatingSystem.IsBrowser();
        await ctx.DisposeAsync();
    }

    private void Listing(params ExistingTransactionFile[] files) =>
        transactions.Setup(t => t.ListFilesAsync(transactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<ExistingTransactionFile>>.Success([.. files], HttpStatusCode.OK));

    private IRenderedComponent<TransactionFilesSection> Render(bool chrome) =>
        ctx.Render<TransactionFilesSection>(p => p
            .Add(s => s.TransactionId, transactionId)
            .Add(s => s.Chrome, chrome)
            .Add(s => s.CanDelete, true));

    private ExistingTransactionFile File() => new()
    {
        Id = Guid.NewGuid(),
        TransactionId = transactionId,
        AttachedAtUtc = DateTime.UtcNow,
        FileMetadata = new ExistingFileMetadata
        {
            Id = Guid.NewGuid(),
            FileName = "receipt.pdf",
            ContentType = "application/pdf",
            SizeBytes = 10,
            FileBlobId = Guid.NewGuid(),
            UploadedAtUtc = DateTime.UtcNow,
        },
    };

    [Fact]
    public void The_bare_form_frames_its_table_when_files_exist()
    {
        Listing(File());

        var cut = Render(chrome: false);

        cut.WaitForAssertion(() =>
        {
            var frame = Assert.Single(cut.FindAll(".acct-table-frame"));
            Assert.NotNull(frame.QuerySelector("table"));
            Assert.Empty(cut.FindAll(".odc-empty.line"));
        });
    }

    [Fact]
    public void The_bare_form_leaves_an_empty_line_unframed()
    {
        Listing();

        var cut = Render(chrome: false);

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("No files attached to this transaction yet.", cut.Find(".odc-empty.line").TextContent);
            Assert.Empty(cut.FindAll(".acct-table-frame"));
        });
    }

    [Fact]
    public void The_disclosure_form_never_takes_the_frame()
    {
        Listing(File());

        var cut = Render(chrome: true);
        cut.Find(".odc-collapsible-head button[aria-expanded]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(cut.Find(".odc-collapsible-body table"));
            Assert.Empty(cut.FindAll(".acct-table-frame"));
        });
    }

    [Fact]
    public async Task Detaching_the_last_file_leaves_the_empty_line_unframed()
    {
        var file = File();
        Listing(file);
        transactions.Setup(t => t.DetachFileAsync(transactionId, file.FileMetadata.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.NoContent));
        dialogs.Setup(d => d.ShowMessageBoxAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DialogOptions>()))
            .ReturnsAsync(true);
        var cut = Render(chrome: false);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".acct-table-frame")));

        var table = cut.FindComponent<OdsFilesTable>();
        var row = Assert.Single(table.Instance.Files);
        await cut.InvokeAsync(() => table.Instance.OnDelete.InvokeAsync(row));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("No files attached to this transaction yet.", cut.Find(".odc-empty.line").TextContent);
            Assert.Empty(cut.FindAll(".acct-table-frame"));
        });
    }
}
