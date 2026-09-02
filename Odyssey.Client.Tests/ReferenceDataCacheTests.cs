using System.Net;
using Moq;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Covers <see cref="ReferenceDataCache"/> — the per-session cache for currencies, transaction tags
/// and contacts (issue #372), which replaced twelve components each re-fetching the currency list on
/// every dialog open.
/// </summary>
/// <remarks>
/// The three behaviours worth pinning are the ones a naive memoization gets wrong: a failed load must
/// not be cached (or a transient 500 while a dialog opens would empty every picker for the rest of
/// the session), concurrent readers must share one in-flight request rather than racing two, and an
/// <c>Invalidate</c> after a write must actually force a re-fetch.
/// </remarks>
public class ReferenceDataCacheTests
{
    private static ExistingCurrency Currency(string code, bool archived = false, string? name = null) => new()
    {
        CurrencyCode = code,
        Name = name ?? code,
        Symbol = code,
        MinorUnits = 2,
        Archived = archived ? DateTime.UtcNow : null,
    };

    private static ExistingTransactionTag Tag(string name) => new()
    {
        TransactionTagId = Guid.NewGuid(),
        Name = name,
        Archived = null,
    };

    private static ExistingContact Contact(string name) => new()
    {
        ContactId = Guid.NewGuid(),
        ResolvedDisplayName = name,
        NormalizedName = name.ToUpperInvariant(),
        ExternalUid = Guid.NewGuid().ToString(),
    };

    private static ApiResult<List<T>> Ok<T>(params T[] items) =>
        ApiResult<List<T>>.Success([.. items], HttpStatusCode.OK);

    private static ApiResult<List<T>> Failed<T>(string detail = "boom") =>
        ApiResult<List<T>>.Failure(HttpStatusCode.InternalServerError, new ApiProblem { Detail = detail });

    private sealed class Harness
    {
        public Mock<ICurrenciesApiClient> Currencies { get; } = new(MockBehavior.Loose);
        public Mock<ITransactionTagsApiClient> Tags { get; } = new(MockBehavior.Loose);
        public Mock<IContactsApiClient> Contacts { get; } = new(MockBehavior.Loose);
        public RecordingSnackbar Snackbar { get; } = new();

        public IReferenceDataCache Cache => new ReferenceDataCache(
            Currencies.Object, Tags.Object, Contacts.Object, Snackbar);
    }

