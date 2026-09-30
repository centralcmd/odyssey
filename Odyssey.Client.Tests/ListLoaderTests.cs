using Odyssey.Client.Services;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <see cref="ListLoader"/>, the latest-request-wins guard every list page routes its fetch through
/// (issue #249). A slower earlier response that lands last must not overwrite the newer one, must not
/// clear the refetch state the newer one still owns, and must not toast.
/// </summary>
public class ListLoaderTests
{
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

    /// <summary>
    /// A superseded request touches nothing whatever it throws — not only the cancellation the loader
    /// asked for, but a failure it met on the way (a JSON error on a half-read body, say). The newest
    /// request's own exceptions are not the loader's to swallow.
    /// </summary>
    [Fact]
    public async Task A_superseded_request_that_throws_anything_is_reported_superseded()
    {
        using var loader = new ListLoader();
        var older = new TaskCompletionSource<int>();

        var first = loader.RunAsync(_ => older.Task);
        var second = loader.RunAsync(_ => Task.FromResult(2));

        older.SetException(new InvalidOperationException("half-read body"));

        Assert.True((await first).IsSuperseded);
        Assert.Equal(2, (await second).Value);
    }

    [Fact]
    public async Task A_current_request_that_throws_something_else_is_not_swallowed()
    {
        using var loader = new ListLoader();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            loader.RunAsync<int>(_ => throw new InvalidOperationException("boom")));
    }
}
