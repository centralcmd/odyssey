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
/// refusals, the manual refresh, and the fetched-rate delete on /exchange-rates. The currency rows'
/// flows are in ExchangeRateSyncSurfaceTests.Currencies.cs.
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
    public async Task Refresh_status_announces_what_it_read()
    {
        var sync = ExchangeRateSyncFakes.Client(Scheduled());
        var (ctx, _) = NewContext(sync.Object);
        await using var __ = ctx;
        var said = new Announced([], []);

        var cut = RenderPanel(ctx, said);
        await cut.InvokeAsync(cut.Instance.RefreshAsync);

        // The explicit refresh asks the server to re-fetch the provider's list; a plain read does not.
        sync.Verify(c => c.RefreshStatusAsync(It.IsAny<CancellationToken>()), Times.Once);
        sync.Verify(c => c.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Once);

        Assert.Equal([$"Status refreshed: {OdsTypeRegistries.ExchangeRateSyncStateOf(ExchangeRateSyncState.Scheduled).Label}"], said.Polite);
    }

    [Fact]
    public async Task A_failed_refresh_goes_stale_and_says_so_once()
    {
        var sync = ExchangeRateSyncFakes.Client(Scheduled());
        sync.Setup(c => c.RefreshStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Refused<ExchangeRateSyncStatus>(HttpStatusCode.TooManyRequests));
        var (ctx, _) = NewContext(sync.Object);
        await using var __ = ctx;
        var said = new Announced([], []);

        var cut = RenderPanel(ctx, said);
        await cut.InvokeAsync(cut.Instance.RefreshAsync);

        Assert.Equal(["Couldn’t refresh status"], said.Polite);
        Assert.Equal(ExchangeRateSyncStatusPanel.StaleReason.Errors, cut.Instance.Stale);
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
        var sync = ExchangeRateSyncFakes.Client(Scheduled());
        sync.Setup(c => c.RefreshStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Refused<ExchangeRateSyncStatus>(HttpStatusCode.Forbidden));
        var (ctx, _) = NewContext(sync.Object);
        await using var __ = ctx;
        var said = new Announced([], []);

        var cut = RenderPanel(ctx, said);
        await cut.InvokeAsync(cut.Instance.RefreshAsync);

        Assert.Equal(["Your permissions changed — reload the page"], said.Assertive);
        Assert.Empty(said.Polite);
    }
}