    private static Harness NewHarness()
    {
        var harness = new Harness();
        harness.Currencies
            .Setup(c => c.ListAllAsync(It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok<ExistingCurrency>());
        harness.Tags
            .Setup(t => t.ListAllAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok<ExistingTransactionTag>());
        harness.Contacts
            .Setup(c => c.ListAllAsync(It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok<ExistingContact>());
        return harness;
    }

    // ── Caching ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Currencies_are_fetched_once_however_many_readers_ask()
    {
        var harness = NewHarness();
        harness.Currencies
            .Setup(c => c.ListAllAsync(It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Currency("USD"), Currency("NOK")));
        var cache = harness.Cache;

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(2, (await cache.CurrenciesAsync()).Count);
            await cache.ActiveCurrenciesAsync();
            await cache.CurrencyOptionsAsync();
        }

        harness.Currencies.Verify(
            c => c.ListAllAsync(It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Tags_and_contacts_are_each_fetched_once()
    {
        var harness = NewHarness();
        harness.Tags
            .Setup(t => t.ListAllAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Tag("Groceries")));
        harness.Contacts
            .Setup(c => c.ListAllAsync(It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Contact("Acme")));
        var cache = harness.Cache;

        await cache.TransactionTagsAsync();
        await cache.TransactionTagsAsync();
        await cache.ContactsAsync();
        await cache.ContactsAsync();

        harness.Tags.Verify(t => t.ListAllAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        harness.Contacts.Verify(
            c => c.ListAllAsync(It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Two dialogs opening back to back must cost one request. Memoizing the <em>task</em> rather
    /// than its result is what buys this; memoizing only after the await would issue both.
    /// </summary>
    [Fact]
    public async Task Concurrent_readers_share_one_in_flight_request()
    {
        var harness = NewHarness();
        var gate = new TaskCompletionSource<ApiResult<List<ExistingCurrency>>>();
        harness.Currencies
            .Setup(c => c.ListAllAsync(It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()))
            .Returns(gate.Task);
        var cache = harness.Cache;

        var first = cache.CurrenciesAsync();
        var second = cache.CurrenciesAsync();
        gate.SetResult(Ok(Currency("USD")));

        Assert.Single(await first);
        Assert.Single(await second);
        harness.Currencies.Verify(
            c => c.ListAllAsync(It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Failure handling ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_failed_load_toasts_returns_empty_and_is_not_cached()
    {
        var harness = NewHarness();
        harness.Currencies
            .SetupSequence(c => c.ListAllAsync(It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Failed<ExistingCurrency>("gateway timeout"))
            .ReturnsAsync(Ok(Currency("USD")));
        var cache = harness.Cache;

        Assert.Empty(await cache.CurrenciesAsync());
        Assert.Equal(("Unable to load currencies: gateway timeout", Severity.Error), Assert.Single(harness.Snackbar.Toasts));

        // The next reader retries rather than being served the cached failure.
        Assert.Single(await cache.CurrenciesAsync());
        harness.Currencies.Verify(
            c => c.ListAllAsync(It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    // ── Invalidation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Invalidate_forces_the_next_reader_to_refetch()
    {
        var harness = NewHarness();
        harness.Currencies
            .SetupSequence(c => c.ListAllAsync(It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Currency("USD")))
            .ReturnsAsync(Ok(Currency("USD"), Currency("NOK")));
        var cache = harness.Cache;

        Assert.Single(await cache.CurrenciesAsync());
        cache.InvalidateCurrencies();

        Assert.Equal(2, (await cache.CurrenciesAsync()).Count);
    }

    [Fact]
    public async Task Invalidating_one_kind_leaves_the_others_cached()
    {
        var harness = NewHarness();
        var cache = harness.Cache;

        await cache.CurrenciesAsync();
        await cache.TransactionTagsAsync();
        await cache.ContactsAsync();

        cache.InvalidateContacts();

        await cache.CurrenciesAsync();
        await cache.TransactionTagsAsync();
        await cache.ContactsAsync();

        harness.Currencies.Verify(
            c => c.ListAllAsync(It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()), Times.Once);
        harness.Tags.Verify(
            t => t.ListAllAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        harness.Contacts.Verify(
            c => c.ListAllAsync(It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    // ── Shaping ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ActiveCurrencies_drops_archived_and_orders_by_code()
    {
        var harness = NewHarness();
        harness.Currencies
            .Setup(c => c.ListAllAsync(It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Currency("USD"), Currency("ZWL", archived: true), Currency("NOK")));
        var cache = harness.Cache;

        // The raw list keeps the archived row for callers that resolve a symbol by code.
        Assert.Equal(3, (await cache.CurrenciesAsync()).Count);

        Assert.Equal(["NOK", "USD"], (await cache.ActiveCurrenciesAsync()).Select(c => c.CurrencyCode));
    }

    [Fact]
    public async Task CurrencyOptions_carry_the_code_as_the_value_and_the_NAME_alone_as_the_label()
    {
        // The label must not repeat the code: OdsCurrencySelect / OdsMoneyField render the code
        // themselves, in its own mono gutter, so a "USD · US Dollar" label would print it twice.
        var harness = NewHarness();
        harness.Currencies
            .Setup(c => c.ListAllAsync(It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(
                Currency("USD", name: "US Dollar"),
                Currency("NOK", name: "Norwegian krone"),
                Currency("SEK", name: "Swedish krona")));

        var options = await harness.Cache.CurrencyOptionsAsync();

        Assert.Equal(["NOK", "SEK", "USD"], options.Select(o => o.Value));
        Assert.Equal(["Norwegian krone", "Swedish krona", "US Dollar"], options.Select(o => o.Label));
    }

    [Fact]
    public async Task CurrencyOptions_offer_only_the_live_currencies()
    {
        var harness = NewHarness();
        harness.Currencies
            .Setup(c => c.ListAllAsync(It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Currency("USD"), Currency("NOK", archived: true)));

        Assert.Equal(["USD"], (await harness.Cache.CurrencyOptionsAsync()).Select(o => o.Value));
    }
}
