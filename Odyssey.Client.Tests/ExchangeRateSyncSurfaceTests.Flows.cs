using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Odyssey.Client.Pages.Finance.ExchangeRateSync;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The error paths and multi-step flows of the exchange-rate sync surfaces: Run now's refusals, a poll's
/// refusals, the manual refresh, the row writes' refusals, the start-date edit, the purge, and the
/// fetched-rate delete on /exchange-rates.
/// </summary>
public partial class ExchangeRateSyncSurfaceTests
{
    // ── Status panel: Run now and the poll's refusals ────────────────────────────────────────────

    private static Mock<IExchangeRateSyncApiClient> RunNowReturning(ApiResult<ExchangeRateSyncRunStarted> outcome, params ExchangeRateSyncStatus[] reads)
    {
        var sync = new Mock<IExchangeRateSyncApiClient>();
        var queue = new Queue<ExchangeRateSyncStatus>(reads);
        sync.Setup(c => c.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ApiResult<ExchangeRateSyncStatus>.Success(queue.Count > 1 ? queue.Dequeue() : queue.Peek(), HttpStatusCode.OK));
        sync.Setup(c => c.RunNowAsync(It.IsAny<CancellationToken>())).ReturnsAsync(outcome);
        return sync;
    }

    private static ApiResult<T> Refused<T>(HttpStatusCode status, string title = "refused") =>
        ApiResult<T>.Failure(status, new ApiProblem { Title = title, Detail = title });

    private sealed record Announced(List<string> Polite, List<string> Assertive);

    private static IRenderedComponent<ExchangeRateSyncStatusPanel> RenderPanel(BunitContext ctx, Announced said) =>
        ctx.Render<ExchangeRateSyncStatusPanel>(p => p
            .Add(x => x.CanRun, true)
            .Add(x => x.OnAnnounce, (string m) => said.Polite.Add(m))
            .Add(x => x.OnAnnounceAssertive, (string m) => said.Assertive.Add(m)));

    [Fact]
    public async Task Run_now_accepted_announces_the_start_and_polls_the_new_run()
    {
        var sync = RunNowReturning(
            ApiResult<ExchangeRateSyncRunStarted>.Success(new ExchangeRateSyncRunStarted { RunId = Guid.NewGuid() }, HttpStatusCode.Accepted),
            Scheduled(), Running(Now.UtcDateTime));
        var (ctx, _) = NewContext(sync.Object);
        await using var __ = ctx;
        var said = new Announced([], []);

        var cut = RenderPanel(ctx, said);
        await cut.InvokeAsync(cut.Instance.RunNowAsync);

        Assert.Equal(["Run started"], said.Polite);
        Assert.True(cut.Instance.IsPolling);
    }

    [Fact]
    public async Task Run_now_refused_as_already_running_watches_that_run_instead()
    {
        var sync = RunNowReturning(Refused<ExchangeRateSyncRunStarted>(HttpStatusCode.Conflict), Scheduled(), Running(Now.UtcDateTime));
        var (ctx, _) = NewContext(sync.Object);
        await using var __ = ctx;
        var said = new Announced([], []);

        var cut = RenderPanel(ctx, said);
        await cut.InvokeAsync(cut.Instance.RunNowAsync);

        Assert.True(cut.Instance.IsPolling);
        Assert.Equal(ExchangeRateSyncState.Running, cut.Instance.Status?.State);
        Assert.Empty(said.Assertive);
    }

