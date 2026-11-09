using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Pages.Finance.Instruments;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The dashboard Watchlist card (issue #452 §3.2, §3.5, §6): no request before Home has resolved the display
/// currency, the hidden caption naming that currency, Symbol as the row header, one hidden announcement per row
/// with the visual delta and sparkline aria-hidden, and every card and row state.
/// </summary>
public class WatchlistCardTests
{
    static WatchlistCardTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private static readonly DateOnly Today = new(2026, 11, 9);

    private static InstrumentWatchlistItem Item(string symbol, Action<InstrumentWatchlistItem>? shape = null)
    {
        var item = new InstrumentWatchlistItem
        {
            InstrumentId = Guid.NewGuid(),
            Symbol = symbol,
            Name = symbol + " name",
            NativeCurrencyCode = "NOK",
            NativeClose = 284.6m,
            Close = 284.6m,
            LatestTradingDate = Today.AddDays(-1),
            PreviousClose = 281.2m,
            ChangePercent = 1.2m,
            Change = 3.4m,
            Sparkline = [new() { Date = Today.AddDays(-3), Close = 270m }, new() { Date = Today.AddDays(-1), Close = 284.6m }],
        };
        shape?.Invoke(item);
        return item;
    }

    private static (BunitContext Ctx, Mock<IInstrumentsApiClient> Api) NewContext(ApiResult<InstrumentWatchlist> answer)
    {
        var ctx = PageTestContext.Create(PermissionClaims.InstrumentPricesRead);
        var api = new Mock<IInstrumentsApiClient>();
        api.Setup(a => a.GetWatchlistAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(answer);
        ctx.Services.AddSingleton(api.Object);
        ctx.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero)));
        return (ctx, api);
    }

    private static ApiResult<InstrumentWatchlist> Ok(params InstrumentWatchlistItem[] items) =>
        ApiResult<InstrumentWatchlist>.Success(new InstrumentWatchlist { DisplayCurrencyCode = "NOK", Items = [.. items] }, HttpStatusCode.OK);

    private static IRenderedComponent<WatchlistCard> Render(BunitContext ctx, string? currency = "NOK") =>
        ctx.Render<WatchlistCard>(p => p.Add(c => c.DisplayCurrencyCode, currency).Add(c => c.MinorUnits, 2));

    [Fact]
    public async Task No_request_is_made_until_the_display_currency_is_resolved()
    {
        var (ctx, api) = NewContext(Ok(Item("EQNR")));
        await using var _ = ctx;

        var cut = Render(ctx, currency: null);
        Assert.Contains("Loading watchlist", cut.Markup);
        api.Verify(a => a.GetWatchlistAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);

        cut.Render(p => p.Add(c => c.DisplayCurrencyCode, "NOK"));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("tbody th[scope=row]")));
        api.Verify(a => a.GetWatchlistAsync("NOK", WatchlistCard.SparklineDays, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(30, WatchlistCard.SparklineDays);
    }

    [Fact]
    public async Task The_table_is_captioned_with_the_currency_and_rows_are_headed_by_a_linked_symbol()
    {
        var (ctx, _) = NewContext(Ok(Item("EQNR")));
        await using var __ = ctx;
        var cut = Render(ctx);

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("tbody th[scope=row]")));
        var call = Assert.Single(ctx.JSInterop.Invocations, i => i.Identifier == "odsTableCaption");
        Assert.Equal("Watchlist, prices in NOK", call.Arguments[1]);
        Assert.Equal(true, call.Arguments[2]);

        var head = cut.Find("tbody th[scope=row]");
        var link = head.QuerySelector("a")!;
        Assert.Equal("EQNR", link.TextContent);
        Assert.StartsWith("instruments?instrument=", link.GetAttribute("href"));
        Assert.Contains(cut.FindAll("a"), a => a.TextContent.Contains("Manage instruments") && a.GetAttribute("href") == "instruments");
    }

    [Fact]
    public async Task The_change_and_trend_are_announced_once_with_the_visuals_hidden()
    {
        var (ctx, _) = NewContext(Ok(Item("EQNR")));
        await using var __ = ctx;
        var cut = Render(ctx);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("tbody th[scope=row]")));

        var row = cut.Find("tbody tr");
        var spoken = row.QuerySelectorAll(".odc-sr-only").Select(e => e.TextContent.Trim()).ToList();
        Assert.Single(spoken, s => s.StartsWith("Up 1.20 percent", StringComparison.Ordinal));
        Assert.Contains("NOK", spoken.Single(s => s.StartsWith("Up ", StringComparison.Ordinal)));
        Assert.Contains(spoken, s => s == "30-day trend: up 5.4 %");

        Assert.All(row.QuerySelectorAll(".odc-delta"), d => Assert.NotNull(d.Closest("[aria-hidden=true]")));
        Assert.All(row.QuerySelectorAll("svg"), s => Assert.NotNull(s.Closest("[aria-hidden=true]")));
        Assert.NotEmpty(row.QuerySelectorAll("svg"));
    }

    [Fact]
    public async Task Row_variants_read_as_specified()
    {
        var (ctx, _) = NewContext(Ok(
            Item("NOPX", i => { i.NativeClose = null; i.Close = null; i.LatestTradingDate = null; i.ChangePercent = null; i.Change = null; i.Sparkline = []; }),
            Item("TM", i => { i.NativeCurrencyCode = "JPY"; i.Close = null; i.Change = null; i.Unconvertible = true; }),
            Item("STALE", i => { i.IsStale = true; i.LatestTradingDate = Today.AddDays(-4); }),
            Item("NEW", i => { i.ChangePercent = null; i.Change = null; i.PreviousClose = null; }),
            Item("FLAT", i => { i.ChangePercent = 0m; i.Change = 0m; })));
        await using var __ = ctx;
        var cut = Render(ctx);
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll("tbody th[scope=row]").Count));

        string RowText(string symbol) => cut.FindAll("tbody tr").Single(r => r.QuerySelector("th")!.TextContent.Contains(symbol)).TextContent;
        string Spoken(string symbol) => string.Join(" | ", cut.FindAll("tbody tr")
            .Single(r => r.QuerySelector("th")!.TextContent.Contains(symbol)).QuerySelectorAll(".odc-sr-only").Select(e => e.TextContent.Trim()));

        Assert.Contains("No price yet", RowText("NOPX"));
        Assert.Contains("No exchange rate to NOK", RowText("TM"));
        Assert.Contains("JPY", RowText("TM"));
        Assert.Contains("Stale · 4 days", RowText("STALE"));
        Assert.Contains("No previous price", Spoken("NEW"));
        Assert.Contains("No change", Spoken("FLAT"));
        Assert.Contains("0.00 %", RowText("FLAT"));
        Assert.Empty(cut.FindAll("tbody tr").Single(r => r.QuerySelector("th")!.TextContent.Contains("FLAT")).QuerySelectorAll(".odc-delta"));
    }

    [Fact]
    public async Task Empty_truncated_and_attributed_states()
    {
        var (empty, _) = NewContext(Ok());
        await using var _1 = empty;
        var none = Render(empty);
        none.WaitForAssertion(() => Assert.Contains("No instruments on the dashboard yet.", none.Markup));

        var truncated = ApiResult<InstrumentWatchlist>.Success(new InstrumentWatchlist
        {
            DisplayCurrencyCode = "NOK", Truncated = true, Attributions = ["EODHD"], Items = [Item("A"), Item("B")],
        }, HttpStatusCode.OK);
        var (ctx, _) = NewContext(truncated);
        await using var _2 = ctx;
        var cut = Render(ctx);
        cut.WaitForAssertion(() => Assert.Contains("Showing the first 2 followed instruments.", cut.Markup));
        Assert.Contains("Prices from EODHD", cut.Find(".wl-foot").TextContent);
    }

    [Fact]
    public async Task A_rejected_display_currency_points_to_Preferences_and_a_failure_offers_retry()
    {
        var unavailable = ApiResult<InstrumentWatchlist>.Failure(HttpStatusCode.UnprocessableEntity, new ApiProblem
        {
            Status = 422,
            Errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { ["mainCurrency"] = ["Currency 'XYZ' is not available."] },
        });
        var (ctx, _) = NewContext(unavailable);
        await using var _1 = ctx;
        var cut = Render(ctx, "XYZ");
        cut.WaitForAssertion(() => Assert.Contains("Your display currency isn’t available. Change it in Preferences.", cut.Markup));
        Assert.Contains(cut.FindAll("a"), a => a.GetAttribute("href") == "preferences");

        var (failing, api) = NewContext(ApiResult<InstrumentWatchlist>.Failure(HttpStatusCode.InternalServerError, new ApiProblem { Status = 500 }));
        await using var _2 = failing;
        var error = Render(failing);
        error.WaitForAssertion(() => Assert.Contains("Couldn’t load the watchlist.", error.Markup));
        Assert.Equal("alert", error.Find(".wl-msg").GetAttribute("role"));

        api.Setup(a => a.GetWatchlistAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok(Item("EQNR")));
        await error.InvokeAsync(() => error.FindAll("button").Single(b => b.TextContent.Contains("Retry")).Click());
        error.WaitForAssertion(() => Assert.Single(error.FindAll("tbody th[scope=row]")));
    }

    [Theory]
    [InlineData(100, 104.2, "30-day trend: up 4.2 %")]
    [InlineData(100, 95, "30-day trend: down 5.0 %")]
    [InlineData(100, 100.01, "30-day trend: unchanged")]
    public void The_trend_is_put_into_words(double first, double last, string expected)
    {
        var item = Item("X", i => i.Sparkline =
        [
            new() { Date = Today.AddDays(-20), Close = (decimal)first },
            new() { Date = Today.AddDays(-10), Close = null },
            new() { Date = Today, Close = (decimal)last },
        ]);
        Assert.Equal(expected, WatchlistCard.TrendAnnouncement(item));
        Assert.Equal("30-day trend: not enough prices", WatchlistCard.TrendAnnouncement(Item("Y", i => i.Sparkline = [])));
    }

    /// <summary>
    /// Home renders the card only for <c>instrument-prices.read</c> (AC 10: no request without it) and passes the
    /// resolved currency, which stays null until <c>UserPreferences.MainCurrency ?? "NOK"</c> has been read (AC 24).
    /// </summary>
    [Fact]
    public void Home_gates_the_card_and_hands_it_the_resolved_currency()
    {
        var root = ClientSource.Root;
        var markup = File.ReadAllText(Path.Combine(root, "Pages", "Finance", "Home.razor"));
        var code = File.ReadAllText(Path.Combine(root, "Pages", "Finance", "Home.razor.cs"));

        var gate = markup.IndexOf("@if (_canReadInstrumentPrices)", StringComparison.Ordinal);
        var card = markup.IndexOf("WatchlistCard", gate, StringComparison.Ordinal);
        Assert.True(gate >= 0 && card > gate);
        Assert.True(card < markup.IndexOf("Recent transactions\">", StringComparison.Ordinal), "the card sits above Recent transactions");
        Assert.Contains("DisplayCurrencyCode=\"@_resolvedMainCurrency\"", markup);
        Assert.Contains("_canReadInstrumentPrices = user.HasPermission(PermissionClaims.InstrumentPricesRead);", code);
        Assert.Contains("_resolvedMainCurrency = _mainCurrencyCode;", code);
        Assert.Contains("UserPreferences.MainCurrency ?? DefaultMainCurrency", code);
    }
}
