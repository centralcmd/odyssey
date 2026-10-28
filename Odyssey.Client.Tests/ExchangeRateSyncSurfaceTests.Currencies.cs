using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Odyssey.Client.Pages.Finance.ExchangeRateSync;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The sync's currency rows, which live on /currencies (issue #351): who sees them, the row's sync
/// states, start and stop from the row menu with their refusals, the start date in Edit currency, and the purge.
/// </summary>
public partial class ExchangeRateSyncSurfaceTests
{
    private static readonly string[] AdminClaims =
    [
        PermissionClaims.CurrenciesRead, PermissionClaims.CurrenciesUpdate,
        PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate, PermissionClaims.ExchangeRatesDelete,
    ];

    /// <summary>
    /// A currency row carrying <paramref name="sync"/> and the status the server derives from it with the
    /// shared rule; an archived one carries no projection, as the server sends it.
    /// </summary>
    private static ExistingCurrency Row(ExistingExchangeRateSyncCurrency? sync, string? code = null, bool archived = false) => new()
    {
        CurrencyCode = code ?? sync!.CurrencyCode, Name = (code ?? sync!.CurrencyCode) + " name", Symbol = "¤", MinorUnits = 2,
        Archived = archived ? Now.UtcDateTime : null,
        Sync = archived ? null : sync,
        Status = CurrencyStatuses.Of(archived, archived ? null : sync),
    };

    private static Mock<ICurrenciesApiClient> CurrenciesReturning(params ExistingCurrency[] rows)
    {
        var currencies = new Mock<ICurrenciesApiClient>();
        currencies.Setup(c => c.ListAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ApiResult<PagedResult<ExistingCurrency>>.Success(
                new PagedResult<ExistingCurrency> { Items = [.. rows.Select(r => r with { })], TotalCount = rows.Length }, HttpStatusCode.OK));
        currencies.Setup(c => c.ListAllAsync(It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<ExistingCurrency>>.Success([.. rows], HttpStatusCode.OK));
        return currencies;
    }

    private static void VerifyListed(Mock<ICurrenciesApiClient> currencies, Times times) =>
        currencies.Verify(c => c.ListAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()), times);

    private (BunitContext Ctx, Mock<IExchangeRateSyncApiClient> Sync, Mock<ICurrenciesApiClient> Currencies) NewCurrenciesContext(
        string[] claims, params ExistingCurrency[] rows)
    {
        var sync = ExchangeRateSyncFakes.Client(Scheduled());
        var currencies = CurrenciesReturning(rows);
        var (ctx, _) = NewContext(sync.Object);
        ctx.Services.AddSingleton(currencies.Object);
        ctx.Services.AddSingleton(new Mock<IPageStateService>().Object);
        ctx.Services.AddSingleton(Mock.Of<IClipboardService>());
        ctx.Services.AddSingleton(Mock.Of<IReferenceDataCache>());
        ctx.Services.AddSingleton<AuthenticationStateProvider>(new SignedInWith(claims));
        _popover = ctx.Render<MudPopoverProvider>();
        _dialogs = ctx.Render<MudDialogProvider>();
        ctx.Render<MudSnackbarProvider>();
        CurrenciesCard.InteractiveCheck = static () => true;
        return (ctx, sync, currencies);
    }

    private (BunitContext Ctx, Mock<IExchangeRateSyncApiClient> Sync, Mock<ICurrenciesApiClient> Currencies) NewCurrenciesContext(
        params ExistingCurrency[] rows) => NewCurrenciesContext(AdminClaims, rows);

    private static IRenderedComponent<CurrenciesCard> RenderCurrencies(BunitContext ctx, string code)
    {
        var cut = ctx.Render<CurrenciesCard>();
        cut.WaitForAssertion(() => Assert.Contains(code + " name", cut.Markup));
        return cut;
    }

    /// <summary>The currency's record card — header and, while open, its body.</summary>
    private static AngleSharp.Dom.IElement RowOf(IRenderedComponent<CurrenciesCard> cut, string code) =>
        cut.Find($"#cur-{code}");

    private static AngleSharp.Dom.IElement MenuTrigger(IRenderedComponent<CurrenciesCard> cut, string code) =>
        RowOf(cut, code).QuerySelector($"button[aria-label='Actions for {code}']")!;

    /// <summary>Expands the currency's card (one is open at a time).</summary>
    private static void Open(IRenderedComponent<CurrenciesCard> cut, string code)
    {
        RowOf(cut, code).QuerySelector(".odc-record-trigger")!.Click();
        cut.WaitForAssertion(() => Assert.Equal("true", RowOf(cut, code).QuerySelector(".odc-record-trigger")!.GetAttribute("aria-expanded")));
    }

    /// <summary>The menu's item labels for a row, read by opening and closing its ⋯ menu.</summary>
    private string MenuOf(IRenderedComponent<CurrenciesCard> cut, string code)
    {
        AngleSharp.Dom.IElement Trigger() => MenuTrigger(cut, code);
        Trigger().Click();
        var popover = _popover!;
        popover.WaitForAssertion(() => Assert.Contains("Copy ID", popover.Markup));
        var text = popover.Markup;
        Trigger().Click();
        popover.WaitForAssertion(() => Assert.DoesNotContain("Copy ID", popover.Markup));
        return text;
    }

    /// <summary>Opens a row's ⋯ menu and clicks the item labelled <paramref name="label"/>.</summary>
    private void ClickRowAction(IRenderedComponent<CurrenciesCard> cut, string code, string label)
    {
        MenuTrigger(cut, code).Click();
        var popover = _popover!;
        popover.WaitForAssertion(() => Assert.Contains(label, popover.Markup));
        popover.FindAll(".mud-menu-item, [role=menuitem]").First(i => i.TextContent.Contains(label)).Click();
    }

