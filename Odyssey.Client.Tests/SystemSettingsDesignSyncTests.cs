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
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The System settings page's alignment with the design system (ui_kits/web/SystemSettings.jsx and
/// system-settings-data.js): the section order, the explicit tighten-only rows, the per-row "last
/// changed" stamp, the header rollup's label and the persistent alert a 403 save raises.
/// </summary>
[Collection(SettingsPageCollection.Name)]
public class SystemSettingsDesignSyncTests : IDisposable
{
    /// <summary>Restores the page's browser-check seam, which is <c>internal static</c>.</summary>
    public void Dispose() => Settings.InteractiveCheck = static () => OperatingSystem.IsBrowser();

    [Fact]
    public void Sections_follow_the_design_systems_order()
    {
        Assert.Equal(
            [
                "Security", "File analysis", "Email", "Data", "Weather", "Contracts", "Photos", "Storage",
                "Journal limits", "Contacts import & export", "Calendars import & export",
                "Tasks import & export", "Journal import & export", "Calendar limits",
                "Import & export defaults", "Accounts", "Exchange rates", "Properties",
            ],
            Settings.Sections.Select(section => section.Group).ToArray());
    }

    /// <summary>
    /// "lower only" is the design's <c>ceiling</c>, not "the row has a published bound": the two
    /// aggregate calendar caps and the metadata read size publish theirs only so the control can bound
    /// itself, and marking them one-way would tell an administrator they cannot raise a value they can.
    /// </summary>
    [Fact]
    public void Only_the_design_systems_ceiling_rows_are_lower_only()
    {
        Assert.Equal(
            new[]
            {
                "contactVCardMaxRepeatablePropertiesPerEntry", "fileStorageMaxUploadMegabytes",
                "photoMaxAlbumMembers", "photoMaxLinksPerKind", "recurrenceMaxGeneratedOccurrences",
            },
            Settings.AllItems.Where(item => item.LowerOnly).Select(item => item.Key).Order(StringComparer.Ordinal));

        // Every lower-only row has a published ceiling to quote in its range sentence.
        Assert.All(Settings.AllItems.Where(item => item.LowerOnly), item => Assert.NotNull(item.MaxFrom));
    }

    [Theory]
    [InlineData(true, true, "Credentials")]
    [InlineData(true, false, "Credentials")]
    [InlineData(false, true, "Storage")]
    [InlineData(false, false, "Email")]
    public void The_rollup_label_names_its_most_serious_kind(bool unreadable, bool storage, string expected) =>
        Assert.Equal(expected, Settings.SetupProblemsLabelFor(unreadable, storage));

    [Fact]
    public void The_credential_clear_advisory_points_at_a_card_that_exists() =>
        Assert.Contains(Settings.Sections, section => section.Group == Settings.RelayCredentialGroup);

    [Fact]
    public async Task Each_row_carries_its_own_last_changed_stamp()
    {
        var dto = Valid();
        dto.Provenance = new Dictionary<string, SettingProvenance>
        {
            [nameof(SystemSettingsUpdate.EmailFromName)] = new()
            {
                UpdatedAt = new DateTime(2026, 8, 2, 9, 10, 0, DateTimeKind.Utc),
                UpdatedByDisplayName = "Marcus Reyes",
            },
        };

        await using var ctx = NewPageContext(SettingsClient(dto));
        var page = ctx.Render<Settings>();

        var changed = page.Find("[data-ss-row=emailFromName] .odc-sfield-stamp").TextContent;
        Assert.StartsWith("Last changed by Marcus Reyes, ", changed, StringComparison.Ordinal);

        var untouched = page.Find("[data-ss-row=emailFromAddress] .odc-sfield-stamp").TextContent;
        Assert.Equal("Never changed — default value.", untouched);

        // The page-level summary line is gone: the stamp lives on the row it describes.
        Assert.Empty(page.FindAll(".ss-lastchanged"));
    }

