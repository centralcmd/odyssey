using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Pages.Finance;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The transaction row's expanded detail (design system · Transactions TxnDetail): an in-table
/// OdsRecordBody whose Details slot is an OdsInfoTileGrid, then the Files section.
/// </summary>
/// <remarks>
/// The tile set is conditional, and each condition is its own rule: the two id tiles appear together
/// when either id is set (the absent one reads "—"), Extra data only when present, the status comment
/// rides as the Status tile's foot rather than a tile of its own, and the account number as the
/// Account tile's. None of that shows as broken in a screenshot of one seeded transaction, which is
/// why it is pinned here. The section's file load is skipped off-browser, so these render no requests.
/// </remarks>
[Collection(TransactionDialogCollection.Name)]
public sealed class TransactionDetailPanelTests : IAsyncLifetime
{
    private readonly BunitContext ctx = new();

    public TransactionDetailPanelTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var uploadLimits = new Mock<IUploadLimitsCache>();
        uploadLimits.Setup(u => u.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UploadLimitsCache.Fallback);
        ctx.Services.AddSingleton(uploadLimits.Object);
        ctx.Services.AddSingleton(Mock.Of<ITransactionsApiClient>());
        ctx.Services.AddSingleton(Mock.Of<IFilesApiClient>());
        ctx.Services.AddSingleton(Mock.Of<IClipboardService>());
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await ctx.DisposeAsync();

    private static ExistingTransaction Transaction(Action<ExistingTransaction>? configure = null)
    {
        var t = new ExistingTransaction
        {
            TransactionId = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            Account = new ExistingAccount
            {
                AccountId = Guid.NewGuid(),
                Name = "Everyday Checking",
                AccountNumber = "1002 0034 8150 1042",
                Description = "",
                Opened = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            },
            Description = "Weekly groceries",
            Amount = -211.04m,
            TimeStamp = new DateTime(2026, 6, 17, 12, 0, 0, DateTimeKind.Utc),
            CurrencyCode = "USD",
            Status = TransactionStatus.New,
        };
        configure?.Invoke(t);
        return t;
    }

    private IRenderedComponent<TransactionDetailPanel> Render(ExistingTransaction transaction) =>
        ctx.Render<TransactionDetailPanel>(p => p.Add(d => d.Transaction, transaction));

    private static IReadOnlyList<string> TileLabels(IRenderedComponent<TransactionDetailPanel> cut) =>
        [.. cut.FindAll(".odc-infotile-k").Select(k => k.TextContent.Trim())];

    /// <summary>The tile carrying <paramref name="label"/>, found by its overline.</summary>
    private static AngleSharp.Dom.IElement Tile(IRenderedComponent<TransactionDetailPanel> cut, string label) =>
        cut.FindAll(".odc-infotile").Single(t => t.QuerySelector(".odc-infotile-k")?.TextContent.Trim() == label);

    private static ExistingTransactionFile File() => new()
    {
        Id = Guid.NewGuid(),
        TransactionId = Guid.NewGuid(),
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
    public void The_body_is_the_in_table_record_body()
    {
        var cut = Render(Transaction());

        Assert.Contains("in-table", cut.Find(".odc-record-body").ClassList);
    }

    [Theory]
    [InlineData(0, "0 files")]
    [InlineData(1, "1 file")]
    [InlineData(2, "2 files")]
    public void The_files_divider_counts_the_attachments(int count, string expected)
    {
        var cut = Render(Transaction(t => t.TransactionFiles = [.. Enumerable.Range(0, count).Select(_ => File())]));

        Assert.Equal(expected, cut.Find(".odc-sectiondivider-meta").TextContent.Trim());
    }

    [Fact]
    public void A_transaction_with_no_ids_or_extra_data_renders_the_base_tile_set()
    {
        var cut = Render(Transaction());

        Assert.Equal(
            ["Amount", "Date", "Status", "Status updated", "Account", "Contact", "Tags", "Currency", "Description"],
            TileLabels(cut));
    }

    [Fact]
    public void Either_id_brings_both_id_tiles_with_the_absent_one_as_a_dash()
    {
        var cut = Render(Transaction(t => t.ExternalId = "WF-10472-1123"));

        Assert.Equal("WF-10472-1123", Tile(cut, "External ID").QuerySelector(".odc-infotile-v")!.TextContent.Trim());
        Assert.Equal("—", Tile(cut, "Internal ID").QuerySelector(".odc-infotile-v")!.TextContent.Trim());
    }

    [Fact]
    public void Extra_data_gets_a_wide_tile_only_when_present()
    {
        var cut = Render(Transaction(t => t.ExtraData = "{\"source\":\"import\"}"));

        Assert.Contains("wide", Tile(cut, "Extra data").ClassList);
    }

    [Fact]
    public void The_status_comment_is_the_status_tiles_foot_and_absent_without_one()
    {
        var withComment = Render(Transaction(t => t.StatusComment = "Verified against statement."));
        Assert.Equal("Verified against statement.", Tile(withComment, "Status").QuerySelector(".odc-infotile-foot")!.TextContent.Trim());

        var without = Render(Transaction());
        Assert.Null(Tile(without, "Status").QuerySelector(".odc-infotile-foot"));
    }

    [Fact]
    public void The_account_tile_names_the_account_with_its_number_as_the_foot()
    {
        var cut = Render(Transaction());

        var tile = Tile(cut, "Account");
        Assert.Equal("Everyday Checking", tile.QuerySelector(".odc-infotile-v")!.TextContent.Trim());
        Assert.Equal("1002 0034 8150 1042", tile.QuerySelector(".odc-infotile-foot")!.TextContent.Trim());
    }

    [Fact]
    public void Tags_read_as_a_dash_when_none_and_the_label_is_singular_for_one()
    {
        var none = Render(Transaction());
        Assert.Equal("—", Tile(none, "Tags").QuerySelector(".odc-infotile-v")!.TextContent.Trim());

        var one = Render(Transaction(t => t.TransactionTags =
            [new ExistingTransactionTag { TransactionTagId = Guid.NewGuid(), Name = "Groceries", Archived = null }]));
        Assert.Contains("Tag", TileLabels(one));
        Assert.DoesNotContain("Tags", TileLabels(one));
    }

    [Fact]
    public void The_amount_tile_is_toned_by_direction()
    {
        var expense = Render(Transaction());
        Assert.Contains("tone-expense", Tile(expense, "Amount").ClassList);
        Assert.Equal("Money out", Tile(expense, "Amount").QuerySelector(".odc-infotile-foot")!.TextContent.Trim());

        var income = Render(Transaction(t => t.Amount = 500m));
        Assert.Contains("tone-income", Tile(income, "Amount").ClassList);
    }
}