    private static string AssertiveText(IRenderedComponent<CurrenciesCard> cut) =>
        cut.Find("[role=alert][aria-live=assertive]").TextContent;

    private static string PoliteText(IRenderedComponent<CurrenciesCard> cut) =>
        string.Concat(cut.FindAll("[role=status][aria-live=polite]").Select(e => e.TextContent));

    // ── Who sees it ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Without_system_settings_read_the_page_has_no_sync_part_and_asks_for_none()
    {
        var (ctx, sync, currencies) = NewCurrenciesContext([PermissionClaims.CurrenciesRead], Row(null, "USD"));
        await using var __ = ctx;

        var cut = RenderCurrencies(ctx, "USD");

        Assert.DoesNotContain("Coverage", cut.Markup);
        Assert.DoesNotContain("Run history", cut.Markup);
        Assert.DoesNotContain("Exchange-rate sync", cut.Markup);
        Assert.Empty(cut.FindComponents<ExchangeRateSyncStatusPanel>());
        sync.VerifyNoOtherCalls();
        // The status is everyone's: the row shows it, with no other sync part.
        Assert.Contains("Syncing off", RowOf(cut, "USD").TextContent);
    }

    [Fact]
    public async Task A_sync_reader_gets_the_row_parts_the_overview_panel_and_run_history_but_no_sync_actions()
    {
        var (ctx, sync, _) = NewCurrenciesContext(
            [PermissionClaims.CurrenciesRead, PermissionClaims.SystemSettingsRead], Row(Currency("USD", enabled: true)));
        await using var __ = ctx;

        var cut = RenderCurrencies(ctx, "USD");

        // The coverage ends the meta line; the sync tiles are in the body.
        Assert.Contains("Not fetched yet", RowOf(cut, "USD").QuerySelector(".odc-record-meta")!.TextContent);
        Open(cut, "USD");
        Assert.Contains("Coverage", RowOf(cut, "USD").TextContent);
        Assert.Contains("Run history", cut.Markup);
        Assert.Single(cut.FindComponents<ExchangeRateSyncStatusPanel>());
        Assert.Contains("Synced", RowOf(cut, "USD").TextContent);
        var menu = MenuOf(cut, "USD");
        Assert.DoesNotContain("Stop sync", menu);
        sync.Verify(c => c.ListRunsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── The header menu: the sync's two actions ───────────────────────────────────────────────────

    private const string RunNowItem = "Run exchange-rate sync job now";
    private const string RefreshItem = "Refresh exchange-rate sync status";

    private static AngleSharp.Dom.IElement? HeaderMenuTrigger(IRenderedComponent<CurrenciesCard> cut) =>
        cut.FindAll("button[aria-label='More actions']").SingleOrDefault();

    /// <summary>Opens the page header's ⋯ menu and returns its markup.</summary>
    private string OpenHeaderMenu(IRenderedComponent<CurrenciesCard> cut)
    {
        HeaderMenuTrigger(cut)!.Click();
        var popover = _popover!;
        popover.WaitForAssertion(() => Assert.Contains(RefreshItem, popover.Markup));
        return popover.Markup;
    }

    private void ClickHeaderItem(IRenderedComponent<CurrenciesCard> cut, string label)
    {
        OpenHeaderMenu(cut);
        _popover!.FindAll(".mud-menu-item, [role=menuitem]").First(i => i.TextContent.Contains(label)).Click();
    }

    [Fact]
    public async Task Without_system_settings_read_the_header_has_no_menu()
    {
        var (ctx, _, _) = NewCurrenciesContext([PermissionClaims.CurrenciesRead], Row(null, "USD"));
        await using var __ = ctx;

        var cut = RenderCurrencies(ctx, "USD");

        Assert.Null(HeaderMenuTrigger(cut));
    }

    [Fact]
    public async Task A_sync_reader_can_refresh_status_from_the_header_but_not_run_the_job()
    {
        var (ctx, sync, _) = NewCurrenciesContext(
            [PermissionClaims.CurrenciesRead, PermissionClaims.SystemSettingsRead], Row(Currency("USD", enabled: true)));
        await using var __ = ctx;
        var cut = RenderCurrencies(ctx, "USD");
        cut.WaitForAssertion(() => Assert.NotNull(cut.FindComponent<ExchangeRateSyncStatusPanel>().Instance.Status));
        var reads = sync.Invocations.Count(i => i.Method.Name == nameof(IExchangeRateSyncApiClient.GetStatusAsync));

        Assert.DoesNotContain(RunNowItem, OpenHeaderMenu(cut));
        _popover!.FindAll(".mud-menu-item, [role=menuitem]").First(i => i.TextContent.Contains(RefreshItem)).Click();

        cut.WaitForAssertion(() => Assert.Equal(reads + 1,
            sync.Invocations.Count(i => i.Method.Name == nameof(IExchangeRateSyncApiClient.GetStatusAsync))));
    }

    [Fact]
    public async Task A_settings_writer_runs_the_job_from_the_header()
    {
        var (ctx, sync, _) = NewCurrenciesContext(Row(Currency("USD", enabled: true)));
        await using var __ = ctx;
        var cut = RenderCurrencies(ctx, "USD");
        cut.WaitForAssertion(() => Assert.NotNull(cut.FindComponent<ExchangeRateSyncStatusPanel>().Instance.Status));

        ClickHeaderItem(cut, RunNowItem);

        cut.WaitForAssertion(() => sync.Verify(c => c.RunNowAsync(It.IsAny<CancellationToken>()), Times.Once));
    }

    [Fact]
    public async Task Run_now_is_omitted_from_the_header_while_it_cools_down_and_the_panel_says_why()
    {
        var (ctx, sync, _) = NewCurrenciesContext(Row(Currency("USD", enabled: true)));
        sync.Setup(c => c.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(ApiResult<ExchangeRateSyncStatus>.Success(
            Scheduled() with { ManualRunAvailableAtUtc = Now.UtcDateTime.AddMinutes(5) }, HttpStatusCode.OK));
        await using var __ = ctx;
        var cut = RenderCurrencies(ctx, "USD");
        cut.WaitForAssertion(() => Assert.Contains("You can start another run at", cut.Markup));

        Assert.DoesNotContain(RunNowItem, OpenHeaderMenu(cut));
    }

    [Fact]
    public async Task Run_now_chosen_with_the_overview_collapsed_opens_it_and_then_runs()
    {
        var (ctx, sync, _) = NewCurrenciesContext(Row(Currency("USD", enabled: true)));
        await using var __ = ctx;
        var cut = RenderCurrencies(ctx, "USD");
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Overview").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindComponents<ExchangeRateSyncStatusPanel>()));

        ClickHeaderItem(cut, RunNowItem);

        cut.WaitForAssertion(() => Assert.Single(cut.FindComponents<ExchangeRateSyncStatusPanel>()));
        cut.WaitForAssertion(() => sync.Verify(c => c.RunNowAsync(It.IsAny<CancellationToken>()), Times.Once));
    }

    // ── The list: loaded whole, revealed N at a time, ending in an add row ────────────────────────

    [Fact]
    public async Task The_list_loads_every_currency_and_ends_in_an_add_row_with_no_pager()
    {
        var (ctx, _, currencies) = NewCurrenciesContext(
            [PermissionClaims.CurrenciesRead, PermissionClaims.CurrenciesCreate], Row(null, "USD"));
        await using var __ = ctx;

        var cut = RenderCurrencies(ctx, "USD");

        currencies.Verify(c => c.ListAsync(1, OdsPageSizes.All, It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        Assert.Empty(cut.FindComponents<OdsPager>());
        Assert.Equal("New currency", cut.Find(".odc-add-row .odc-add-row-title").TextContent);
        Assert.Equal(OdsPageSizes.Batch, cut.FindComponent<OdsPageSizeSelect>().Instance.Options);
    }

    [Fact]
    public async Task A_reader_who_cannot_create_gets_no_add_row()
    {
        var (ctx, _, _) = NewCurrenciesContext([PermissionClaims.CurrenciesRead], Row(null, "USD"));
        await using var __ = ctx;

        var cut = RenderCurrencies(ctx, "USD");

        Assert.Empty(cut.FindAll(".odc-add-row"));
    }

    // ── The row's sync parts and the menu ────────────────────────────────────────────────────────

    [Fact]
    public async Task The_base_row_and_an_unpublished_row_explain_themselves_and_offer_no_start()
    {
        var (ctx, _, _) = NewCurrenciesContext(
            Row(Currency("BASE", enabled: true, locked: true)), Row(Currency("XDR", supported: false)), Row(Currency("RUB", enabled: true, supported: false)));
        await using var __ = ctx;

        var cut = RenderCurrencies(ctx, "BASE");

        Assert.Contains("Synced", RowOf(cut, "BASE").TextContent);
        Assert.Contains("Not published", RowOf(cut, "XDR").TextContent);
        // A synced currency the provider dropped says so in its header, not only when opened.
        Assert.Contains("No longer published", RowOf(cut, "RUB").QuerySelector(".odc-record-head")!.TextContent);

        Open(cut, "BASE");
        Assert.Contains("Provider’s base — always synced", RowOf(cut, "BASE").TextContent);
        Open(cut, "RUB");
        Assert.Contains("Provider no longer publishes RUB.", RowOf(cut, "RUB").TextContent);
        Assert.Equal("false", RowOf(cut, "BASE").QuerySelector(".odc-record-trigger")!.GetAttribute("aria-expanded"));

        var baseMenu = MenuOf(cut, "BASE");
        Assert.DoesNotContain("sync", baseMenu.Replace("Copy ID", string.Empty), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Start sync", MenuOf(cut, "XDR"));
        Assert.Contains("Stop sync", MenuOf(cut, "RUB"));
    }

    [Fact]
    public async Task The_header_counts_fetched_rates_and_the_body_names_the_source_and_the_earliest_rate()
    {
        var (ctx, _, _) = NewCurrenciesContext(
            Row(Currency("USD", enabled: true) with { FetchedRowCount = 1717 }),
            Row(Currency("BASE", enabled: true, locked: true)),
            Row(Currency("SEK")));
        await using var __ = ctx;

        var cut = RenderCurrencies(ctx, "USD");

        string Counts(string code) => RowOf(cut, code).QuerySelector(".odc-record-counts")?.TextContent ?? string.Empty;
        Assert.Contains("1,717", Counts("USD"));
        Assert.Contains("Fetched rates", Counts("USD"));
        // The base has no rates of its own; a currency never synced holds none.
        Assert.Empty(Counts("BASE"));
        Assert.Empty(Counts("SEK"));
        // The source is named once, in the panel, not on every row's header.
        Assert.DoesNotContain("Provider", RowOf(cut, "USD").QuerySelector(".odc-record-head")!.TextContent);

        Open(cut, "USD");
        var body = RowOf(cut, "USD").TextContent;
        Assert.Contains("from Provider", body);
        Assert.Contains("Earliest rate", body);
        Assert.Contains("first day Provider publishes", body);
        Assert.DoesNotContain("Available from", body);
    }

    [Fact]
    public async Task An_archived_row_reads_archived_has_no_sync_detail_and_no_sync_actions()
    {
        var (ctx, _, _) = NewCurrenciesContext(Row(null, "CHF", archived: true));
        await using var __ = ctx;

        var cut = RenderCurrencies(ctx, "CHF");

        Assert.Contains("Archived", RowOf(cut, "CHF").QuerySelector(".odc-record-head")!.TextContent);
        Open(cut, "CHF");
        Assert.DoesNotContain("Coverage", RowOf(cut, "CHF").TextContent);
        var menu = MenuOf(cut, "CHF");
        Assert.DoesNotContain("Start sync", menu);
    }

    [Fact]
    public async Task The_one_status_filter_is_sent_as_status_names_and_offers_every_status()
    {
        var (ctx, _, currencies) = NewCurrenciesContext([PermissionClaims.CurrenciesRead], Row(Currency("USD")));
        await using var __ = ctx;
        var cut = RenderCurrencies(ctx, "USD");

        var filters = cut.FindComponents<Odyssey.Client.Components.OdsMultiSelect>();
        var status = Assert.Single(filters);
        Assert.Equal("Status", status.Instance.Label);
        Assert.Equal(Enum.GetNames<CurrencyStatus>(), status.Instance.Options.Select(o => o.Value));
        await cut.InvokeAsync(() => status.Instance.ValuesChanged.InvokeAsync(["Synced"]));

        currencies.Verify(c => c.ListAsync(1, It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.Is<IReadOnlyCollection<string>?>(s => s != null && s.SequenceEqual(new[] { "Synced" })),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Start and stop ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_started_sync_patches_the_row_in_place_announces_it_and_rereads_status()
    {
        var (ctx, sync, currencies) = NewCurrenciesContext(Row(Currency("USD")), Row(Currency("EUR")));
        await using var __ = ctx;
        sync.Setup(c => c.UpdateCurrencyAsync("USD", It.IsAny<ExchangeRateSyncCurrencyUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingExchangeRateSyncCurrency>.Success(Currency("USD", enabled: true), HttpStatusCode.OK));

        var cut = RenderCurrencies(ctx, "USD");
        cut.WaitForAssertion(() => sync.Verify(c => c.GetStatusAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce));
        var reads = sync.Invocations.Count(i => i.Method.Name == nameof(IExchangeRateSyncApiClient.GetStatusAsync));
        ClickRowAction(cut, "USD", "Start sync");

        cut.WaitForAssertion(() => Assert.Contains("Synced", RowOf(cut, "USD").TextContent));
        sync.Verify(c => c.UpdateCurrencyAsync("USD", It.Is<ExchangeRateSyncCurrencyUpdate>(u => u.Enabled == true), It.IsAny<CancellationToken>()), Times.Once);
        cut.WaitForAssertion(() => Assert.Contains("Sync started for USD", PoliteText(cut)));
        cut.WaitForAssertion(() => sync.Verify(c => c.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Exactly(reads + 1)));
        // No re-query: the list was read once, on load.
        VerifyListed(currencies, Times.Once());
    }

    [Fact]
    public async Task A_refused_start_leaves_the_row_unchanged_with_the_servers_reason()
    {
        var (ctx, sync, _) = NewCurrenciesContext(Row(Currency("USD")));
        await using var __ = ctx;
        sync.Setup(c => c.UpdateCurrencyAsync("USD", It.IsAny<ExchangeRateSyncCurrencyUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingExchangeRateSyncCurrency>.Failure(HttpStatusCode.UnprocessableEntity, new ApiProblem
            {
                Title = "Unprocessable", Errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { ["enabled"] = ["The provider doesn't publish USD."] },
            }));

        var cut = RenderCurrencies(ctx, "USD");
        ClickRowAction(cut, "USD", "Start sync");

        // A refusal opens the row: the reason is its alert, never hidden behind a collapsed card.
        cut.WaitForAssertion(() => Assert.Contains("The provider doesn't publish USD.", RowOf(cut, "USD").TextContent));
        Assert.Equal("true", RowOf(cut, "USD").QuerySelector(".odc-record-trigger")!.GetAttribute("aria-expanded"));
        Assert.Contains("Syncing off", RowOf(cut, "USD").TextContent);
        cut.WaitForAssertion(() => Assert.Contains("Couldn’t start syncing USD.", AssertiveText(cut)));
    }

    [Fact]
    public async Task A_start_the_provider_could_not_confirm_names_the_currency_on_its_row()
    {
        var (ctx, sync, _) = NewCurrenciesContext(Row(Currency("USD")));
        await using var __ = ctx;
        sync.Setup(c => c.UpdateCurrencyAsync("USD", It.IsAny<ExchangeRateSyncCurrencyUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Refused<ExistingExchangeRateSyncCurrency>(HttpStatusCode.ServiceUnavailable));

        var cut = RenderCurrencies(ctx, "USD");
        ClickRowAction(cut, "USD", "Start sync");

        cut.WaitForAssertion(() => Assert.Contains("Couldn’t confirm Provider publishes USD. Try again shortly.", RowOf(cut, "USD").TextContent));
    }

    [Fact]
    public async Task A_stop_refused_for_permission_says_so_and_leaves_no_row_error()
    {
        var (ctx, sync, _) = NewCurrenciesContext(Row(Currency("USD", enabled: true)));
        await using var __ = ctx;
        sync.Setup(c => c.UpdateCurrencyAsync("USD", It.IsAny<ExchangeRateSyncCurrencyUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Refused<ExistingExchangeRateSyncCurrency>(HttpStatusCode.Forbidden));

        var cut = RenderCurrencies(ctx, "USD");
        ClickRowAction(cut, "USD", "Stop sync");

        cut.WaitForAssertion(() => Assert.Equal(ExchangeRateSyncStatusPanel.PermissionMessage, AssertiveText(cut)));
        Assert.Empty(RowOf(cut, "USD").QuerySelectorAll(".mud-alert"));
    }

    [Fact]
    public async Task A_start_on_a_currency_that_is_gone_relists_the_page()
    {
        var (ctx, sync, currencies) = NewCurrenciesContext(Row(Currency("USD")));
        await using var __ = ctx;
        sync.Setup(c => c.UpdateCurrencyAsync("USD", It.IsAny<ExchangeRateSyncCurrencyUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Refused<ExistingExchangeRateSyncCurrency>(HttpStatusCode.NotFound));

        var cut = RenderCurrencies(ctx, "USD");
        ClickRowAction(cut, "USD", "Start sync");

        cut.WaitForAssertion(() => VerifyListed(currencies, Times.Exactly(2)));
    }

    [Fact]
    public async Task A_second_start_while_the_first_is_saving_sends_nothing_and_the_row_says_saving()
    {
        var (ctx, sync, _) = NewCurrenciesContext(Row(Currency("USD")));
        await using var __ = ctx;
        var pending = new TaskCompletionSource<ApiResult<ExistingExchangeRateSyncCurrency>>();
        sync.Setup(c => c.UpdateCurrencyAsync("USD", It.IsAny<ExchangeRateSyncCurrencyUpdate>(), It.IsAny<CancellationToken>()))
            .Returns(pending.Task);

        var cut = RenderCurrencies(ctx, "USD");
        ClickRowAction(cut, "USD", "Start sync");
        cut.WaitForAssertion(() => Assert.Contains("Saving…", RowOf(cut, "USD").TextContent));
        // The menu closes at once rather than waiting out the request.
        Assert.DoesNotContain("Start sync", _popover!.Markup);
        ClickRowAction(cut, "USD", "Start sync");

        sync.Verify(c => c.UpdateCurrencyAsync("USD", It.IsAny<ExchangeRateSyncCurrencyUpdate>(), It.IsAny<CancellationToken>()), Times.Once);
        await cut.InvokeAsync(() => pending.SetResult(ApiResult<ExistingExchangeRateSyncCurrency>.Success(Currency("USD", enabled: true), HttpStatusCode.OK)));
        cut.WaitForAssertion(() => Assert.DoesNotContain("Saving…", RowOf(cut, "USD").TextContent));
    }

    // ── Start date, in Edit currency ─────────────────────────────────────────────────────────────

    private sealed record DialogEvents(List<string> SyncStartSaved, List<bool> Saved);

    private static IRenderedComponent<CreateCurrencyDialog> RenderEditDialog(
        BunitContext ctx, ExistingCurrency currency, bool editable, DialogEvents events) =>
        ctx.Render<CreateCurrencyDialog>(p => p
            .Add(d => d.Open, true)
            .Add(d => d.Currency, currency)
            .Add(d => d.SyncStartEditable, editable)
            .Add(d => d.OnSyncStartSaved, (string code) => events.SyncStartSaved.Add(code))
            .Add(d => d.OnSaved, () => events.Saved.Add(true)));

    private void SubmitDialog() =>
        Dialogs.FindAll("button").Single(b => b.TextContent.Contains("Save changes")).Click();

    [Fact]
    public async Task The_sync_start_date_is_in_edit_currency_only_for_a_settings_writer_and_a_dated_currency()
    {
        var (ctx, _, _) = NewCurrenciesContext(Row(Currency("USD")));
        await using var __ = ctx;
        var events = new DialogEvents([], []);

        RenderEditDialog(ctx, Row(Currency("USD")), editable: true, events);
        Dialogs.WaitForAssertion(() => Assert.Contains("Sync start date", Dialogs.Markup));
        Assert.Contains("Default (", Dialogs.Markup);

        foreach (var (row, editable) in new[]
        {
            (Row(Currency("USD")), false),
            (Row(Currency("BASE", enabled: true, locked: true)), true),
            (Row(null, "CHF", archived: true), true),
        })
        {
            var (other, _, _) = NewCurrenciesContext(row);
            await using var ___ = other;
            RenderEditDialog(other, row, editable, events);
            Dialogs.WaitForAssertion(() => Assert.Contains("Edit currency", Dialogs.Markup));
            Assert.DoesNotContain("Sync start date", Dialogs.Markup);
        }
    }

    private (BunitContext Ctx, Mock<IExchangeRateSyncApiClient> Sync, Mock<ICurrenciesApiClient> Currencies) NewEditContext()
    {
        var (ctx, sync, currencies) = NewCurrenciesContext(Row(Currency("USD", enabled: true)));
        currencies.Setup(c => c.UpdateAsync("USD", It.IsAny<NewCurrency>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.NoContent));
        sync.Setup(c => c.UpdateCurrencyAsync("USD", It.IsAny<ExchangeRateSyncCurrencyUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingExchangeRateSyncCurrency>.Success(Currency("USD", enabled: true), HttpStatusCode.OK));
        return (ctx, sync, currencies);
    }

    [Fact]
    public async Task An_unchanged_start_date_sends_only_the_currency()
    {
        var (ctx, sync, currencies) = NewEditContext();
        await using var __ = ctx;
        var events = new DialogEvents([], []);

        var cut = RenderEditDialog(ctx, Row(Currency("USD", enabled: true)), editable: true, events);
        Dialogs.WaitForAssertion(() => Assert.Contains("Sync start date", Dialogs.Markup));
        SubmitDialog();

        cut.WaitForAssertion(() => Assert.Single(events.Saved));
        currencies.Verify(c => c.UpdateAsync("USD", It.IsAny<NewCurrency>(), It.IsAny<CancellationToken>()), Times.Once);
        sync.Verify(c => c.UpdateCurrencyAsync(It.IsAny<string>(), It.IsAny<ExchangeRateSyncCurrencyUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(events.SyncStartSaved);
    }

    [Fact]
    public async Task A_changed_start_date_is_saved_after_the_currency_keeping_the_sync_state()
    {
        var (ctx, sync, currencies) = NewEditContext();
        await using var __ = ctx;
        var events = new DialogEvents([], []);

        var cut = RenderEditDialog(ctx, Row(Currency("USD", enabled: true)), editable: true, events);
        Dialogs.WaitForAssertion(() => Assert.Contains("Sync start date", Dialogs.Markup));
        await Dialogs.InvokeAsync(() => Dialogs.FindComponent<Odyssey.Client.Components.OdsDateField>().Instance.ValueChanged.InvokeAsync(new DateTime(2020, 1, 1)));
        SubmitDialog();

        cut.WaitForAssertion(() => Assert.Equal(["USD"], events.SyncStartSaved));
        currencies.Verify(c => c.UpdateAsync("USD", It.IsAny<NewCurrency>(), It.IsAny<CancellationToken>()), Times.Once);
        sync.Verify(c => c.UpdateCurrencyAsync("USD",
            It.Is<ExchangeRateSyncCurrencyUpdate>(u => u.Enabled == true && u.HistoryStartDate == new DateOnly(2020, 1, 1)), It.IsAny<CancellationToken>()), Times.Once);
        cut.WaitForAssertion(() => Assert.Single(events.Saved));
    }

    [Fact]
    public async Task Clearing_the_start_date_sends_the_default_and_the_page_announces_the_save()
    {
        var own = Currency("USD", enabled: true) with { HistoryStartDate = new DateOnly(2020, 1, 1) };
        var (ctx, sync, currencies) = NewCurrenciesContext(Row(own));
        await using var __ = ctx;
        currencies.Setup(c => c.UpdateAsync("USD", It.IsAny<NewCurrency>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.NoContent));
        sync.Setup(c => c.UpdateCurrencyAsync("USD", It.IsAny<ExchangeRateSyncCurrencyUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingExchangeRateSyncCurrency>.Success(own with { HistoryStartDate = null }, HttpStatusCode.OK));

        var cut = RenderCurrencies(ctx, "USD");
        MenuTrigger(cut, "USD").Click();
        var popover = _popover!;
        popover.WaitForAssertion(() => Assert.Contains("Edit", popover.Markup));
        popover.FindAll(".mud-menu-item, [role=menuitem]").First(i => i.TextContent.Trim().EndsWith("Edit", StringComparison.Ordinal)).Click();
        Dialogs.WaitForAssertion(() => Assert.Contains("Sync start date", Dialogs.Markup));
        await Dialogs.InvokeAsync(() => Dialogs.FindComponent<Odyssey.Client.Components.OdsDateField>().Instance.ValueChanged.InvokeAsync(null));
        SubmitDialog();

        cut.WaitForAssertion(() => sync.Verify(c => c.UpdateCurrencyAsync("USD",
            It.Is<ExchangeRateSyncCurrencyUpdate>(u => u.Enabled == true && u.HistoryStartDate == null), It.IsAny<CancellationToken>()), Times.Once));
        cut.WaitForAssertion(() => Assert.Contains("Start date for USD saved", PoliteText(cut)));
    }

    [Fact]
    public async Task A_refused_start_date_keeps_the_dialog_open_with_the_servers_message_on_the_field()
    {
        var (ctx, sync, currencies) = NewCurrenciesContext(Row(Currency("USD", enabled: true)));
        await using var __ = ctx;
        currencies.Setup(c => c.UpdateAsync("USD", It.IsAny<NewCurrency>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.NoContent));
        sync.Setup(c => c.UpdateCurrencyAsync("USD", It.IsAny<ExchangeRateSyncCurrencyUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingExchangeRateSyncCurrency>.Failure(HttpStatusCode.UnprocessableEntity, new ApiProblem
            {
                Title = "Unprocessable",
                Errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { ["historyStartDate"] = ["Too early for this currency."] },
            }));
        var events = new DialogEvents([], []);

        var cut = RenderEditDialog(ctx, Row(Currency("USD", enabled: true)), editable: true, events);
        Dialogs.WaitForAssertion(() => Assert.Contains("Sync start date", Dialogs.Markup));
        await Dialogs.InvokeAsync(() => Dialogs.FindComponent<Odyssey.Client.Components.OdsDateField>().Instance.ValueChanged.InvokeAsync(new DateTime(1990, 1, 1)));
        SubmitDialog();

        Dialogs.WaitForAssertion(() => Assert.Contains("Too early for this currency.", Dialogs.Markup));
        Assert.Contains("Sync start date", Dialogs.Markup);
        Assert.Empty(events.Saved);
        Assert.Empty(events.SyncStartSaved);
    }

    private static IRenderedComponent<CreateCurrencyDialog> RenderNewDialog(BunitContext ctx, bool editable, DialogEvents events) =>
        ctx.Render<CreateCurrencyDialog>(p => p
            .Add(d => d.Open, true)
            .Add(d => d.SyncStartEditable, editable)
            .Add(d => d.DefaultSyncStart, new DateOnly(2026, 1, 1))
            .Add(d => d.OnSyncStartSaved, (string code) => events.SyncStartSaved.Add(code))
            .Add(d => d.OnSaved, () => events.Saved.Add(true)));

    private void FillNewCurrency(string code)
    {
        var fields = Dialogs.FindAll("input");
        // The code field is Immediate: it binds on input, not change.
        fields[0].Input(code);
        fields[1].Change("¤");
        fields[2].Change(code + " name");
    }

    [Fact]
    public async Task New_currency_offers_the_sync_start_date_only_to_a_settings_writer()
    {
        var (ctx, _, _) = NewCurrenciesContext(Row(Currency("USD")));
        await using var __ = ctx;
        var events = new DialogEvents([], []);

        RenderNewDialog(ctx, editable: true, events);
        Dialogs.WaitForAssertion(() => Assert.Contains("Sync start date", Dialogs.Markup));
        Assert.Contains("Default (", Dialogs.Markup);

        var (other, _, _) = NewCurrenciesContext(Row(Currency("USD")));
        await using var ___ = other;
        RenderNewDialog(other, editable: false, events);
        Dialogs.WaitForAssertion(() => Assert.Contains("New currency", Dialogs.Markup));
        Assert.DoesNotContain("Sync start date", Dialogs.Markup);
    }

    [Fact]
    public async Task A_new_currencys_start_date_is_saved_with_sync_off_and_a_refusal_never_creates_it_twice()
    {
        var (ctx, sync, currencies) = NewCurrenciesContext(Row(Currency("USD")));
        await using var __ = ctx;
        currencies.Setup(c => c.CreateAsync(It.IsAny<NewCurrency>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.Created));
        sync.SetupSequence(c => c.UpdateCurrencyAsync("DKK", It.IsAny<ExchangeRateSyncCurrencyUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Refused<ExistingExchangeRateSyncCurrency>(HttpStatusCode.UnprocessableEntity, "Too early."))
            .ReturnsAsync(ApiResult<ExistingExchangeRateSyncCurrency>.Success(Currency("DKK"), HttpStatusCode.OK));
        var events = new DialogEvents([], []);

        RenderNewDialog(ctx, editable: true, events);
        Dialogs.WaitForAssertion(() => Assert.Contains("Sync start date", Dialogs.Markup));
        FillNewCurrency("DKK");
        await Dialogs.InvokeAsync(() => Dialogs.FindComponent<Odyssey.Client.Components.OdsDateField>().Instance.ValueChanged.InvokeAsync(new DateTime(2020, 1, 1)));
        Dialogs.FindAll("button").Single(b => b.TextContent.Contains("Create currency")).Click();

        Dialogs.WaitForAssertion(() => Assert.Contains("Too early.", Dialogs.Markup));
        Assert.Empty(events.Saved);
        Dialogs.FindAll("button").Single(b => b.TextContent.Contains("Create currency")).Click();

        Dialogs.WaitForAssertion(() => Assert.Single(events.Saved));
        Assert.Equal(["DKK"], events.SyncStartSaved);
        currencies.Verify(c => c.CreateAsync(It.Is<NewCurrency>(n => n.CurrencyCode == "DKK"), It.IsAny<CancellationToken>()), Times.Once);
        sync.Verify(c => c.UpdateCurrencyAsync("DKK",
            It.Is<ExchangeRateSyncCurrencyUpdate>(u => u.Enabled == false && u.HistoryStartDate == new DateOnly(2020, 1, 1)), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task A_new_currency_without_a_start_date_sends_no_sync_write()
    {
        var (ctx, sync, currencies) = NewCurrenciesContext(Row(Currency("USD")));
        await using var __ = ctx;
        currencies.Setup(c => c.CreateAsync(It.IsAny<NewCurrency>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.Created));
        var events = new DialogEvents([], []);

        RenderNewDialog(ctx, editable: true, events);
        Dialogs.WaitForAssertion(() => Assert.Contains("Sync start date", Dialogs.Markup));
        FillNewCurrency("DKK");
        Dialogs.FindAll("button").Single(b => b.TextContent.Contains("Create currency")).Click();

        Dialogs.WaitForAssertion(() => Assert.Single(events.Saved));
        sync.Verify(c => c.UpdateCurrencyAsync(It.IsAny<string>(), It.IsAny<ExchangeRateSyncCurrencyUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Each_row_is_a_record_card_named_by_its_code_with_the_example_code_after()
    {
        var (ctx, _, _) = NewCurrenciesContext(Row(Currency("USD")));
        await using var __ = ctx;

        var cut = RenderCurrencies(ctx, "USD");

        // A record card, not a table row (Run history below is still a table).
        Assert.Null(RowOf(cut, "USD").Closest("table"));
        Assert.Contains("odc-record", RowOf(cut, "USD").ClassList);
        var mark = RowOf(cut, "USD").QuerySelector(".odc-record-mark.mono")!;
        Assert.Equal("USD", mark.TextContent.Trim());
        // The code is shown nowhere else, so the mark is text inside the trigger — part of its name.
        Assert.Null(mark.GetAttribute("aria-hidden"));
        Assert.Same(mark.ParentElement, RowOf(cut, "USD").QuerySelector(".odc-record-trigger"));
        Assert.Contains(OdsMoney.Format(1234.5m, "USD", 2), RowOf(cut, "USD").QuerySelector(".odc-record-meta")!.TextContent);
        Assert.EndsWith(" USD", OdsMoney.Format(1234.5m, "USD", 2));
    }

    [Theory]
    [InlineData(true, "USD archived. Its sync was stopped.")]
    [InlineData(false, "Currency updated.")]
    public async Task Archiving_says_when_it_stopped_the_currencys_sync(bool synced, string expected)
    {
        var (ctx, sync, currencies) = NewCurrenciesContext(Row(Currency("USD", enabled: synced)));
        await using var __ = ctx;
        currencies.Setup(c => c.UpdateAsync("USD", It.IsAny<NewCurrency>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.NoContent));
        var cut = RenderCurrencies(ctx, "USD");
        var reads = sync.Invocations.Count(i => i.Method.Name == nameof(IExchangeRateSyncApiClient.GetStatusAsync));

        ClickRowAction(cut, "USD", "Archive");

        cut.WaitForAssertion(() => Assert.Contains(expected,
            ctx.Services.GetRequiredService<ISnackbar>().ShownSnackbars.Select(s => s.Message)));
        currencies.Verify(c => c.UpdateAsync("USD", It.Is<NewCurrency>(n => n.Archived), It.IsAny<CancellationToken>()), Times.Once);
        // The synced count in the header follows a stopped sync.
        if (synced)
            cut.WaitForAssertion(() => Assert.True(sync.Invocations.Count(i => i.Method.Name == nameof(IExchangeRateSyncApiClient.GetStatusAsync)) > reads));
    }

    // ── Purge ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_fetched_rates_is_offered_only_for_an_unsynced_currency_with_fetched_rows()
    {
        var (ctx, _, _) = NewCurrenciesContext(
            Row(Currency("USD") with { FetchedRowCount = 12 }), Row(Currency("EUR", enabled: true) with { FetchedRowCount = 9 }), Row(Currency("SEK")));
        await using var __ = ctx;

        var cut = RenderCurrencies(ctx, "USD");

        Assert.Contains("Delete fetched rates…", MenuOf(cut, "USD"));
        Assert.DoesNotContain("Delete fetched rates…", MenuOf(cut, "EUR"));
        Assert.DoesNotContain("Delete fetched rates…", MenuOf(cut, "SEK"));
    }

    [Fact]
    public async Task A_confirmed_purge_zeroes_the_row_announces_and_rereads_runs_and_status()
    {
        var (ctx, sync, _) = NewCurrenciesContext(Row(Currency("USD") with { FetchedRowCount = 12 }));
        await using var __ = ctx;
        sync.Setup(c => c.PurgeCurrencyAsync("USD", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExchangeRatePurgeResult>.Success(new ExchangeRatePurgeResult { RowsDeleted = 12 }, HttpStatusCode.OK));

        var cut = RenderCurrencies(ctx, "USD");
        cut.WaitForAssertion(() => sync.Verify(c => c.ListRunsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once));
        var reads = sync.Invocations.Count(i => i.Method.Name == nameof(IExchangeRateSyncApiClient.GetStatusAsync));
        ClickRowAction(cut, "USD", "Delete fetched rates…");
        Dialogs.WaitForAssertion(() => Assert.Contains("Delete fetched rates for USD?", Dialogs.Markup));
        Assert.Contains("This removes 12 rates fetched from Provider.", Dialogs.Markup);
        Dialogs.FindAll("button").Single(b => b.TextContent.Contains("Delete fetched rates")).Click();

        cut.WaitForAssertion(() => Assert.Contains("Deleted 12 fetched rates for USD", PoliteText(cut)));
        cut.WaitForAssertion(() =>
        {
            sync.Verify(c => c.ListRunsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
            sync.Verify(c => c.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Exactly(reads + 1));
        });
        Assert.DoesNotContain("Delete fetched rates…", MenuOf(cut, "USD"));
    }

    [Fact]
    public async Task A_refused_purge_keeps_the_dialog_open_with_the_message_and_a_reopen_starts_clean()
    {
        var (ctx, sync, _) = NewCurrenciesContext(Row(Currency("USD") with { FetchedRowCount = 12 }));
        await using var __ = ctx;
        sync.Setup(c => c.PurgeCurrencyAsync("USD", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Refused<ExchangeRatePurgeResult>(HttpStatusCode.Conflict, "A run is in progress."));

        var cut = RenderCurrencies(ctx, "USD");
        ClickRowAction(cut, "USD", "Delete fetched rates…");
        Dialogs.WaitForAssertion(() => Assert.Contains("Delete fetched rates for USD?", Dialogs.Markup));
        Dialogs.FindAll("button").Single(b => b.TextContent.Contains("Delete fetched rates")).Click();

        Dialogs.WaitForAssertion(() => Assert.Contains("A run is in progress.", Dialogs.Find("[role=alert]").TextContent));
        Dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel").Click();
        ClickRowAction(cut, "USD", "Delete fetched rates…");
        Dialogs.WaitForAssertion(() => Assert.Contains("Delete fetched rates for USD?", Dialogs.Markup));
        Assert.DoesNotContain("A run is in progress.", Dialogs.Markup);
    }

    // ── A run ending ─────────────────────────────────────────────────────────────────────────────

    // Bounded: without the guard the superseded query never settles, and a hang is no failure message.
    [Fact(Timeout = 20000)]
    public async Task A_run_ending_during_the_users_own_query_never_supersedes_it()
    {
        var (ctx, sync, currencies) = NewCurrenciesContext(Row(Currency("USD")));
        await using var __ = ctx;
        var reads = new Queue<ExchangeRateSyncStatus>([Running(Now.UtcDateTime), Running(Now.UtcDateTime), Scheduled()]);
        sync.Setup(c => c.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ApiResult<ExchangeRateSyncStatus>.Success(reads.Count > 1 ? reads.Dequeue() : reads.Peek(), HttpStatusCode.OK));

        var cut = RenderCurrencies(ctx, "USD");
        var userQuery = new TaskCompletionSource<ApiResult<PagedResult<ExistingCurrency>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        currencies.Setup(c => c.ListAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()))
            .Returns(userQuery.Task);
        _ = cut.InvokeAsync(() => cut.FindComponents<Odyssey.Client.Components.OdsMultiSelect>()
            .Single(m => m.Instance.Label == "Status").Instance.ValuesChanged.InvokeAsync(["NotPublished"]));
        _time!.Advance(ExchangeRateSyncStatusPanel.PollInterval);
        // The page's own read and the panel's, both Running, then the poll that sees the run end.
        cut.WaitForAssertion(() => sync.Verify(c => c.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Exactly(3)));

        // Initial load + the user's query, and no merge.
        VerifyListed(currencies, Times.Exactly(2));
        await cut.InvokeAsync(() => userQuery.SetResult(ApiResult<PagedResult<ExistingCurrency>>.Success(
            new PagedResult<ExistingCurrency> { Items = [Row(Currency("XDR", supported: false))], TotalCount = 1 }, HttpStatusCode.OK)));
        cut.WaitForAssertion(() => Assert.Contains("XDR name", cut.Markup));
        Assert.DoesNotContain("USD name", cut.Markup);
    }
}
