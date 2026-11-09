using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Moq;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance.Instruments;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The <c>/instrument-prices</c> page (issue #452 §3.2–3.3): the hidden caption and row header, a fetched row with
/// no menu and its read-only Source text, claim-gated manual-row actions, <c>RowBusy</c> while a delete is in
/// flight, and the <c>?instrument=</c> / <c>&amp;add=1</c> deep link.
/// </summary>
public class InstrumentPricesPageTests
{
    static InstrumentPricesPageTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private static readonly Guid Equinor = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ManualId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid FetchedId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

    private static ExistingInstrumentPrice Price(Guid id, InstrumentPriceSource source, DateOnly date) => new()
    {
        InstrumentPriceId = id,
        InstrumentId = Equinor,
        Symbol = "EQNR",
        InstrumentName = "Equinor",
        CurrencyCode = "NOK",
        TradingDate = date,
        Close = 274.5m,
        Source = source,
        SourceKey = source == InstrumentPriceSource.Fetched ? "eodhd" : null,
        SourceDisplayName = source == InstrumentPriceSource.Fetched ? "EODHD" : null,
        Status = InstrumentPriceStatus.Historical,
    };

    private static readonly ExistingInstrumentPrice Manual = Price(ManualId, InstrumentPriceSource.Manual, new(2026, 11, 6));
    private static readonly ExistingInstrumentPrice Fetched = Price(FetchedId, InstrumentPriceSource.Fetched, new(2026, 11, 5));

    private sealed class Harness
    {
        public required BunitContext Ctx { get; init; }
        public Mock<IInstrumentPricesApiClient> Prices { get; } = new() { DefaultValue = DefaultValue.Mock };
        public Mock<IInstrumentsApiClient> Instruments { get; } = new();
        public RecordingSnackbar Snackbar { get; } = new();
    }

    private static Harness NewHarness(params string[] claims)
    {
        var h = new Harness { Ctx = PageTestContext.Create(claims) };
        h.Prices.Setup(p => p.ListAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InstrumentPricesFilter?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<PagedResult<ExistingInstrumentPrice>>.Success(
                new PagedResult<ExistingInstrumentPrice> { Items = [Manual, Fetched], TotalCount = 2 }, HttpStatusCode.OK));
        h.Instruments.Setup(i => i.GetSummaryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<InstrumentSummary>.Success(new InstrumentSummary { Attributions = ["EODHD"] }, HttpStatusCode.OK));
        h.Ctx.Services.AddSingleton(h.Prices.Object);
        h.Ctx.Services.AddSingleton(h.Instruments.Object);
        h.Ctx.Services.AddSingleton<ISnackbar>(h.Snackbar);
        h.Ctx.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero)));
        return h;
    }

    private static IRenderedComponent<InstrumentPricesPage> Render(Harness h)
    {
        var cut = h.Ctx.Render<InstrumentPricesPage>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("tbody th[scope=row]").Count));
        return cut;
    }

    private static AngleSharp.Dom.IElement RowOf(IRenderedComponent<InstrumentPricesPage> cut, string date) =>
        cut.FindAll("tbody tr").First(r => r.QuerySelector("th[scope=row]") is not null && r.TextContent.Contains(date));

    [Fact]
    public async Task The_table_is_named_by_a_hidden_caption_and_the_instrument_is_the_row_header()
    {
        var harness = NewHarness(PermissionClaims.InstrumentPricesRead);
        await using var _ = harness.Ctx;
        var cut = Render(harness);

        var caption = cut.Find("table > caption");
        Assert.Equal("Instrument prices", caption.TextContent);
        Assert.Contains("odc-sr-only", caption.ClassList);
        Assert.All(cut.FindAll("tbody th[scope=row]"), th => Assert.Contains("EQNR", th.TextContent));
    }

    [Fact]
    public async Task A_fetched_row_has_no_menu_and_says_it_is_read_only()
    {
        var h = NewHarness(PermissionClaims.InstrumentPricesRead, PermissionClaims.InstrumentPricesUpdate, PermissionClaims.InstrumentPricesDelete);
        await using var _ = h.Ctx;
        var cut = Render(h);

        var fetched = RowOf(cut, "5 Nov 2026");
        Assert.Empty(fetched.QuerySelectorAll(".odc-rec-menu"));
        Assert.Contains("Fetched · EODHD · read-only", fetched.TextContent);

        var manual = RowOf(cut, "6 Nov 2026");
        Assert.Single(manual.QuerySelectorAll(".odc-rec-menu"));
        Assert.Contains("Manual", manual.TextContent);
    }

    [Fact]
    public async Task Manual_row_actions_follow_the_claims()
    {
        var reader = NewHarness(PermissionClaims.InstrumentPricesRead);
        await using var _ = reader.Ctx;
        var cut = Render(reader);
        var ctx = new OdsRecordActionContext { Remove = () => { }, Toggle = () => { }, StartEdit = () => { } };

        Assert.Empty(cut.Instance.BuildActions(Manual, ctx));
        Assert.Empty(RowOf(cut, "6 Nov 2026").QuerySelectorAll(".odc-rec-menu"));
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Trim() == "Add price");

        var writer = NewHarness(PermissionClaims.InstrumentPricesRead, PermissionClaims.InstrumentPricesCreate,
            PermissionClaims.InstrumentPricesUpdate, PermissionClaims.InstrumentPricesDelete);
        await using var __ = writer.Ctx;
        var full = Render(writer);

        Assert.Equal(["Edit", "Delete"], full.Instance.BuildActions(Manual, ctx).Where(i => !i.Divider).Select(i => i.Label));
        Assert.Empty(full.Instance.BuildActions(Fetched, ctx));
        Assert.Contains(full.FindAll("button"), b => b.TextContent.Trim() == "Add price");

        var updateOnly = NewHarness(PermissionClaims.InstrumentPricesRead, PermissionClaims.InstrumentPricesUpdate);
        await using var ___ = updateOnly.Ctx;
        Assert.Equal(["Edit"], Render(updateOnly).Instance.BuildActions(Manual, ctx).Select(i => i.Label));
    }

    [Fact]
    public async Task A_row_whose_delete_is_in_flight_shows_the_labelled_spinner_instead_of_its_menu()
    {
        var h = NewHarness(PermissionClaims.InstrumentPricesRead, PermissionClaims.InstrumentPricesDelete);
        await using var _ = h.Ctx;
        var pending = new TaskCompletionSource<ApiResult>();
        h.Prices.Setup(p => p.DeleteAsync(ManualId, It.IsAny<CancellationToken>())).Returns(pending.Task);
        var cut = Render(h);

        var table = cut.FindComponent<OdsRecordTable<ExistingInstrumentPrice>>();
        var deleting = cut.InvokeAsync(() => table.Instance.OnDelete.InvokeAsync(ManualId));

        cut.WaitForAssertion(() =>
        {
            var row = RowOf(cut, "6 Nov 2026");
            Assert.Empty(row.QuerySelectorAll(".odc-rec-menu"));
            Assert.Equal("Saving EQNR 6 Nov 2026", row.QuerySelector(".odc-rt-busy [role=status]")!.GetAttribute("aria-label"));
        });

        await cut.InvokeAsync(() => pending.SetResult(ApiResult.Success(HttpStatusCode.NoContent)));
        await deleting;
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".odc-rt-busy")));
        Assert.Contains(h.Snackbar.Toasts, t => t.Message == "Price deleted.");
    }

    [Fact]
    public async Task The_sub_line_names_the_source_only_while_fetched_prices_exist()
    {
        var h = NewHarness(PermissionClaims.InstrumentPricesRead);
        await using var _ = h.Ctx;
        h.Prices.Setup(p => p.ListAsync(1, 1, It.Is<InstrumentPricesFilter?>(f => f != null && f.Sources != null), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<PagedResult<ExistingInstrumentPrice>>.Success(new PagedResult<ExistingInstrumentPrice> { TotalCount = 1 }, HttpStatusCode.OK));
        var cut = Render(h);

        cut.WaitForAssertion(() => Assert.Equal("2 prices · Prices from EODHD", cut.Instance.SubLineText));
    }

    [Fact]
    public async Task A_deep_link_filters_to_the_instrument_and_add_opens_the_dialog_locked_to_it()
    {
        var h = NewHarness(PermissionClaims.InstrumentPricesRead, PermissionClaims.InstrumentPricesCreate);
        await using var _ = h.Ctx;
        h.Instruments.Setup(i => i.GetAsync(Equinor, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingInstrument>.Success(new ExistingInstrument
            {
                InstrumentId = Equinor, Symbol = "EQNR", Name = "Equinor", Type = InstrumentType.Stock, CurrencyCode = "NOK",
            }, HttpStatusCode.OK));
        h.Ctx.Services.GetRequiredService<NavigationManager>().NavigateTo($"/instrument-prices?instrument={Equinor}&add=1");

        var cut = Render(h);

        h.Prices.Verify(p => p.ListAsync(1, It.IsAny<int>(),
            It.Is<InstrumentPricesFilter?>(f => f != null && f.InstrumentIds != null && f.InstrumentIds.Single() == Equinor),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        var dialog = cut.WaitForComponent<InstrumentPriceDialog>();
        Assert.Equal(Equinor, dialog.Instance.LockedInstrumentId);
        Assert.Null(dialog.Instance.Price);
    }

    [Fact]
    public async Task An_unknown_linked_instrument_is_announced_and_leaves_the_list_unfiltered()
    {
        var h = NewHarness(PermissionClaims.InstrumentPricesRead, PermissionClaims.InstrumentPricesCreate);
        await using var _ = h.Ctx;
        h.Instruments.Setup(i => i.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingInstrument>.Failure(HttpStatusCode.NotFound, new ApiProblem { Detail = "Not found" }));
        h.Ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/instrument-prices?instrument=not-a-guid&add=1");

        var cut = Render(h);

        Assert.Contains(h.Snackbar.Toasts, t => t.Message == InstrumentPricesPage.MissingInstrumentMessage);
        Assert.Empty(cut.FindComponents<InstrumentPriceDialog>());
        h.Prices.Verify(p => p.ListAsync(It.IsAny<int>(), It.IsAny<int>(),
            It.Is<InstrumentPricesFilter?>(f => f != null && f.InstrumentIds != null), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void The_page_follows_the_spec_title_route_and_state_key()
    {
        var source = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Finance", "Instruments", "InstrumentPricesPage.razor"));
        Assert.Contains("<PageTitle>Instrument prices · Odyssey</PageTitle>", source);
        Assert.Contains("@page \"/instrument-prices\"", source);
        Assert.Contains("[Authorize(Policy = PermissionClaims.InstrumentPricesRead)]", source);
        Assert.Equal("instrument-prices-page", InstrumentPricesPage.PageStateKey);
    }
}