    [Fact]
    public async Task A_save_refused_with_403_raises_the_persistent_sign_out_alert()
    {
        var settings = SettingsClient(Valid());
        settings.Setup(client => client.UpdateAsync(It.IsAny<SystemSettingsUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<SystemSettingsDto>.Failure(
                HttpStatusCode.Forbidden, new ApiProblem { Title = "Forbidden" }));

        await using var ctx = NewPageContext(settings);
        var page = ctx.Render<Settings>();

        page.Find("#ss-in-emailFromName").Input("Odyssey Mail");
        page.FindAll("button").Single(button =>
            button.TextContent.Contains("Save changes", StringComparison.Ordinal)).Click();

        var alert = page.WaitForElement(".ss-authalert");
        Assert.Equal("alert", alert.GetAttribute("role"));
        Assert.Contains("withdrawn", alert.TextContent, StringComparison.Ordinal);
        Assert.Contains(alert.QuerySelectorAll("button"), button => button.TextContent.Contains("Sign out", StringComparison.Ordinal));

        // The next edit dismisses it, as the design's page does.
        page.Find("#ss-in-emailFromName").Input("Odyssey Mail 2");
        Assert.Empty(page.FindAll(".ss-authalert"));
    }

    [Fact]
    public void Section_headings_are_h2()
    {
        var markup = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages/Settings.razor"));
        Assert.Contains("<h2 class=\"ss-sect-t\"", markup, StringComparison.Ordinal);
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    private static Mock<ISystemSettingsApiClient> SettingsClient(SystemSettingsDto dto)
    {
        var settings = new Mock<ISystemSettingsApiClient>();
        settings.Setup(client => client.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<SystemSettingsDto>.Success(dto, HttpStatusCode.OK));
        return settings;
    }

    /// <summary>A DTO every row is valid against, with mail configured so the rollup stays empty.</summary>
    private static SystemSettingsDto Valid()
    {
        var dto = new SystemSettingsDto();
        var properties = typeof(SystemSettingsDto).GetProperties()
            .ToDictionary(property => property.Name, StringComparer.Ordinal);

        foreach (var property in properties.Values.Where(p => p.PropertyType == typeof(int)))
        {
            if (property.Name.EndsWith("Ceiling", StringComparison.Ordinal))
            {
                property.SetValue(dto, 1_000_000);
            }
            else if (property.Name.EndsWith("Floor", StringComparison.Ordinal))
            {
                property.SetValue(dto, 1);
            }
        }

        foreach (var item in Settings.AllItems)
        {
            if (item.Field is not { } field || !properties.TryGetValue(field, out var property))
            {
                continue;
            }

            if (property.PropertyType == typeof(int))
            {
                property.SetValue(dto, Math.Max(item.Min, 1));
            }
            else if (property.PropertyType == typeof(decimal))
            {
                property.SetValue(dto, item.DecimalMin);
            }
            else if (property.PropertyType == typeof(string) && !item.AllowEmpty)
            {
                property.SetValue(dto, "x");
            }
        }

        dto.EmailSmtpHost = "smtp.example.test";
        dto.EmailSmtpPort = 587;
        dto.EmailUseStartTls = true;
        return dto;
    }

    private static BunitContext NewPageContext(Mock<ISystemSettingsApiClient> settings)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();

        var secrets = new Mock<ISecretSettingsApiClient>();
        secrets.Setup(client => client.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<SecretSettingStatusDto>>.Success([], HttpStatusCode.OK));

        ctx.Services.AddSingleton(settings.Object);
        ctx.Services.AddSingleton(secrets.Object);
        ctx.Services.AddSingleton(ExchangeRateSyncFakes.Client().Object);
        ctx.Services.AddSingleton(new Mock<IFileStorageApiClient>().Object);
        ctx.Services.AddSingleton(new Mock<IPhotosApiClient>().Object);
        ctx.Services.AddSingleton(new Mock<IDataExportApiClient>().Object);
        ctx.Services.AddSingleton(new Mock<IWeatherApiClient>().Object);
        ctx.Services.AddSingleton(new Mock<IPageStateService>().Object);
        ctx.Services.AddSingleton(new Mock<IImportLimitsCache>().Object);
        ctx.Services.AddSingleton(new Mock<IUploadLimitsCache>().Object);
        ctx.Services.AddSingleton(new Mock<IAccountLimitsCache>().Object);
        ctx.Services.AddSingleton(new Mock<IContractLimitsCache>().Object);
        ctx.Services.AddSingleton(new Mock<IPropertyLimitsCache>().Object);
        ctx.Services.AddSingleton(new Mock<IFileAnalysisDisclosureCache>().Object);
        ctx.Services.AddSingleton(sp => new Odyssey.ApiClient.Auth.AuthApiClient(
            new HttpClient(), new Odyssey.ApiClient.Auth.AntiforgeryTokenStore(sp)));
        ctx.Services.AddSingleton<AuthenticationStateProvider>(new StubAuthenticationStateProvider(
        [
            PermissionClaims.SystemSettingsRead,
            PermissionClaims.SystemSettingsUpdate,
            PermissionClaims.SystemSettingsSecurityUpdate,
        ]));

        Settings.InteractiveCheck = static () => true;
        return ctx;
    }

    private sealed class StubAuthenticationStateProvider(IReadOnlyList<string> permissions)
        : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                permissions.Select(permission => new Claim(PermissionClaims.Type, permission)), "Test"))));
    }
}
