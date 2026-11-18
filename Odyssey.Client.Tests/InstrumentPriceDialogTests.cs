using System.Net;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Moq;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Pages.Finance.Instruments;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The Add / edit a manual price dialog (issue #452 §3.3): the shared decimal rule (value-based, trailing zeros
/// accepted), the future-date check and its <c>OsloDate</c> degradation, and the <c>409 price-duplicate</c>
/// "Edit that price" hand-off, which only an <c>instrument-prices.update</c> holder is offered.
/// </summary>
public class InstrumentPriceDialogTests
{
    static InstrumentPriceDialogTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private static readonly Guid Equinor = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ExistingPrice = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly DateOnly Today = new(2026, 11, 9);

    [Theory]
    [InlineData("274.12345678")]
    [InlineData("274,5")]
    [InlineData("274.500000000")]
    [InlineData("1 250.5")]
    public void Up_to_eight_significant_decimals_pass(string close) =>
        Assert.Empty(InstrumentPriceDialog.Validate(Equinor, Today, close, Today));

    [Fact]
    public void Nine_significant_decimals_are_refused_with_the_shared_message()
    {
        var errors = InstrumentPriceDialog.Validate(Equinor, Today, "274.123456789", Today);
        Assert.Equal($"Use at most {InstrumentPriceRules.MaxDecimals} decimals.", errors["close"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("abc")]
    public void A_close_that_is_not_above_zero_is_refused(string close) =>
        Assert.Equal(InstrumentPriceDialog.PriceAboveZeroMessage, InstrumentPriceDialog.Validate(Equinor, Today, close, Today)["close"]);

    [Fact]
    public void A_future_date_is_refused_while_Oslo_today_is_known_and_skipped_when_it_is_not()
    {
        var tomorrow = Today.AddDays(1);
        Assert.Equal(InstrumentPriceDialog.FutureDateMessage, InstrumentPriceDialog.Validate(Equinor, tomorrow, "1", Today)["date"]);

        // OsloDate unavailable (#446 v6 §8.7): the check is skipped and the server's 422 remains.
        Assert.Empty(InstrumentPriceDialog.Validate(Equinor, tomorrow, "1", today: null));
    }

    [Fact]
    public void Missing_instrument_and_date_are_named()
    {
        var errors = InstrumentPriceDialog.Validate(null, null, "1", Today);
        Assert.Equal(["date", "instrument"], errors.Keys.Order());
    }

    private static (BunitContext Ctx, Mock<IInstrumentPricesApiClient> Prices) NewContext(params string[] claims)
    {
        var ctx = PageTestContext.Create(claims);
        var prices = new Mock<IInstrumentPricesApiClient>();
        var instruments = new Mock<IInstrumentsApiClient>();
        instruments.Setup(i => i.GetAsync(Equinor, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingInstrument>.Success(new ExistingInstrument
            {
                InstrumentId = Equinor, Symbol = "EQNR", Name = "Equinor", Type = InstrumentType.Stock, CurrencyCode = "NOK",
            }, HttpStatusCode.OK));
        prices.Setup(p => p.CreateAsync(It.IsAny<NewInstrumentPrice>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingInstrumentPrice>.Failure(HttpStatusCode.Conflict, new ApiProblem
            {
                Status = 409,
                Detail = "A manual price already exists for this date.",
                Code = InstrumentPriceDialog.PriceDuplicateCode,
                Errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                {
                    ["TradingDate"] = ["A manual price already exists for this date."],
                },
                Extensions = new Dictionary<string, JsonElement>
                {
                    [InstrumentPriceDialog.ExistingPriceIdExtension] = JsonSerializer.SerializeToElement(ExistingPrice),
                },
            }));
        ctx.Services.AddSingleton(prices.Object);
        ctx.Services.AddSingleton(instruments.Object);
        ctx.Services.AddSingleton<ISnackbar>(new RecordingSnackbar());
        ctx.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero)));
        return (ctx, prices);
    }

    private static async Task<IRenderedComponent<MudDialogProvider>> SubmitDuplicateAsync(BunitContext ctx, List<Guid> editRequests)
    {
        ctx.Render<MudPopoverProvider>();
        var cut = ctx.Render<MudDialogProvider>();
        ctx.Render<InstrumentPriceDialog>(p => p
            .Add(d => d.Open, true)
            .Add(d => d.LockedInstrumentId, Equinor)
            .Add(d => d.OnEditExisting, (Guid id) => editRequests.Add(id)));

        cut.WaitForAssertion(() => Assert.Contains("EQNR · Equinor", cut.Markup));
        await cut.InvokeAsync(() => cut.FindComponent<Odyssey.Client.Components.OdsAmountField>().Instance.ValueChanged.InvokeAsync("274.5"));
        var submit = cut.FindAll("button").First(b => b.TextContent.Contains("Create price"));
        await cut.InvokeAsync(() => submit.Click());
        cut.WaitForAssertion(() => Assert.Contains("A manual price already exists for this date.", cut.Markup));
        return cut;
    }

    [Fact]
    public async Task A_duplicate_offers_Edit_that_price_to_an_update_holder_and_hands_over_the_existing_id()
    {
        var (ctx, prices) = NewContext(PermissionClaims.InstrumentPricesCreate, PermissionClaims.InstrumentPricesUpdate);
        await using var _ = ctx;
        var requests = new List<Guid>();
        var cut = await SubmitDuplicateAsync(ctx, requests);

        prices.Verify(p => p.CreateAsync(It.Is<NewInstrumentPrice>(n => n.InstrumentId == Equinor && n.Close == 274.5m && n.TradingDate == Today),
            It.IsAny<CancellationToken>()), Times.Once);

        var edit = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Edit that price");
        await cut.InvokeAsync(() => edit.Click());
        Assert.Equal([ExistingPrice], requests);
    }

    [Fact]
    public async Task Edit_that_price_is_absent_without_the_update_claim()
    {
        var (ctx, _) = NewContext(PermissionClaims.InstrumentPricesCreate);
        await using var __ = ctx;
        var cut = await SubmitDuplicateAsync(ctx, []);

        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Trim() == "Edit that price");
    }

    [Fact]
    public async Task The_close_carries_the_currency_suffix_and_the_decimals_help()
    {
        var (ctx, _) = NewContext(PermissionClaims.InstrumentPricesCreate);
        await using var __ = ctx;
        ctx.Render<MudPopoverProvider>();
        var cut = ctx.Render<MudDialogProvider>();
        ctx.Render<InstrumentPriceDialog>(p => p.Add(d => d.Open, true).Add(d => d.LockedInstrumentId, Equinor));

        cut.WaitForAssertion(() => Assert.Equal("NOK", cut.Find(".odc-amount-adorn.suf").TextContent));
        Assert.Contains($"Up to {InstrumentPriceRules.MaxDecimals} decimals", cut.Markup);
        var locked = cut.Find("input[readonly]");
        Assert.Equal("true", locked.GetAttribute("aria-readonly"));
    }
}
