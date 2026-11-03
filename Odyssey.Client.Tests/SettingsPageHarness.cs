using System.Net;
using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Pages;
using Odyssey.Client.Services;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;

namespace Odyssey.Client.Tests;

/// <summary>
/// The one bUnit harness for rendering the System settings page: every service the page and its
/// panels inject, faked, plus the interactivity seam. A class using it must join
/// <see cref="SettingsPageCollection"/>, since the seam is process-wide. Keeping a single copy is the
/// point — a new injection on the page is registered here once instead of hand-patched into each
/// suite (the <c>AuthApiClient</c> registration was patched into four copies separately).
/// </summary>
internal static class SettingsPageHarness
{
    /// <summary>The claims an administrator holds over the page.</summary>
    public static readonly IReadOnlyList<string> AdminClaims =
    [
        PermissionClaims.SystemSettingsRead,
        PermissionClaims.SystemSettingsUpdate,
        PermissionClaims.SystemSettingsSecurityUpdate,
    ];

    /// <summary>A settings client whose GET answers <paramref name="dto"/>.</summary>
    public static Mock<ISystemSettingsApiClient> SettingsClient(SystemSettingsDto dto)
    {
        var settings = new Mock<ISystemSettingsApiClient>();
        settings.Setup(client => client.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<SystemSettingsDto>.Success(dto, HttpStatusCode.OK));
        return settings;
    }

    /// <param name="settings">The system-settings client.</param>
    /// <param name="secrets">The secret-settings client; null answers an empty status list.</param>
    /// <param name="storage">The file-storage client; null is an unconfigured mock.</param>
    /// <param name="claims">The caller's permissions; null is <see cref="AdminClaims"/>.</param>
    public static BunitContext NewPageContext(
        ISystemSettingsApiClient settings,
        ISecretSettingsApiClient? secrets = null,
        IFileStorageApiClient? storage = null,
        IReadOnlyList<string>? claims = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();

        if (secrets is null)
        {
            var mock = new Mock<ISecretSettingsApiClient>();
            mock.Setup(client => client.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(ApiResult<List<SecretSettingStatusDto>>.Success([], HttpStatusCode.OK));
            secrets = mock.Object;
        }

        ctx.Services.AddSingleton(settings);
        ctx.Services.AddSingleton(secrets);
        ctx.Services.AddSingleton(ExchangeRateSyncFakes.Client().Object);
        ctx.Services.AddSingleton(storage ?? new Mock<IFileStorageApiClient>().Object);
        ctx.Services.AddSingleton(new Mock<IPhotosApiClient>().Object);
        ctx.Services.AddSingleton(new Mock<IDataExportApiClient>().Object);
        ctx.Services.AddSingleton(new Mock<IWeatherApiClient>().Object);
        ctx.Services.AddSingleton<IWeatherPointOptionsCache>(sp => new WeatherPointOptionsCache(
            sp.GetRequiredService<IWeatherApiClient>(), sp.GetRequiredService<AuthenticationStateProvider>()));
        ctx.Services.AddSingleton(new Mock<IPageStateService>().Object);
        ctx.Services.AddSingleton(new Mock<IImportLimitsCache>().Object);
        ctx.Services.AddSingleton(new Mock<IUploadLimitsCache>().Object);
        ctx.Services.AddSingleton(new Mock<IAccountLimitsCache>().Object);
        ctx.Services.AddSingleton(new Mock<IContractLimitsCache>().Object);
        ctx.Services.AddSingleton(new Mock<IPropertyLimitsCache>().Object);
        ctx.Services.AddSingleton(new Mock<IFileAnalysisDisclosureCache>().Object);
        ctx.Services.AddSingleton(sp => new Odyssey.ApiClient.Auth.AuthApiClient(
            new HttpClient(), new Odyssey.ApiClient.Auth.AntiforgeryTokenStore(sp)));
        ctx.Services.AddSingleton<AuthenticationStateProvider>(new PermissionsAuthenticationStateProvider(claims ?? AdminClaims));

        Settings.InteractiveCheck = static () => true;
        return ctx;
    }

    /// <summary>An authenticated principal carrying exactly <paramref name="permissions"/>.</summary>
    public sealed class PermissionsAuthenticationStateProvider(IReadOnlyList<string> permissions)
        : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                permissions.Select(permission => new Claim(PermissionClaims.Type, permission)), "Test"))));
    }
}