    [Fact]
    public async Task Run_now_refused_by_the_cooldown_rereads_status_and_shows_when_it_can_run()
    {
        var cooling = Scheduled() with { ManualRunAvailableAtUtc = Now.UtcDateTime.AddMinutes(3) };
        var sync = RunNowReturning(Refused<ExchangeRateSyncRunStarted>(HttpStatusCode.TooManyRequests), Scheduled(), cooling);
        var (ctx, _) = NewContext(sync.Object);
        await using var __ = ctx;

        var cut = RenderPanel(ctx, new Announced([], []));
        await cut.InvokeAsync(cut.Instance.RunNowAsync);

        Assert.False(cut.Instance.IsPolling);
        Assert.StartsWith("You can start another run at", cut.Instance.RunNowReason);
        sync.Verify(c => c.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, ExchangeRateSyncStatusPanel.PermissionMessage)]
    [InlineData(HttpStatusCode.InternalServerError, "Couldn’t start a run.")]
    public async Task Run_now_failing_is_announced_assertively(HttpStatusCode status, string expected)
    {
        var sync = RunNowReturning(Refused<ExchangeRateSyncRunStarted>(status), Scheduled());
        var (ctx, _) = NewContext(sync.Object);
        await using var __ = ctx;
        var said = new Announced([], []);

        var cut = RenderPanel(ctx, said);
        await cut.InvokeAsync(cut.Instance.RunNowAsync);

        Assert.Equal([expected], said.Assertive);
        Assert.Empty(said.Polite);
        Assert.False(cut.Instance.IsPolling);
    }

    [Fact]
    public async Task A_poll_refused_for_permission_stops_and_says_to_reload()
    {
        var sync = new Mock<IExchangeRateSyncApiClient>();
        var first = true;
        sync.Setup(c => c.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() =>
        {
            if (!first)
                return Refused<ExchangeRateSyncStatus>(HttpStatusCode.Forbidden);
            first = false;
            return ApiResult<ExchangeRateSyncStatus>.Success(Running(Now.UtcDateTime), HttpStatusCode.OK);
        });
        var (ctx, time) = NewContext(sync.Object);
        await using var __ = ctx;
        var said = new Announced([], []);

        var cut = RenderPanel(ctx, said);
        time.Advance(ExchangeRateSyncStatusPanel.PollInterval);

        cut.WaitForAssertion(() => Assert.False(cut.Instance.IsPolling));
        Assert.Equal(["Your permissions changed — reload the page"], said.Assertive);
        Assert.Null(cut.Instance.Stale);
    }

    [Fact]
    public async Task A_poll_that_is_rate_limited_goes_stale_at_once()
    {
        var sync = new Mock<IExchangeRateSyncApiClient>();
        var first = true;
        sync.Setup(c => c.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() =>
        {
            if (!first)
                return Refused<ExchangeRateSyncStatus>(HttpStatusCode.TooManyRequests);
            first = false;
            return ApiResult<ExchangeRateSyncStatus>.Success(Running(Now.UtcDateTime), HttpStatusCode.OK);
        });
        var (ctx, time) = NewContext(sync.Object);
        await using var __ = ctx;
        var said = new Announced([], []);

        var cut = RenderPanel(ctx, said);
        time.Advance(ExchangeRateSyncStatusPanel.PollInterval);

        cut.WaitForAssertion(() => Assert.False(cut.Instance.IsPolling));
        Assert.Equal(ExchangeRateSyncStatusPanel.StaleReason.Errors, cut.Instance.Stale);
        Assert.Equal(["Couldn’t refresh status"], said.Polite);
    }

    [Fact]
    public async Task Refresh_status_announces_what_it_read_and_keeps_focus_while_busy()
    {
        var (ctx, _) = NewContext(ExchangeRateSyncFakes.Client(Scheduled()).Object);
        await using var __ = ctx;
        var said = new Announced([], []);

        var cut = RenderPanel(ctx, said);
        var refresh = cut.FindAll("button").Single(b => b.TextContent.Contains("Refresh status"));
        Assert.False(refresh.HasAttribute("disabled"));
        await cut.InvokeAsync(cut.Instance.RefreshAsync);

        Assert.Equal([$"Status refreshed: {OdsTypeRegistries.ExchangeRateSyncStateOf(ExchangeRateSyncState.Scheduled).Label}"], said.Polite);
    }

