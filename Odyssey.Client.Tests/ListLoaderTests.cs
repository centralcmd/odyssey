using System.Net;
using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <see cref="ListLoader"/>, the latest-request-wins guard every list page routes its fetch through
/// (issue #249). A slower earlier response that lands last must not overwrite the newer one, must not
/// clear the refetch state the newer one still owns, and must not toast.
/// </summary>
public class ListLoaderTests
{
    static ListLoaderTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task An_earlier_response_that_lands_last_is_superseded()
    {
        using var loader = new ListLoader();
        var older = new TaskCompletionSource<string>();
        var newer = new TaskCompletionSource<string>();

        var first = loader.RunAsync(_ => older.Task);
        var second = loader.RunAsync(_ => newer.Task);

        newer.SetResult("newer");
        older.SetResult("older");

        var latest = await second;
        Assert.False(latest.IsSuperseded);
        Assert.Equal("newer", latest.Value);
        Assert.True((await first).IsSuperseded);
    }

    [Fact]
    public async Task An_earlier_response_that_lands_first_is_still_superseded()
    {
        using var loader = new ListLoader();
        var older = new TaskCompletionSource<string>();
        var newer = new TaskCompletionSource<string>();

        var first = loader.RunAsync(_ => older.Task);
        var second = loader.RunAsync(_ => newer.Task);

        older.SetResult("older");
        Assert.True((await first).IsSuperseded);

        newer.SetResult("newer");
        Assert.Equal("newer", (await second).Value);
    }

    [Fact]
    public async Task A_single_request_is_current()
    {
        using var loader = new ListLoader();

        var only = await loader.RunAsync(_ => Task.FromResult(42));

        Assert.False(only.IsSuperseded);
        Assert.Equal(42, only.Value);
    }

    [Fact]
    public async Task Starting_a_request_cancels_the_token_of_the_one_in_flight()
    {
        using var loader = new ListLoader();
        CancellationToken olderToken = default, newerToken = default;
        var older = new TaskCompletionSource<int>();

        var first = loader.RunAsync(ct => { olderToken = ct; return older.Task; });
        Assert.False(olderToken.IsCancellationRequested);

        var second = loader.RunAsync(ct => { newerToken = ct; return Task.FromResult(2); });

        Assert.True(olderToken.IsCancellationRequested);
        Assert.False(newerToken.IsCancellationRequested);

        older.SetResult(1);
        Assert.True((await first).IsSuperseded);
        Assert.Equal(2, (await second).Value);
    }

    /// <summary>
    /// A transport that honours the token throws when the newer request cancels it; that is the
    /// expected shape of a superseded request, not an error the page should see.
    /// </summary>
    [Fact]
    public async Task A_superseded_request_that_throws_on_cancellation_is_reported_superseded()
    {
        using var loader = new ListLoader();

        var first = loader.RunAsync(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 1;
        });
        var second = loader.RunAsync(_ => Task.FromResult(2));

