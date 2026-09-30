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
using Odyssey.Client.Pages.Journal;
using Odyssey.Client.Pages.Photos;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <see cref="ListLoader"/> wired into real list components (issue #249): responses arriving out of
/// order resolve to the newest request, a superseded request raises no toast, and the newest request's
/// own failure still does. The loader's unit behaviour is <see cref="ListLoaderTests"/>.
/// </summary>
/// <remarks>
/// Most list pages return early from <c>OnInitializedAsync</c> outside a browser, so these drive the
/// fetch through the same callbacks the user does — the search field's <c>OnSearch</c>, a filter's
/// <c>ValuesChanged</c> — with responses held in <see cref="TaskCompletionSource{T}"/>s the test
/// releases in the order it wants.
/// </remarks>
public class ListLoaderCardTests
{
    static ListLoaderCardTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    // ── CurrenciesCard: a server-paged table ──────────────────────────────────────────────────────

    private static ExistingCurrency Currency(string code) =>
        new() { CurrencyCode = code, Name = code + " name", Symbol = "$", MinorUnits = 2 };

    private sealed class CurrenciesHarness
    {
        public required BunitContext Context { get; init; }
        public required IRenderedComponent<CurrenciesCard> Cut { get; init; }
        public Dictionary<string, TaskCompletionSource<ApiResult<PagedResult<ExistingCurrency>>>> Pending { get; } = [];
        public Dictionary<string, CancellationToken> Tokens { get; } = [];

        public IEnumerable<string> Toasts =>
            Context.Services.GetRequiredService<ISnackbar>().ShownSnackbars.Select(s => s.Message ?? string.Empty);

        public string? Announcement => Cut.FindComponent<OdsLiveAnnouncer>().Instance.Message;

        /// <summary>Types <paramref name="text"/> and lets the debounce elapse; the fetch stays in flight.</summary>
        public Task Search(string text)
        {
            Pending[text] = new();
            var field = Cut.FindComponent<OdsSearchField>().Instance;
            Cut.InvokeAsync(() => field.ValueChanged.InvokeAsync(text)).GetAwaiter().GetResult();
            return Cut.InvokeAsync(() => field.OnSearch.InvokeAsync());
        }

        public Task Answer(string search, ApiResult<PagedResult<ExistingCurrency>> result) =>
            Cut.InvokeAsync(() => Pending[search].SetResult(result));
    }

    private static CurrenciesHarness RenderCurrencies()
    {
        var ctx = NewContext(PermissionClaims.CurrenciesRead);
        CurrenciesHarness? harness = null;

        var currencies = new Mock<ICurrenciesApiClient>();
        currencies.Setup(c => c.ListAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((int _, int _, string? search, IReadOnlyCollection<string>? _, string? _, string? _, CancellationToken ct) =>
            {
                harness!.Tokens[search ?? string.Empty] = ct;
                return harness.Pending[search ?? string.Empty].Task;
            });
        ctx.Services.AddSingleton(currencies.Object);

        harness = new CurrenciesHarness { Context = ctx, Cut = ctx.Render<CurrenciesCard>() };
        return harness;
    }

    private static ApiResult<PagedResult<T>> Page<T>(params T[] rows) =>
        ApiResult<PagedResult<T>>.Success(
            new PagedResult<T> { Items = rows, TotalCount = rows.Length, Offset = 0, Limit = 25 }, HttpStatusCode.OK);

    /// <summary>What the transport returns for a request whose token was cancelled.</summary>
    private static ApiResult<T> Cancelled<T>() => ApiResult<T>.Failure(new TaskCanceledException());

    [Fact]
    public async Task Currencies_an_older_response_landing_last_does_not_replace_the_newer()
    {
        var h = RenderCurrencies();
        await using var _ = h.Context;

        var older = h.Search("eu");
        var newer = h.Search("usd");
        Assert.True(h.Tokens["eu"].IsCancellationRequested);

        await h.Answer("usd", Page(Currency("USD")));
        await newer;
        await h.Answer("eu", Cancelled<PagedResult<ExistingCurrency>>());
        await older;

        h.Cut.WaitForAssertion(() =>
        {
            Assert.Contains("USD name", h.Cut.Markup, StringComparison.Ordinal);
            Assert.Equal("Showing 1–1 of 1 currency.", h.Announcement);
            Assert.Equal(1, h.Cut.FindComponent<OdsPager>().Instance.TotalCount);
        });
        Assert.Empty(h.Toasts);
    }

    /// <summary>
    /// The other order: the older response arrives while the newer is still in flight. It must not paint
    /// its rows in the gap, and must not clear the refetch state the newer request still owns.
    /// </summary>
    [Fact]
    public async Task Currencies_an_older_response_landing_first_is_ignored_while_the_newer_runs()
    {
        var h = RenderCurrencies();
        await using var _ = h.Context;

        var older = h.Search("eu");
        var newer = h.Search("usd");

        await h.Answer("eu", Page(Currency("EUR")));
        await older;

        Assert.DoesNotContain("EUR name", h.Cut.Markup, StringComparison.Ordinal);
        Assert.True(h.Cut.FindComponent<OdsRecordTable<ExistingCurrency>>().Instance.Loading);

        await h.Answer("usd", Page(Currency("USD")));
        await newer;

        h.Cut.WaitForAssertion(() =>
        {
            Assert.Contains("USD name", h.Cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("EUR name", h.Cut.Markup, StringComparison.Ordinal);
            Assert.False(h.Cut.FindComponent<OdsRecordTable<ExistingCurrency>>().Instance.Loading);
        });
        Assert.Empty(h.Toasts);
    }

    /// <summary>The guard must not swallow a real failure: the NEWEST request failing still toasts and shows the error state.</summary>
    [Fact]
    public async Task Currencies_a_failure_of_the_newest_request_still_toasts_and_sets_the_error_state()
    {
        var h = RenderCurrencies();
        await using var _ = h.Context;

        var older = h.Search("eu");
        var newer = h.Search("usd");

        await h.Answer("usd", ApiResult<PagedResult<ExistingCurrency>>.Failure(
            HttpStatusCode.InternalServerError, new ApiProblem { Title = "Server error" }));
        await newer;
        await h.Answer("eu", Page(Currency("EUR")));
        await older;

        h.Cut.WaitForAssertion(() =>
        {
            Assert.True(h.Cut.FindComponent<OdsRecordTable<ExistingCurrency>>().Instance.Error);
            Assert.Equal("Couldn't load currencies.", h.Announcement);
            Assert.DoesNotContain("EUR name", h.Cut.Markup, StringComparison.Ordinal);
        });
        Assert.Single(h.Toasts);
    }

    [Fact]
    public async Task Currencies_disposing_the_card_cancels_its_fetch()
    {
        var h = RenderCurrencies();
        await using var _ = h.Context;

        var pending = h.Search("eu");
        await h.Cut.InvokeAsync(() => h.Cut.Instance.Dispose());

        Assert.True(h.Tokens["eu"].IsCancellationRequested);

        await h.Answer("eu", Page(Currency("EUR")));
        await pending;
        Assert.DoesNotContain("EUR name", h.Cut.Markup, StringComparison.Ordinal);
    }

    // ── OdsTagAdmin: a shared generic component, not a page ───────────────────────────────────────

    private static ExistingJournalTag Tag(string name) => new() { JournalTagId = Guid.NewGuid(), Name = name };

    [Fact]
    public async Task TagAdmin_resolves_out_of_order_search_responses_to_the_latest()
    {
        await using var ctx = NewContext();
        var pending = new Dictionary<string, TaskCompletionSource<ApiResult<PagedResult<ExistingJournalTag>>>>
        {
            ["gro"] = new(),
            ["tra"] = new(),
        };
        var tags = new Mock<ITagsApiClient<ExistingJournalTag>>();
        tags.Setup(t => t.ListAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((int _, int _, string? search, IReadOnlyCollection<string>? _, string? _, string? _, CancellationToken _) =>
                pending[search!].Task);
        ctx.Services.AddSingleton(tags.Object);

        var cut = ctx.Render<OdsTagAdmin<ExistingJournalTag>>(p => p
            .Add(c => c.Title, "Journal tags")
            .Add(c => c.Noun, "journal tags")
            .Add(c => c.PageStateKey, "journal-tags-page")
            .Add(c => c.CreateClaim, PermissionClaims.JournalTagsCreate)
            .Add(c => c.UpdateClaim, PermissionClaims.JournalTagsUpdate)
            .Add(c => c.DeleteClaim, PermissionClaims.JournalTagsDelete)
            .Add(c => c.Id, t => t.JournalTagId)
            .Add(c => c.Name, t => t.Name)
            .Add(c => c.Description, t => t.Description)
            .Add(c => c.Archived, t => t.Archived));
        var field = cut.FindComponent<OdsSearchField>().Instance;

        await cut.InvokeAsync(() => field.ValueChanged.InvokeAsync("gro"));
        var older = cut.InvokeAsync(() => field.OnSearch.InvokeAsync());
        await cut.InvokeAsync(() => field.ValueChanged.InvokeAsync("tra"));
        var newer = cut.InvokeAsync(() => field.OnSearch.InvokeAsync());

        await cut.InvokeAsync(() => pending["tra"].SetResult(Page(Tag("Travel"))));
        await newer;
        await cut.InvokeAsync(() => pending["gro"].SetResult(Page(Tag("Groceries"), Tag("Grocery run"))));
        await older;

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Travel", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("Groceries", cut.Markup, StringComparison.Ordinal);
            Assert.Equal("Showing 1–1 of 1 tag.", cut.FindComponent<OdsLiveAnnouncer>().Instance.Message);
        });
        Assert.Empty(ctx.Services.GetRequiredService<ISnackbar>().ShownSnackbars);
    }

    // ── PhotosCard: a grid with an append-only "Load more" ───────────────────────────────────────

    private static PhotoSummary Photo(string title) => new()
    {
        PhotoId = Guid.NewGuid(),
        FileId = Guid.NewGuid(),
        Title = title,
        PersonCount = 0,
        AlbumCount = 0,
    };

    private sealed record PhotoCall(string? Search, int Page, CancellationToken Token,
        TaskCompletionSource<ApiResult<PagedResult<PhotoSummary>>> Response);

    private static (BunitContext Ctx, IRenderedComponent<PhotosCard> Cut, List<PhotoCall> Calls) RenderPhotos()
    {
        var ctx = NewContext(PermissionClaims.PhotosRead);
        var calls = new List<PhotoCall>();
        var photos = new Mock<IPhotosApiClient> { DefaultValueProvider = new EmptySuccess() };
        photos.Setup(p => p.ListAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns((int page, int pageSize, string? search, IReadOnlyCollection<string>? _, IReadOnlyCollection<string>? _, IReadOnlyCollection<string>? _,
                    DateTime? _, DateTime? _, bool _, string? _, string? _, string? _, CancellationToken ct) =>
            {
                // The subline's whole-library count asks for a one-row window; answer it at once.
                if (pageSize == 1)
                    return Task.FromResult(Page<PhotoSummary>());

                var call = new PhotoCall(search, page, ct, new());
                calls.Add(call);
                return call.Response.Task;
            });
        ctx.Services.AddSingleton(photos.Object);

        return (ctx, ctx.Render<PhotosCard>(), calls);
    }

    private static ApiResult<PagedResult<PhotoSummary>> PhotoPage(int total, params PhotoSummary[] rows) =>
        ApiResult<PagedResult<PhotoSummary>>.Success(
            new PagedResult<PhotoSummary> { Items = rows, TotalCount = total, Offset = 0, Limit = 24 }, HttpStatusCode.OK);

    private static Task SubmitPhotoSearch(IRenderedComponent<PhotosCard> cut, string text)
    {
        var field = cut.FindComponent<OdsSearchField>().Instance;
        cut.InvokeAsync(() => field.ValueChanged.InvokeAsync(text)).GetAwaiter().GetResult();
        return cut.InvokeAsync(() => field.OnSearch.InvokeAsync());
    }

    /// <summary>
    /// A reload in flight owns the grid. "Load more" clicked in that gap would append a page of the OLD
    /// query onto the new result set — so it is ignored, and no request is sent.
    /// </summary>
    [Fact]
    public async Task Photos_load_more_is_ignored_while_a_reload_is_running()
    {
        var (ctx, cut, calls) = RenderPhotos();
        await using var _ = ctx;

        cut.WaitForState(() => calls.Count == 1);
        await cut.InvokeAsync(() => calls[0].Response.SetResult(PhotoPage(30, Photo("Fjord"))));
        cut.WaitForState(() => cut.FindAll("button.pl-drop-btn").Count == 1);

        var reload = SubmitPhotoSearch(cut, "cabin");
        Assert.Equal(2, calls.Count);

        await cut.InvokeAsync(() => cut.Find("button.pl-drop-btn").Click());
        Assert.Equal(2, calls.Count);

        await cut.InvokeAsync(() => calls[1].Response.SetResult(PhotoPage(1, Photo("Cabin"))));
        await reload;

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Cabin", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("Fjord", cut.Markup, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// A superseded reload returns no load at all, so the grid is left to the newer one and the older
    /// request's cancellation — a failure, as the transport reports it — raises no toast.
    /// </summary>
    [Fact]
    public async Task Photos_a_superseded_reload_leaves_the_grid_and_raises_no_toast()
    {
        var (ctx, cut, calls) = RenderPhotos();
        await using var _ = ctx;

        cut.WaitForState(() => calls.Count == 1);
        await cut.InvokeAsync(() => calls[0].Response.SetResult(PhotoPage(0)));

        var older = SubmitPhotoSearch(cut, "fjord");
        var newer = SubmitPhotoSearch(cut, "cabin");
        Assert.True(calls[1].Token.IsCancellationRequested);

        await cut.InvokeAsync(() => calls[2].Response.SetResult(PhotoPage(1, Photo("Cabin"))));
        await newer;
        await cut.InvokeAsync(() => calls[1].Response.SetResult(Cancelled<PagedResult<PhotoSummary>>()));
        await older;

        cut.WaitForAssertion(() => Assert.Contains("Cabin", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("Couldn't load", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(ctx.Services.GetRequiredService<ISnackbar>().ShownSnackbars);
    }

    // ── JournalCard: Active + Archived as one concurrent pair ────────────────────────────────────

    private static JournalEntrySummary Entry(string title) => new()
    {
        JournalEntryId = Guid.NewGuid(),
        Title = title,
        Content = string.Empty,
        EntryDate = new DateTime(2026, 9, 1),
        CreatedAt = new DateTime(2026, 9, 1),
        UpdatedAt = new DateTime(2026, 9, 1),
        AttachmentCount = 0,
    };

    private sealed record JournalCall(string? Status, string? Search,
        TaskCompletionSource<ApiResult<List<JournalEntrySummary>>> Response);

    private static (BunitContext Ctx, IRenderedComponent<JournalCard> Cut, List<JournalCall> Calls) RenderJournal()
    {
        var ctx = NewContext(PermissionClaims.JournalRead);
        var calls = new List<JournalCall>();
        var journal = new Mock<IJournalApiClient> { DefaultValueProvider = new EmptySuccess() };
        journal.Setup(j => j.ListAsync(It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<bool?>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .Returns((string? search, IReadOnlyCollection<string>? _, IReadOnlyCollection<string>? _, string? status, string? _, string? _,
                    DateTime? _, DateTime? _, bool? _, bool? _, CancellationToken _) =>
            {
                var call = new JournalCall(status, search, new());
                calls.Add(call);
                return call.Response.Task;
            });
        ctx.Services.AddSingleton(journal.Object);

        return (ctx, ctx.Render<JournalCard>(), calls);
    }

    private static Task ShowActiveAndArchived(IRenderedComponent<JournalCard> cut)
    {
        var status = cut.FindComponents<OdsMultiSelect>().Single(m => m.Instance.Label == "Active").Instance;
        return cut.InvokeAsync(() => status.ValuesChanged.InvokeAsync(
            [nameof(Odyssey.Dtos.Journal.ArchivalStatus.Active), nameof(Odyssey.Dtos.Journal.ArchivalStatus.Archived)]));
    }

    private static string? JournalAnnouncement(IRenderedComponent<JournalCard> cut) =>
        cut.FindComponent<OdsLiveAnnouncer>().Instance.Message;

    private static ApiResult<List<JournalEntrySummary>> Entries(params JournalEntrySummary[] rows) =>
        ApiResult<List<JournalEntrySummary>>.Success([.. rows], HttpStatusCode.OK);

    /// <summary>Both legs are in flight before either answers, and the two lists are joined.</summary>
    [Fact]
    public async Task Journal_fetches_active_and_archived_concurrently_and_joins_them()
    {
        var (ctx, cut, calls) = RenderJournal();
        await using var _ = ctx;

        var load = ShowActiveAndArchived(cut);

        Assert.Equal(["Active", "Archived"], calls.Select(c => c.Status));

        await cut.InvokeAsync(() => calls[1].Response.SetResult(Entries(Entry("Old trip"))));
        await cut.InvokeAsync(() => calls[0].Response.SetResult(Entries(Entry("New trip"))));
        await load;

        cut.WaitForAssertion(() => Assert.Equal("Showing 2 entries.", JournalAnnouncement(cut)));
        Assert.Empty(ctx.Services.GetRequiredService<ISnackbar>().ShownSnackbars);
    }

    /// <summary>Either leg failing makes the joined list untrustworthy: error state, and that leg's toast.</summary>
    [Fact]
    public async Task Journal_one_failed_leg_marks_the_pair_failed()
    {
        var (ctx, cut, calls) = RenderJournal();
        await using var _ = ctx;

        var load = ShowActiveAndArchived(cut);

        await cut.InvokeAsync(() => calls[0].Response.SetResult(Entries(Entry("New trip"))));
        await cut.InvokeAsync(() => calls[1].Response.SetResult(ApiResult<List<JournalEntrySummary>>.Failure(
            HttpStatusCode.InternalServerError, new ApiProblem { Title = "Server error" })));
        await load;

        cut.WaitForAssertion(() => Assert.Equal("Couldn't load journal entries.", JournalAnnouncement(cut)));
        Assert.Single(ctx.Services.GetRequiredService<ISnackbar>().ShownSnackbars);
    }

    /// <summary>A whole pair is superseded as one: its legs landing after the newer pair change nothing.</summary>
    [Fact]
    public async Task Journal_a_superseded_pair_is_ignored_as_a_whole()
    {
        var (ctx, cut, calls) = RenderJournal();
        await using var _ = ctx;

        var older = ShowActiveAndArchived(cut);
        var search = cut.FindComponent<OdsSearchField>().Instance;
        await cut.InvokeAsync(() => search.ValueChanged.InvokeAsync("trip"));
        var newer = cut.InvokeAsync(() => search.OnSearch.InvokeAsync());
        Assert.Equal(4, calls.Count);

        await cut.InvokeAsync(() => calls[2].Response.SetResult(Entries(Entry("New trip"))));
        await cut.InvokeAsync(() => calls[3].Response.SetResult(Entries()));
        await newer;
        await cut.InvokeAsync(() => calls[0].Response.SetResult(Cancelled<List<JournalEntrySummary>>()));
        await cut.InvokeAsync(() => calls[1].Response.SetResult(Entries(Entry("A"), Entry("B"))));
        await older;

        cut.WaitForAssertion(() => Assert.Equal("Showing 1 entry.", JournalAnnouncement(cut)));
        Assert.Empty(ctx.Services.GetRequiredService<ISnackbar>().ShownSnackbars);
    }

    // ── Shared set-up ─────────────────────────────────────────────────────────────────────────────

    private static BunitContext NewContext(params string[] permissions)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(Mock.Of<IClipboardService>());
        ctx.Services.AddSingleton(new Mock<IPageStateService> { DefaultValueProvider = new EmptySuccess() }.Object);
        ctx.Services.AddSingleton(new Mock<IReferenceDataCache> { DefaultValueProvider = new EmptySuccess() }.Object);
        ctx.Services.AddSingleton<AuthenticationStateProvider>(new SignedIn(permissions));

        // Everything else a page (or a dialog it keeps mounted) injects is an auto-mock answering with
        // an empty success, so only the list under test is ever in flight.
        ctx.Services.AddFallbackServiceProvider(new AutoMocks());
        return ctx;
    }

    private sealed class AutoMocks : IServiceProvider
    {
        private readonly Dictionary<Type, object> made = [];

        public object? GetService(Type serviceType)
        {
            // Only the app's own services: the framework asks the provider for optional services of
            // its own (a component activator, say) and must keep getting its defaults for those.
            if (!serviceType.IsInterface || serviceType.Namespace?.StartsWith("Odyssey.", StringComparison.Ordinal) != true)
                return null;

            if (!made.TryGetValue(serviceType, out var instance))
            {
                var mock = (Mock)Activator.CreateInstance(typeof(Mock<>).MakeGenericType(serviceType))!;
                mock.DefaultValueProvider = new EmptySuccess();
                made[serviceType] = instance = mock.Object;
            }

            return instance;
        }
    }

    /// <summary>
    /// A Moq default that answers every <c>Task&lt;ApiResult&lt;T&gt;&gt;</c> with an empty success, rather
    /// than Moq's <c>null</c> result, which the pages' unwrapping would dereference.
    /// </summary>
    private sealed class EmptySuccess : DefaultValueProvider
    {
        protected override object GetDefaultValue(Type type, Mock mock)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = type.GetGenericArguments()[0];
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [Value(inner)])!;
            }

            return type == typeof(Task) ? Task.CompletedTask : Value(type)!;
        }

        private static object? Value(Type type)
        {
            if (type == typeof(ApiResult))
                return ApiResult.Success(HttpStatusCode.OK);

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ApiResult<>))
            {
                var t = type.GetGenericArguments()[0];
                var value = t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>) ? Activator.CreateInstance(t) : null;
                return type.GetMethod(nameof(ApiResult<int>.Success))!.Invoke(null, [value, HttpStatusCode.OK, null]);
            }

            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }

    private sealed class SignedIn(IEnumerable<string> permissions) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                permissions.Select(p => new Claim(PermissionClaims.Type, p)), "test"))));
    }
}
