using System.Net;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Services;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Internet;

namespace Odyssey.Client.Tests;

/// <summary>
/// The one bUnit harness for the Internet pages (issue #473): a mocked <see cref="INewsApiClient"/>, the real
/// <see cref="NewsCatalogueCache"/> over it, a fake clock, page state and the caller's claims, and a host that
/// renders the dialog and popover providers beside the page so its menus and dialogs are in the DOM.
/// </summary>
internal sealed class NewsPageTestContext : IAsyncDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);

    public static readonly string[] AllNewsClaims =
    [
        PermissionClaims.NewsRead, PermissionClaims.NewsUpdate,
        PermissionClaims.NewsFeedsCreate, PermissionClaims.NewsFeedsUpdate, PermissionClaims.NewsFeedsDelete,
        PermissionClaims.NewsTagsCreate, PermissionClaims.NewsTagsUpdate, PermissionClaims.NewsTagsDelete,
    ];

    public NewsPageTestContext(IReadOnlyList<string>? claims = null)
    {
        Ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        Ctx.Services.AddMudServices();
        Time.SetLocalTimeZone(TimeZoneInfo.Utc);
        Ctx.Services.AddSingleton<TimeProvider>(Time);
        Ctx.Services.AddScoped<RelativeTimeTicker>();
        Ctx.Services.AddSingleton(News.Object);
        Ctx.Services.AddSingleton(NewsTags.Object);
        Podcasts.Setup(p => p.ArtworkUrl(It.IsAny<Guid>(), It.IsAny<PodcastArtworkSize>()))
            .Returns((Guid id, PodcastArtworkSize size) => $"http://localhost/api/podcast-artwork/{id}/content?size={size}");
        Podcasts.Setup(p => p.AudioUrl(It.IsAny<Guid>())).Returns((Guid id) => $"http://localhost/api/podcast-episodes/{id}/audio/content");
        Ctx.Services.AddSingleton(Podcasts.Object);
        Ctx.Services.AddSingleton(sp => new Odyssey.ApiClient.Auth.AntiforgeryTokenStore(sp));
        Ctx.Services.AddSingleton(Transfer.Object);
        Ctx.Services.AddSingleton(Mock.Of<IReferenceDataCache>());
        Ctx.Services.AddSingleton(State.Object);
        Ctx.Services.AddSingleton<AuthenticationStateProvider>(new SignedInWith(claims ?? AllNewsClaims));
        Ctx.Services.AddScoped<INewsCatalogueCache, NewsCatalogueCache>();
        Ctx.Services.AddScoped<NewsStoreGate>();
        Clipboard.Setup(c => c.CopyAsync(It.IsAny<string>(), It.IsAny<string?>())).ReturnsAsync(true);
        Ctx.Services.AddSingleton(Clipboard.Object);
        Limits.Setup(l => l.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => CurrentLimits);
        Ctx.Services.AddSingleton(Limits.Object);
        Ctx.Services.AddSingleton<IClaimsRefresher>(Refresher);
    }

    /// <summary>What <see cref="Limits"/> returns; change it to stand for a Settings save.</summary>
    public ImportLimitsDto CurrentLimits { get; set; } = ImportLimitsCache.Fallback;

    public Mock<IImportLimitsCache> Limits { get; } = new();

    public FakeClaimsRefresher Refresher { get; } = new();

    public BunitContext Ctx { get; } = new();

    public Mock<INewsApiClient> News { get; } = new();

    /// <summary>The podcast endpoints (issue #556/#559): episodes, audio, artwork and the podcast feed list.</summary>
    public Mock<IPodcastsApiClient> Podcasts { get; } = new();

    /// <summary>The browser's native download, behind Save to device.</summary>
    public Mock<INativeTransfer> Transfer { get; } = new();

    /// <summary>The news tags, on the contract every tag resource shares.</summary>
    public Mock<ITagsApiClient<ExistingNewsTag>> NewsTags { get; } = new();

    public Mock<IPageStateService> State { get; } = new();

    public Mock<IClipboardService> Clipboard { get; } = new();

    public FakeTimeProvider Time { get; } = new(Now);

    /// <summary>Renders the providers and the page; the page is found with <c>FindComponent</c>.</summary>
    public IRenderedComponent<Bunit.Rendering.ContainerFragment> Render<TPage>(IReadOnlyDictionary<string, object?>? parameters = null)
        where TPage : IComponent =>
        Ctx.Render(b =>
        {
            b.OpenComponent<MudDialogProvider>(0);
            b.CloseComponent();
            b.OpenComponent<MudPopoverProvider>(10);
            b.CloseComponent();
            b.OpenComponent<MudSnackbarProvider>(15);
            b.CloseComponent();
            b.OpenComponent<TPage>(20);
            foreach (var (name, value) in parameters ?? new Dictionary<string, object?>())
            {
                b.AddComponentParameter(30, name, value);
            }

            b.CloseComponent();
        });

    public static ApiResult<T> Ok<T>(T value) => ApiResult<T>.Success(value, HttpStatusCode.OK);

    public static ApiResult<T> Fail<T>(HttpStatusCode status, string? code = null, TimeSpan? retryAfter = null,
        IDictionary<string, object>? extensions = null, IReadOnlyDictionary<string, string[]>? errors = null) =>
        new()
        {
            Status = status,
            RetryAfter = retryAfter,
            Problem = new ApiProblem
            {
                Status = (int)status,
                Code = code,
                Detail = $"Server said {(int)status}",
                Extensions = extensions?.ToDictionary(e => e.Key, e => JsonSerializer.SerializeToElement(e.Value)),
                Errors = errors ?? new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase),
            },
        };

    public static ApiResult Fail(HttpStatusCode status, string? code = null, IDictionary<string, object>? extensions = null) =>
        ApiResult.Failure(status, new ApiProblem
        {
            Status = (int)status,
            Code = code,
            Detail = $"Server said {(int)status}",
            Extensions = extensions?.ToDictionary(e => e.Key, e => JsonSerializer.SerializeToElement(e.Value)),
        });

    public static ApiResult<T> Busy<T>() => Fail<T>(HttpStatusCode.ServiceUnavailable, NewsErrorCodes.Busy, TimeSpan.FromSeconds(2));

    public ValueTask DisposeAsync() => Ctx.DisposeAsync();
}