        Assert.True((await first).IsSuperseded);
        Assert.Equal(2, (await second).Value);
    }

    /// <summary>Only a cancellation the loader caused is swallowed; the current request's own failure surfaces.</summary>
    [Fact]
    public async Task A_current_request_that_throws_is_not_swallowed()
    {
        using var loader = new ListLoader();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            loader.RunAsync<int>(_ => throw new OperationCanceledException()));
    }

    [Fact]
    public async Task Dispose_cancels_the_request_in_flight_and_drops_its_response()
    {
        var loader = new ListLoader();
        CancellationToken token = default;
        var pending = new TaskCompletionSource<int>();

        var run = loader.RunAsync(ct => { token = ct; return pending.Task; });
        loader.Dispose();

        Assert.True(token.IsCancellationRequested);
        pending.SetResult(1);
        Assert.True((await run).IsSuperseded);
    }

    [Fact]
    public async Task After_dispose_nothing_is_fetched()
    {
        var loader = new ListLoader();
        loader.Dispose();
        var called = false;

        var run = await loader.RunAsync(_ => { called = true; return Task.FromResult(1); });

        Assert.True(run.IsSuperseded);
        Assert.False(called);
    }

    /// <summary>
    /// The card shape the loader exists to serve: flags raised by every request, cleared only by the
    /// newest. The older request landing first must leave the refetch bar up, because the newer one is
    /// still running; the older one landing after the newer must change nothing at all.
    /// </summary>
    [Fact]
    public async Task Refetch_state_is_cleared_only_by_the_newest_request()
    {
        var card = new FakeCard();
        var older = new TaskCompletionSource<string>();
        var newer = new TaskCompletionSource<string>();

        var first = card.LoadAsync(older.Task);
        var second = card.LoadAsync(newer.Task);
        Assert.True(card.Refetching);

        older.SetResult("older");
        await first;
        Assert.True(card.Refetching);
        Assert.Null(card.Rows);

        newer.SetResult("newer");
        await second;
        Assert.False(card.Refetching);
        Assert.Equal("newer", card.Rows);
    }

    private sealed class FakeCard
    {
        private readonly ListLoader loader = new();
        public bool Refetching { get; private set; }
        public string? Rows { get; private set; }

        public async Task LoadAsync(Task<string> fetch)
        {
            Refetching = true;
            var response = await loader.RunAsync(_ => fetch);
            if (response.IsSuperseded)
                return;

            Rows = response.Value;
            Refetching = false;
        }
    }

    // ── A real card ─────────────────────────────────────────────────────────────────────────────

    private static ExistingCurrency Currency(string code) =>
        new() { CurrencyCode = code, Name = code + " name", Symbol = "$", MinorUnits = 2 };

    private static ApiResult<PagedResult<ExistingCurrency>> Page(params ExistingCurrency[] rows) =>
        ApiResult<PagedResult<ExistingCurrency>>.Success(
            new PagedResult<ExistingCurrency> { Items = rows, TotalCount = rows.Length, Offset = 0, Limit = 25 },
            HttpStatusCode.OK);

    /// <summary>
    /// End to end on <c>CurrenciesCard</c>: two searches in flight, the older one answering last. The
    /// grid, the pager total and the live announcement must all describe the newer search, and the
    /// older one — cancelled when the newer started, which the transport reports as a failure — must
    /// not raise a "couldn't load" toast.
    /// </summary>
    [Fact]
    public async Task A_card_resolves_out_of_order_search_responses_to_the_latest()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(Mock.Of<IClipboardService>());
        ctx.Services.AddSingleton(Mock.Of<IPageStateService>());
        ctx.Services.AddSingleton(Mock.Of<IReferenceDataCache>());
        ctx.Services.AddSingleton<AuthenticationStateProvider>(new SignedIn([PermissionClaims.CurrenciesRead]));

        // Keyed on the search text so the test decides the order the two responses arrive in.
        var pending = new Dictionary<string, TaskCompletionSource<ApiResult<PagedResult<ExistingCurrency>>>>
        {
            ["eu"] = new(),
            ["usd"] = new(),
        };
        var tokens = new Dictionary<string, CancellationToken>();
        var currencies = new Mock<ICurrenciesApiClient>();
        currencies.Setup(c => c.ListAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((int _, int _, string? search, IReadOnlyCollection<string>? _, string? _, string? _, CancellationToken ct) =>
            {
                tokens[search!] = ct;
                return pending[search!].Task;
            });
        ctx.Services.AddSingleton(currencies.Object);

        var cut = ctx.Render<CurrenciesCard>();
        var search = cut.FindComponent<OdsSearchField>().Instance;

        await cut.InvokeAsync(() => search.ValueChanged.InvokeAsync("eu"));
        var older = cut.InvokeAsync(() => search.OnSearch.InvokeAsync());
        await cut.InvokeAsync(() => search.ValueChanged.InvokeAsync("usd"));
        var newer = cut.InvokeAsync(() => search.OnSearch.InvokeAsync());

        Assert.True(tokens["eu"].IsCancellationRequested);

        await cut.InvokeAsync(() => pending["usd"].SetResult(Page(Currency("USD"))));
        await newer;
        // What the transport returns for a request whose token was cancelled.
        await cut.InvokeAsync(() => pending["eu"].SetResult(
            ApiResult<PagedResult<ExistingCurrency>>.Failure(new TaskCanceledException())));
        await older;

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("USD name", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("EUR name", cut.Markup, StringComparison.Ordinal);
            Assert.Equal("Showing 1–1 of 1 currency.", cut.FindComponent<OdsLiveAnnouncer>().Instance.Message);
            Assert.Equal(1, cut.FindComponent<OdsPager>().Instance.TotalCount);
        });
        Assert.Empty(ctx.Services.GetRequiredService<ISnackbar>().ShownSnackbars);
    }

    [Fact]
    public async Task A_card_cancels_its_fetch_when_disposed()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(Mock.Of<IClipboardService>());
        ctx.Services.AddSingleton(Mock.Of<IPageStateService>());
        ctx.Services.AddSingleton(Mock.Of<IReferenceDataCache>());
        ctx.Services.AddSingleton<AuthenticationStateProvider>(new SignedIn([PermissionClaims.CurrenciesRead]));

        CancellationToken token = default;
        var pending = new TaskCompletionSource<ApiResult<PagedResult<ExistingCurrency>>>();
        var currencies = new Mock<ICurrenciesApiClient>();
        currencies.Setup(c => c.ListAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((int _, int _, string? _, IReadOnlyCollection<string>? _, string? _, string? _, CancellationToken ct) =>
            {
                token = ct;
                return pending.Task;
            });
        ctx.Services.AddSingleton(currencies.Object);

        var cut = ctx.Render<CurrenciesCard>();
        var search = cut.FindComponent<OdsSearchField>().Instance;
        _ = cut.InvokeAsync(() => search.OnSearch.InvokeAsync());

        await cut.InvokeAsync(() => cut.Instance.Dispose());

        Assert.True(token.IsCancellationRequested);
    }

    private sealed class SignedIn(IEnumerable<string> permissions) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                permissions.Select(p => new Claim(PermissionClaims.Type, p)), "test"))));
    }
}