    [Fact]
    public async Task A_failed_refresh_goes_stale_and_says_so_once()
    {
        var sync = new Mock<IExchangeRateSyncApiClient>();
        var first = true;
        sync.Setup(c => c.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() =>
        {
            if (!first)
                return Refused<ExchangeRateSyncStatus>(HttpStatusCode.TooManyRequests);
            first = false;
            return ApiResult<ExchangeRateSyncStatus>.Success(Scheduled(), HttpStatusCode.OK);
        });
        var (ctx, _) = NewContext(sync.Object);
        await using var __ = ctx;
        var said = new Announced([], []);

        var cut = RenderPanel(ctx, said);
        await cut.InvokeAsync(cut.Instance.RefreshAsync);

        Assert.Equal(["Couldn’t refresh status"], said.Polite);
        Assert.Equal(ExchangeRateSyncStatusPanel.StaleReason.Errors, cut.Instance.Stale);
    }

    // ── Sync page: row refusals, busy guard, start date, purge ───────────────────────────────────

    private static string AssertiveText(IRenderedComponent<ExchangeRateSyncPage> cut) =>
        cut.Find("[role=alert][aria-live=assertive]").TextContent;

    [Fact]
    public async Task A_toggle_the_provider_could_not_confirm_names_the_currency_on_its_row()
    {
        var (ctx, sync) = NewPageContext(Currency("USD"));
        await using var __ = ctx;
        sync.Setup(c => c.UpdateCurrencyAsync("USD", It.IsAny<ExchangeRateSyncCurrencyUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Refused<ExistingExchangeRateSyncCurrency>(HttpStatusCode.ServiceUnavailable));

        var cut = ctx.Render<ExchangeRateSyncPage>();
        cut.WaitForAssertion(() => SyncSwitch(cut, "USD"));
        SyncSwitch(cut, "USD").Change(true);

        cut.WaitForAssertion(() => Assert.Contains("Couldn’t confirm Provider publishes USD. Try again shortly.", cut.Find("#xrs-err-USD").TextContent));
        cut.WaitForAssertion(() => Assert.Contains("Couldn’t switch on USD.", AssertiveText(cut)));
    }

    [Fact]
    public async Task A_toggle_refused_for_permission_says_so_and_leaves_no_row_error()
    {
        var (ctx, sync) = NewPageContext(Currency("USD"));
        await using var __ = ctx;
        sync.Setup(c => c.UpdateCurrencyAsync("USD", It.IsAny<ExchangeRateSyncCurrencyUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Refused<ExistingExchangeRateSyncCurrency>(HttpStatusCode.Forbidden));

        var cut = ctx.Render<ExchangeRateSyncPage>();
        cut.WaitForAssertion(() => SyncSwitch(cut, "USD"));
        SyncSwitch(cut, "USD").Change(true);

        cut.WaitForAssertion(() => Assert.Equal(ExchangeRateSyncStatusPanel.PermissionMessage, AssertiveText(cut)));
        Assert.Empty(cut.FindAll("#xrs-err-USD"));
    }

    [Fact]
    public async Task A_toggle_on_a_currency_that_is_gone_relists_the_page()
    {
        var (ctx, sync) = NewPageContext(Currency("USD"));
        await using var __ = ctx;
        sync.Setup(c => c.UpdateCurrencyAsync("USD", It.IsAny<ExchangeRateSyncCurrencyUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Refused<ExistingExchangeRateSyncCurrency>(HttpStatusCode.NotFound));

        var cut = ctx.Render<ExchangeRateSyncPage>();
        cut.WaitForAssertion(() => SyncSwitch(cut, "USD"));
        SyncSwitch(cut, "USD").Change(true);

        cut.WaitForAssertion(() => sync.Verify(c => c.ListCurrenciesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<bool?>(),
            It.IsAny<bool?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(2)));
    }

    [Fact]
    public async Task A_second_toggle_while_the_first_is_saving_sends_nothing()
    {
        var (ctx, sync) = NewPageContext(Currency("USD"));
        await using var __ = ctx;
        var pending = new TaskCompletionSource<ApiResult<ExistingExchangeRateSyncCurrency>>();
        sync.Setup(c => c.UpdateCurrencyAsync("USD", It.IsAny<ExchangeRateSyncCurrencyUpdate>(), It.IsAny<CancellationToken>()))
            .Returns(pending.Task);

        var cut = ctx.Render<ExchangeRateSyncPage>();
        cut.WaitForAssertion(() => SyncSwitch(cut, "USD"));
        SyncSwitch(cut, "USD").Change(true);
        cut.WaitForAssertion(() => Assert.Equal("true", SyncSwitch(cut, "USD").GetAttribute("aria-busy")));
        SyncSwitch(cut, "USD").Change(false);

        sync.Verify(c => c.UpdateCurrencyAsync("USD", It.IsAny<ExchangeRateSyncCurrencyUpdate>(), It.IsAny<CancellationToken>()), Times.Once);
        await cut.InvokeAsync(() => pending.SetResult(ApiResult<ExistingExchangeRateSyncCurrency>.Success(Currency("USD", enabled: true), HttpStatusCode.OK)));
        cut.WaitForAssertion(() => Assert.Null(SyncSwitch(cut, "USD").GetAttribute("aria-busy")));
    }

    [Fact]
    public async Task A_saved_toggle_rereads_status_so_the_chip_and_count_follow()
    {
        var (ctx, sync) = NewPageContext(Currency("USD"));
        await using var __ = ctx;
        sync.Setup(c => c.UpdateCurrencyAsync("USD", It.IsAny<ExchangeRateSyncCurrencyUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingExchangeRateSyncCurrency>.Success(Currency("USD", enabled: true), HttpStatusCode.OK));

        var cut = ctx.Render<ExchangeRateSyncPage>();
        cut.WaitForAssertion(() => SyncSwitch(cut, "USD"));
        sync.Verify(c => c.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Once);
        SyncSwitch(cut, "USD").Change(true);

        cut.WaitForAssertion(() => sync.Verify(c => c.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Exactly(2)));
    }

    /// <summary>Opens a row's ⋯ menu and clicks the item labelled <paramref name="label"/>.</summary>
    private void ClickRowAction(IRenderedComponent<ExchangeRateSyncPage> cut, string code, string label)
    {
        var row = cut.FindAll("tr").First(tr => tr.TextContent.Contains(code + " name"));
        row.QuerySelector("button[aria-label='Row actions']")!.Click();
        var popover = _popover!;
        popover.WaitForAssertion(() => Assert.Contains(label, popover.Markup));
        popover.FindAll(".mud-menu-item, [role=menuitem]").First(i => i.TextContent.Contains(label)).Click();
    }

    [Fact]
    public async Task Use_default_clears_the_override_and_announces_the_save()
    {
        var own = Currency("USD", enabled: true) with { HistoryStartDate = new DateOnly(2020, 1, 1) };
        var (ctx, sync) = NewPageContext(own);
        await using var __ = ctx;
        sync.Setup(c => c.UpdateCurrencyAsync("USD", It.IsAny<ExchangeRateSyncCurrencyUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingExchangeRateSyncCurrency>.Success(own with { HistoryStartDate = null }, HttpStatusCode.OK));

        var cut = ctx.Render<ExchangeRateSyncPage>();
        cut.WaitForAssertion(() => SyncSwitch(cut, "USD"));
        ClickRowAction(cut, "USD", "Set start date…");
        cut.WaitForAssertion(() => Assert.Contains("Use default", cut.Markup));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Use default")).Click();

        cut.WaitForAssertion(() => sync.Verify(c => c.UpdateCurrencyAsync("USD",
            It.Is<ExchangeRateSyncCurrencyUpdate>(u => u.Enabled == true && u.HistoryStartDate == null), It.IsAny<CancellationToken>()), Times.Once));
        cut.WaitForAssertion(() => Assert.Contains("Start date for USD saved", cut.Find("[role=status][aria-live=polite]").TextContent));
    }

    [Fact]
    public async Task A_refused_start_date_keeps_the_edit_row_open_with_the_servers_message()
    {
        var (ctx, sync) = NewPageContext(Currency("USD", enabled: true));
        await using var __ = ctx;
        sync.Setup(c => c.UpdateCurrencyAsync("USD", It.IsAny<ExchangeRateSyncCurrencyUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExistingExchangeRateSyncCurrency>.Failure(HttpStatusCode.UnprocessableEntity, new ApiProblem
            {
                Title = "Unprocessable",
                Errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { ["historyStartDate"] = ["Too early for this currency."] },
            }));

        var cut = ctx.Render<ExchangeRateSyncPage>();
        cut.WaitForAssertion(() => SyncSwitch(cut, "USD"));
        ClickRowAction(cut, "USD", "Set start date…");
        cut.WaitForAssertion(() => Assert.Contains("Use default", cut.Markup));
        // The label is the button's text after its icon ligature, which is aria-hidden.
        cut.FindAll("button").Single(b => b.QuerySelector(".material-icons")?.TextContent == "check" && b.TextContent.Contains("Save")).Click();

        cut.WaitForAssertion(() => Assert.Contains("Too early for this currency.", cut.Markup));
        Assert.Contains("Use default", cut.Markup);
    }

    [Fact]
    public async Task Delete_fetched_rates_is_offered_only_for_an_off_currency_with_fetched_rows()
    {
        var (ctx, _) = NewPageContext(Currency("USD") with { FetchedRowCount = 12 }, Currency("EUR", enabled: true) with { FetchedRowCount = 9 }, Currency("SEK"));
        await using var __ = ctx;

        var cut = ctx.Render<ExchangeRateSyncPage>();
        cut.WaitForAssertion(() => SyncSwitch(cut, "USD"));

        AngleSharp.Dom.IElement Trigger(string code) =>
            cut.FindAll("tr").First(tr => tr.TextContent.Contains(code + " name")).QuerySelector("button[aria-label='Row actions']")!;

        string ItemsFor(string code)
        {
            Trigger(code).Click();
            var popover = _popover!;
            popover.WaitForAssertion(() => Assert.Contains("Set start date…", popover.Markup));
            var text = popover.Markup;
            Trigger(code).Click();
            popover.WaitForAssertion(() => Assert.DoesNotContain("Set start date…", popover.Markup));
            return text;
        }

        Assert.Contains("Delete fetched rates…", ItemsFor("USD"));
        Assert.DoesNotContain("Delete fetched rates…", ItemsFor("EUR"));
        Assert.DoesNotContain("Delete fetched rates…", ItemsFor("SEK"));
    }

    [Fact]
    public async Task A_confirmed_purge_zeroes_the_row_announces_and_rereads_runs_and_status()
    {
        var (ctx, sync) = NewPageContext(Currency("USD") with { FetchedRowCount = 12 });
        await using var __ = ctx;
        sync.Setup(c => c.PurgeCurrencyAsync("USD", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<ExchangeRatePurgeResult>.Success(new ExchangeRatePurgeResult { RowsDeleted = 12 }, HttpStatusCode.OK));

        var cut = ctx.Render<ExchangeRateSyncPage>();
        cut.WaitForAssertion(() => SyncSwitch(cut, "USD"));
        ClickRowAction(cut, "USD", "Delete fetched rates…");
        Dialogs.WaitForAssertion(() => Assert.Contains("Delete fetched rates for USD?", Dialogs.Markup));
        Assert.Contains("This removes 12 rates fetched from Provider.", Dialogs.Markup);
        Dialogs.FindAll("button").Single(b => b.TextContent.Contains("Delete fetched rates")).Click();

        cut.WaitForAssertion(() => Assert.Contains("Deleted 12 fetched rates for USD", cut.Find("[role=status][aria-live=polite]").TextContent));
        cut.WaitForAssertion(() =>
        {
            sync.Verify(c => c.ListRunsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
            sync.Verify(c => c.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        });
    }

    [Fact]
    public async Task A_refused_purge_keeps_the_dialog_open_with_the_message_and_a_reopen_starts_clean()
    {
        var (ctx, sync) = NewPageContext(Currency("USD") with { FetchedRowCount = 12 });
        await using var __ = ctx;
        sync.Setup(c => c.PurgeCurrencyAsync("USD", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Refused<ExchangeRatePurgeResult>(HttpStatusCode.Conflict, "A run is in progress."));

        var cut = ctx.Render<ExchangeRateSyncPage>();
        cut.WaitForAssertion(() => SyncSwitch(cut, "USD"));
        ClickRowAction(cut, "USD", "Delete fetched rates…");
        Dialogs.WaitForAssertion(() => Assert.Contains("Delete fetched rates for USD?", Dialogs.Markup));
        Dialogs.FindAll("button").Single(b => b.TextContent.Contains("Delete fetched rates")).Click();

        Dialogs.WaitForAssertion(() => Assert.Contains("A run is in progress.", Dialogs.Find("[role=alert]").TextContent));
        Dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel").Click();
        ClickRowAction(cut, "USD", "Delete fetched rates…");
        Dialogs.WaitForAssertion(() => Assert.Contains("Delete fetched rates for USD?", Dialogs.Markup));
        Assert.DoesNotContain("A run is in progress.", Dialogs.Markup);
    }

    // ── /exchange-rates: the rest of the claim matrix and the confirmed delete ───────────────────

    [Theory]
    [InlineData(new[] { PermissionClaims.ExchangeRatesRead, PermissionClaims.SystemSettingsUpdate }, false)]
    [InlineData(new[] { PermissionClaims.ExchangeRatesRead, PermissionClaims.ExchangeRatesDelete }, false)]
    [InlineData(new[] { PermissionClaims.ExchangeRatesRead, PermissionClaims.ExchangeRatesDelete, PermissionClaims.SystemSettingsUpdate }, true)]
    public async Task A_fetched_rates_delete_needs_both_claims(string[] claims, bool offered)
    {
        var (ctx, _) = NewContext(ExchangeRateSyncFakes.Client().Object);
        await using var __ = ctx;
        var fetched = Rate(ExchangeRateSource.Fetched);

        var cut = RenderRatesCard(ctx, claims, fetched);

        Assert.Equal(offered, cut.Instance.BuildActions(fetched, NoopActions()).Any(i => i.Label == "Delete…"));
    }

    [Fact]
    public async Task A_manual_rate_without_update_has_no_edit()
    {
        var (ctx, _) = NewContext(ExchangeRateSyncFakes.Client().Object);
        await using var __ = ctx;
        var manual = Rate(ExchangeRateSource.Manual);

        var cut = RenderRatesCard(ctx, [PermissionClaims.ExchangeRatesRead, PermissionClaims.ExchangeRatesDelete], manual);

        var labels = cut.Instance.BuildActions(manual, NoopActions()).Select(i => i.Label).ToList();
        Assert.DoesNotContain("Edit", labels);
        Assert.Contains("Delete", labels);
    }

    [Fact]
    public async Task A_confirmed_fetched_delete_announces_and_moves_focus_to_the_next_rows_menu()
    {
        var (ctx, _) = NewContext(ExchangeRateSyncFakes.Client().Object);
        await using var __ = ctx;
        var first = Rate(ExchangeRateSource.Fetched);
        var second = Rate(ExchangeRateSource.Manual) with { AsOf = first.AsOf.AddDays(-1) };
        var cut = RenderRatesCard(ctx,
            [PermissionClaims.ExchangeRatesRead, PermissionClaims.ExchangeRatesDelete, PermissionClaims.SystemSettingsUpdate],
            first, second);
        var mock = Mock.Get(ctx.Services.GetRequiredService<IExchangeRatesApiClient>());
        mock.Setup(c => c.DeleteAsync(first.ExchangeRateId, It.IsAny<CancellationToken>())).ReturnsAsync(ApiResult.Success(HttpStatusCode.NoContent));
        mock.Setup(c => c.ListAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<PagedResult<ExistingExchangeRate>>.Success(new PagedResult<ExistingExchangeRate> { Items = [second], TotalCount = 1 }, HttpStatusCode.OK));

        var deleteItem = await cut.InvokeAsync(() => cut.Instance.BuildActions(first, NoopActions("first-menu")).Single(i => i.Label == "Delete…"));
        await cut.InvokeAsync(() => deleteItem.OnClick.InvokeAsync());
        Dialogs.WaitForAssertion(() => Assert.Contains("Delete this fetched rate?", Dialogs.Markup));
        // The same local timestamp the row's As of column shows.
        Assert.Contains($"USD/BASE on {first.AsOf.ToLocalTime().ToString("MMM dd, yyyy hh:mm tt")} will not be fetched again", Dialogs.Markup);
        Dialogs.FindAll("button").Single(b => b.TextContent.Contains("Delete rate")).Click();

        // The remaining row's real trigger id, as the table rendered it.
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("tbody tr [id$='-menu']")));
        var nextTrigger = cut.Find("tbody tr [id$='-menu']").Id;
        cut.WaitForAssertion(() => Assert.Contains(ctx.JSInterop.Invocations,
            i => i.Identifier == "odsFocusFirstIn" && Equals(i.Arguments[0], nextTrigger)));
        cut.WaitForAssertion(() => Assert.Equal("Rate deleted", cut.Find("[role=status]").TextContent));
        // The dialog stays mounted, closed — its own focus restore runs on close.
        Dialogs.WaitForAssertion(() => Assert.DoesNotContain("Delete this fetched rate?", Dialogs.Markup));
    }

    [Fact]
    public async Task A_refused_fetched_delete_shows_the_servers_message_in_the_dialog()
    {
        var (ctx, _) = NewContext(ExchangeRateSyncFakes.Client().Object);
        await using var __ = ctx;
        var fetched = Rate(ExchangeRateSource.Fetched);
        var cut = RenderRatesCard(ctx,
            [PermissionClaims.ExchangeRatesRead, PermissionClaims.ExchangeRatesDelete, PermissionClaims.SystemSettingsUpdate], fetched);
        Mock.Get(ctx.Services.GetRequiredService<IExchangeRatesApiClient>())
            .Setup(c => c.DeleteAsync(fetched.ExchangeRateId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Failure(HttpStatusCode.Forbidden, new ApiProblem { Detail = "Deleting a fetched rate needs system-settings.update." }));

        var deleteItem = cut.Instance.BuildActions(fetched, NoopActions()).Single(i => i.Label == "Delete…");
        await cut.InvokeAsync(() => deleteItem.OnClick.InvokeAsync());
        Dialogs.WaitForAssertion(() => Assert.Contains("Delete this fetched rate?", Dialogs.Markup));
        Dialogs.FindAll("button").Single(b => b.TextContent.Contains("Delete rate")).Click();

        Dialogs.WaitForAssertion(() => Assert.Contains("Deleting a fetched rate needs system-settings.update.", Dialogs.Markup));
        Assert.Contains("Delete this fetched rate?", Dialogs.Markup);
    }

    // ── Round-1 review fixes ─────────────────────────────────────────────────────────────────────

    // Bounded: without the guard the superseded query never settles, and a hang is no failure message.
    [Fact(Timeout = 20000)]
    public async Task A_run_ending_during_the_users_own_query_never_supersedes_it()
    {
        var (ctx, sync) = NewPageContext(Currency("USD"));
        await using var __ = ctx;
        var reads = new Queue<ExchangeRateSyncStatus>([Running(Now.UtcDateTime), Scheduled()]);
        sync.Setup(c => c.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ApiResult<ExchangeRateSyncStatus>.Success(reads.Count > 1 ? reads.Dequeue() : reads.Peek(), HttpStatusCode.OK));
        var userQuery = new TaskCompletionSource<ApiResult<PagedResult<ExistingExchangeRateSyncCurrency>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        sync.Setup(c => c.ListCurrenciesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<bool?>(),
                null, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(userQuery.Task);

        var cut = ctx.Render<ExchangeRateSyncPage>();
        cut.WaitForAssertion(() => SyncSwitch(cut, "USD"));
        cut.FindAll("[role=radio]").Single(b => b.TextContent.Trim() == "All").Click();
        _time!.Advance(ExchangeRateSyncStatusPanel.PollInterval);
        cut.WaitForAssertion(() => sync.Verify(c => c.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Exactly(2)));

        // Initial load + the user's query, and no merge.
        sync.Verify(c => c.ListCurrenciesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<bool?>(),
            It.IsAny<bool?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        await cut.InvokeAsync(() => userQuery.SetResult(ApiResult<PagedResult<ExistingExchangeRateSyncCurrency>>.Success(
            new PagedResult<ExistingExchangeRateSyncCurrency> { Items = [Currency("XDR", supported: false)], TotalCount = 1 }, HttpStatusCode.OK)));
        cut.WaitForAssertion(() => SyncSwitch(cut, "XDR"));
        Assert.DoesNotContain(cut.FindAll("span.mud-sr-only"), sp => sp.TextContent == "Sync USD");
    }

    [Fact]
    public async Task A_cell_with_nothing_to_say_names_it_in_text_not_an_aria_label()
    {
        var (ctx, _) = NewPageContext(Currency("BASE", enabled: true, locked: true));
        await using var __ = ctx;

        var cut = ctx.Render<ExchangeRateSyncPage>();
        cut.WaitForAssertion(() => SyncSwitch(cut, "BASE"));

        Assert.Empty(cut.FindAll("span[aria-label='Not applicable'], span[aria-label='None']"));
        Assert.Contains(cut.FindAll(".sr-only"), sp => sp.TextContent == "Not applicable");
    }

    [Fact]
    public async Task A_busy_button_that_also_holds_focus_stays_focusable_and_ignores_clicks()
    {
        var (ctx, _) = NewContext(ExchangeRateSyncFakes.Client().Object);
        await using var __ = ctx;
        var clicks = 0;

        var held = ctx.Render<OdsButton>(p => p.Add(b => b.Loading, true).Add(b => b.AriaDisabled, true)
            .Add(b => b.OnClick, () => clicks++).AddChildContent("Save"));
        var plain = ctx.Render<OdsButton>(p => p.Add(b => b.Loading, true).AddChildContent("Save"));

        Assert.False(held.Find("button").HasAttribute("disabled"));
        Assert.Equal("true", held.Find("button").GetAttribute("aria-disabled"));
        held.Find("button").Click();
        Assert.Equal(0, clicks);
        Assert.True(plain.Find("button").HasAttribute("disabled"));
    }

    [Fact]
    public async Task A_refresh_refused_for_permission_says_so_once()
    {
        var sync = new Mock<IExchangeRateSyncApiClient>();
        var first = true;
        sync.Setup(c => c.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() =>
        {
            if (!first)
                return Refused<ExchangeRateSyncStatus>(HttpStatusCode.Forbidden);
            first = false;
            return ApiResult<ExchangeRateSyncStatus>.Success(Scheduled(), HttpStatusCode.OK);
        });
        var (ctx, _) = NewContext(sync.Object);
        await using var __ = ctx;
        var said = new Announced([], []);

        var cut = RenderPanel(ctx, said);
        await cut.InvokeAsync(cut.Instance.RefreshAsync);

        Assert.Equal(["Your permissions changed — reload the page"], said.Assertive);
        Assert.Empty(said.Polite);
    }
}
