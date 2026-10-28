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
/// The sync's currency rows, which live on /currencies (issue #351): who sees them, the Sync column's
/// states, start and stop from the row menu with their refusals, the start date in Edit currency, and the purge.
/// </summary>
public partial class ExchangeRateSyncSurfaceTests
{
    private static readonly string[] AdminClaims =
    [
        PermissionClaims.CurrenciesRead, PermissionClaims.CurrenciesUpdate,
        PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate, PermissionClaims.ExchangeRatesDelete,
    ];

    /// <summary>A currency row carrying <paramref name="sync"/>; an archived one carries none, as the server sends it.</summary>
    private static ExistingCurrency Row(ExistingExchangeRateSyncCurrency? sync, string? code = null, bool archived = false) => new()
    {
        CurrencyCode = code ?? sync!.CurrencyCode, Name = (code ?? sync!.CurrencyCode) + " name", Symbol = "¤", MinorUnits = 2,
        Archived = archived ? Now.UtcDateTime : null,
        Sync = archived ? null : sync,
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

    private static AngleSharp.Dom.IElement RowOf(IRenderedComponent<CurrenciesCard> cut, string code) =>
        cut.FindAll("tr").First(tr => tr.TextContent.Contains(code + " name"));

    /// <summary>The menu's item labels for a row, read by opening and closing its ⋯ menu.</summary>
    private string MenuOf(IRenderedComponent<CurrenciesCard> cut, string code)
    {
        AngleSharp.Dom.IElement Trigger() => RowOf(cut, code).QuerySelector("button[aria-label='Row actions']")!;
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
        RowOf(cut, code).QuerySelector("button[aria-label='Row actions']")!.Click();
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
        currencies.Verify(c => c.ListAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), null, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task A_sync_reader_gets_the_columns_the_overview_panel_and_run_history_but_no_sync_actions()
    {
        var (ctx, sync, _) = NewCurrenciesContext(
            [PermissionClaims.CurrenciesRead, PermissionClaims.SystemSettingsRead], Row(Currency("USD", enabled: true)));
        await using var __ = ctx;

        var cut = RenderCurrencies(ctx, "USD");

        Assert.Contains("Coverage", cut.Markup);
        Assert.Contains("Run history", cut.Markup);
        Assert.Single(cut.FindComponents<ExchangeRateSyncStatusPanel>());
        Assert.Contains("Synced", RowOf(cut, "USD").TextContent);
        var menu = MenuOf(cut, "USD");
        Assert.DoesNotContain("Stop sync", menu);
        sync.Verify(c => c.ListRunsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── The Sync column and the menu ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_base_row_and_an_unpublished_row_explain_themselves_and_offer_no_start()
    {
        var (ctx, _, _) = NewCurrenciesContext(
            Row(Currency("BASE", enabled: true, locked: true)), Row(Currency("XDR", supported: false)), Row(Currency("RUB", enabled: true, supported: false)));
        await using var __ = ctx;

        var cut = RenderCurrencies(ctx, "BASE");

        Assert.Contains("Provider’s base — always synced", RowOf(cut, "BASE").TextContent);
        Assert.Contains("Synced", RowOf(cut, "BASE").TextContent);
        Assert.Contains("Not published", RowOf(cut, "XDR").TextContent);
        Assert.Contains("No longer published — stop syncing", RowOf(cut, "RUB").TextContent);

        var baseMenu = MenuOf(cut, "BASE");
        Assert.DoesNotContain("sync", baseMenu.Replace("Copy ID", string.Empty), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Start sync", MenuOf(cut, "XDR"));
        Assert.Contains("Stop sync", MenuOf(cut, "RUB"));
    }

    [Fact]
    public async Task An_archived_row_reads_not_applicable_in_text_and_has_no_sync_actions()
    {
        var (ctx, _, _) = NewCurrenciesContext(Row(null, "CHF", archived: true));
        await using var __ = ctx;

        var cut = RenderCurrencies(ctx, "CHF");

        Assert.Empty(cut.FindAll("span[aria-label='Not applicable']"));
        Assert.Contains(RowOf(cut, "CHF").QuerySelectorAll(".sr-only"), sp => sp.TextContent == "Not applicable");
        var menu = MenuOf(cut, "CHF");
        Assert.DoesNotContain("Start sync", menu);
    }

    [Fact]
    public async Task The_sync_filter_is_sent_as_state_names()
    {
        var (ctx, _, currencies) = NewCurrenciesContext(Row(Currency("USD")));
        await using var __ = ctx;
        var cut = RenderCurrencies(ctx, "USD");

        await cut.InvokeAsync(() => cut.FindComponents<Odyssey.Client.Components.OdsMultiSelect>()
            .Single(m => m.Instance.Label == "Sync").Instance.ValuesChanged.InvokeAsync(["On"]));

        currencies.Verify(c => c.ListAsync(1, It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.Is<IReadOnlyCollection<string>?>(s => s != null && s.SequenceEqual(new[] { "On" })),
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

        cut.WaitForAssertion(() => Assert.Contains("The provider doesn't publish USD.", RowOf(cut, "USD").TextContent));
        Assert.Contains("Not synced", RowOf(cut, "USD").TextContent);
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
        Assert.Empty(RowOf(cut, "USD").QuerySelectorAll(".xrs-sync-err"));
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
        Assert.Equal("true", RowOf(cut, "USD").QuerySelector(".xrs-sync")!.GetAttribute("aria-busy"));
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
        RowOf(cut, "USD").QuerySelector("button[aria-label='Row actions']")!.Click();
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
    public async Task Each_row_names_its_code_in_the_avatar_and_writes_the_example_with_the_code_after()
    {
        var (ctx, _, _) = NewCurrenciesContext(Row(Currency("USD")));
        await using var __ = ctx;

        var cut = RenderCurrencies(ctx, "USD");

        var avatar = RowOf(cut, "USD").QuerySelector(".mud-avatar")!;
        Assert.Equal("USD", avatar.TextContent.Trim());
        // No Code column, so the avatar is the code's only place and must be named, not hidden.
        Assert.Equal("USD", avatar.GetAttribute("aria-label"));
        Assert.Null(avatar.GetAttribute("aria-hidden"));
        Assert.DoesNotContain(cut.FindAll("th"), th => th.TextContent.Trim() == "Code");
        Assert.Contains(OdsMoney.Format(1234.5m, "USD", 2), RowOf(cut, "USD").TextContent);
        Assert.EndsWith(" USD", OdsMoney.Format(1234.5m, "USD", 2));
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
            .Single(m => m.Instance.Label == "Sync").Instance.ValuesChanged.InvokeAsync(["NotPublished"]));
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
