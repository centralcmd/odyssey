using System.Net;
using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages;
using Odyssey.Client.Pages.Storage;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The Storage card on <c>/settings</c> (issue #346; Odyssey Design System · StorageHealth.jsx): the health
/// panel in each state, its claim gate, its one row in the page-header signal, and the deletion grace row.
/// </summary>
[Collection(SettingsPageCollection.Name)]
public class StorageHealthSurfaceTests : IDisposable
{
    public void Dispose() => Settings.InteractiveCheck = static () => OperatingSystem.IsBrowser();

    private static FileStorageVolumeStatus Volume(Action<FileStorageVolumeStatus>? change = null)
    {
        var volume = new FileStorageVolumeStatus
        {
            Name = "primary",
            State = FileStorageVolumeState.Available,
            InitializedAtUtc = new DateTime(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc),
            BoundFeatures = ["PhotoThumbnails"],
            Features = [new FileStorageFeatureUsage { FeatureKey = "PhotoThumbnails", ObjectCount = 18234, CommittedBytes = 912345678 }],
            PendingDeleteCount = 12,
            FreeBytes = 412316860416,
            TotalBytes = 499963174912,
            LastScrubAtUtc = new DateTime(2026, 10, 29, 17, 0, 0, DateTimeKind.Utc),
            MaintenanceLastCompletedAtUtc = new DateTime(2026, 10, 29, 17, 20, 0, DateTimeKind.Utc),
        };
        change?.Invoke(volume);
        return volume;
    }

    // ── The issue list (sthIssues) ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_healthy_volume_has_no_issues()
    {
        Assert.Empty(StorageHealth.Issues([Volume()]));
        Assert.Empty(StorageHealth.Issues(null));
    }

    [Fact]
    public void Issues_are_ordered_most_severe_first_and_cover_every_condition()
    {
        var issues = StorageHealth.Issues(
        [
            Volume(v =>
            {
                v.ReconciliationHalted = true;
                v.LastPassOrphanCount = 1240;
                v.OrphanedFeatures = ["ContactAvatars"];
                v.MaintenanceStale = true;
            }),
            Volume(v =>
            {
                v.Name = "archive";
                v.State = FileStorageVolumeState.NotConfigured;
                v.Features = [];
            }),
            Volume(v =>
            {
                v.Name = "fast";
                v.State = FileStorageVolumeState.Degraded;
                v.IntegrityFaultCount = 1;
            }),
        ]);

        Assert.Equal(
            ["deg-fast", "int-fast", "halt-primary", "orph-primary", "stale", "nc-archive"],
            issues.Select(i => i.Id));
        Assert.Equal("1 file on fast failed verification.", issues[1].Title);
        Assert.Contains("1,240 files", issues[2].Text, StringComparison.Ordinal);
        Assert.StartsWith("ContactAvatars.", issues[3].Text, StringComparison.Ordinal);
        Assert.Equal("File clean-up hasn’t completed in over 3 hours.", issues[4].Title);
        Assert.Equal(StorageIssueSeverity.Info, issues[5].Severity);
    }

    [Fact]
    public void Clean_up_that_never_completed_says_so() =>
        Assert.Equal("File clean-up has never completed.",
            Assert.Single(StorageHealth.Issues([Volume(v => { v.MaintenanceStale = true; v.MaintenanceLastCompletedAtUtc = null; })])).Title);

    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(1023, "1023 bytes")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(912345678, "870 MB")]
    [InlineData(4404019, "4.2 MB")]
    [InlineData(412316860416, "384 GB")]
    public void Bytes_read_in_1024_steps(long bytes, string expected) => Assert.Equal(expected, StorageHealth.Bytes(bytes));

    // ── The panel ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_healthy_panel_says_everything_is_working_and_shows_each_tile_as_text()
    {
        await using var ctx = NewComponentContext();

        var panel = ctx.Render<StorageHealthPanel>(p => p
            .Add(x => x.Volumes, [Volume()])
            .Add(x => x.CheckedAt, DateTime.UtcNow));

        Assert.Contains("Everything is working", panel.Markup, StringComparison.Ordinal);
        Assert.Contains("Volume · <span class=\"mono\"", panel.Markup, StringComparison.Ordinal);
        Assert.Contains("Available", panel.Find(".sth-tone.ok").TextContent, StringComparison.Ordinal);
        Assert.Contains("18,234 · 870 MB", panel.Markup, StringComparison.Ordinal);
        Assert.Contains("Photo thumbnails", panel.Markup, StringComparison.Ordinal);
        Assert.Contains("No faults", panel.Markup, StringComparison.Ordinal);
        Assert.Contains("Clean", panel.Markup, StringComparison.Ordinal);
        Assert.Contains("On schedule", panel.Markup, StringComparison.Ordinal);
        Assert.Equal("primary: 18% used", panel.Find("[role=progressbar]").GetAttribute("aria-label"));
        Assert.Equal("ss-storage-health", panel.Find("section.sth").Id);
        Assert.Empty(panel.FindAll(".sth-note"));
    }

    [Fact]
    public async Task Conditions_render_as_notes_with_the_volume_state_as_a_word()
    {
        await using var ctx = NewComponentContext();

        var panel = ctx.Render<StorageHealthPanel>(p => p.Add(x => x.Volumes,
        [
            Volume(v => { v.State = FileStorageVolumeState.Degraded; v.IntegrityFaultCount = 3; v.MaintenanceStale = true; }),
        ]));

        var notes = panel.FindAll(".sth-notes > li");
        Assert.Equal(3, notes.Count);
        Assert.Contains("error", notes[0].ClassName, StringComparison.Ordinal);
        Assert.Contains("Volume primary is degraded.", notes[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Degraded", panel.Find(".sth-tone.error").TextContent, StringComparison.Ordinal);
        Assert.Contains("3 failed", panel.Markup, StringComparison.Ordinal);
        Assert.Contains("Stalled", panel.Markup, StringComparison.Ordinal);
        Assert.Equal("Needs attention", panel.Find(".sth-notes").GetAttribute("aria-label"));
    }

    [Fact]
    public async Task An_unknown_feature_key_is_shown_as_its_key_with_a_per_feature_breakdown()
    {
        await using var ctx = NewComponentContext();

        var panel = ctx.Render<StorageHealthPanel>(p => p.Add(x => x.Volumes,
        [
            Volume(v => v.Features =
            [
                new FileStorageFeatureUsage { FeatureKey = "PhotoThumbnails", ObjectCount = 2, CommittedBytes = 2048 },
                new FileStorageFeatureUsage { FeatureKey = "ContactAvatars", ObjectCount = 96, CommittedBytes = 4404019 },
            ]),
        ]));

        var features = panel.FindAll(".sth-feat");
        Assert.Equal(2, features.Count);
        Assert.Equal("ContactAvatars", features[1].QuerySelector(".mono")!.TextContent);
        Assert.Contains("96 · 4.2 MB", features[1].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_volume_no_longer_configured_shows_only_its_state_and_files()
    {
        await using var ctx = NewComponentContext();

        var panel = ctx.Render<StorageHealthPanel>(p => p.Add(x => x.Volumes,
        [
            Volume(),
            Volume(v => { v.Name = "archive"; v.State = FileStorageVolumeState.NotConfigured; v.Features = []; v.FreeBytes = null; v.TotalBytes = null; }),
        ]));

        var archive = panel.FindAll(".sth-tile")[1];
        Assert.Contains("Not configured", archive.TextContent, StringComparison.Ordinal);
        Assert.Contains("None", archive.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Integrity", archive.TextContent, StringComparison.Ordinal);
        Assert.Contains("is recorded but no longer configured", panel.Find(".sth-note.info").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_no_volumes_the_panel_says_storage_is_not_set_up()
    {
        await using var ctx = NewComponentContext();

        var panel = ctx.Render<StorageHealthPanel>(p => p.Add(x => x.Volumes, []));

        Assert.Contains("No file volumes are configured.", panel.Markup, StringComparison.Ordinal);
        Assert.Empty(panel.FindAll(".sth-tile"));
    }

    [Fact]
    public async Task Loading_is_busy_and_an_error_offers_retry()
    {
        await using var ctx = NewComponentContext();
        var retried = false;

        var loading = ctx.Render<StorageHealthPanel>(p => p.Add(x => x.Phase, StorageHealthPhase.Loading));
        Assert.Equal("true", loading.Find("section.sth").GetAttribute("aria-busy"));
        Assert.Contains("Loading storage health", loading.Markup, StringComparison.Ordinal);

        var error = ctx.Render<StorageHealthPanel>(p => p
            .Add(x => x.Phase, StorageHealthPhase.Error)
            .Add(x => x.OnRetry, () => retried = true));
        Assert.Equal("alert", error.Find(".sth-note.error").GetAttribute("role"));
        error.Find(".sth-note.error button").Click();
        Assert.True(retried);
    }

    // ── The page ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task With_the_claim_the_storage_card_opens_with_the_panel_above_its_two_settings()
    {
        await using var ctx = NewPageContext([Volume()], out var storage);

        var page = ctx.Render<Settings>();

        var card = page.Find("#ss-grp-Storage").Closest("section")!;
        var panel = card.QuerySelector("section.sth");
        Assert.NotNull(panel);
        Assert.NotNull(card.QuerySelector("[data-ss-row=fileStorageDeletionGraceHours]"));
        Assert.NotNull(card.QuerySelector("[data-ss-row=fileStorageMaxUploadMegabytes]"));
        storage.Verify(client => client.GetVolumesAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.Null(page.FindComponent<PageHeader>().Instance.Problems);
    }

    [Fact]
    public async Task Without_the_claim_there_is_no_panel_and_no_request()
    {
        await using var ctx = NewPageContext([Volume(v => v.State = FileStorageVolumeState.Degraded)], out var storage,
            claims: [PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate, PermissionClaims.SystemSettingsSecurityUpdate]);

        var page = ctx.Render<Settings>();

        Assert.Empty(page.FindAll("section.sth"));
        storage.Verify(client => client.GetVolumesAsync(It.IsAny<CancellationToken>()), Times.Never);
        Assert.Null(page.FindComponent<PageHeader>().Instance.Problems);
    }

    [Fact]
    public async Task Storage_conditions_join_the_header_signal_as_one_row_at_the_worst_severity_for_a_read_only_admin()
    {
        await using var ctx = NewPageContext(
            [Volume(v => { v.MaintenanceStale = true; v.ReconciliationHalted = true; v.IntegrityFaultCount = 2; })], out _,
            claims: [PermissionClaims.SystemSettingsRead, PermissionClaims.FileStorageRead]);

        var page = ctx.Render<Settings>();

        var problem = Assert.Single(page.FindComponent<PageHeader>().Instance.Problems!);
        Assert.Equal(PageHeaderSeverity.Error, problem.Severity);
        Assert.Equal("2 files on primary failed verification", problem.Lead);
        Assert.Equal("And 2 more storage conditions.", problem.Message);
        Assert.Equal("In Storage.", problem.Where);
        Assert.Equal("View", problem.ViewLabel);
        Assert.True(problem.OnView.HasDelegate);
    }

    [Fact]
    public async Task An_informational_condition_stays_in_the_panel()
    {
        await using var ctx = NewPageContext(
            [Volume(), Volume(v => { v.Name = "archive"; v.State = FileStorageVolumeState.NotConfigured; v.Features = []; })], out _);

        var page = ctx.Render<Settings>();

        Assert.Null(page.FindComponent<PageHeader>().Instance.Problems);
        Assert.NotEmpty(page.FindAll(".sth-note.info"));
    }

    [Fact]
    public async Task A_failed_load_shows_the_panels_retry_and_leaves_the_settings_alone()
    {
        await using var ctx = NewPageContext(null, out var storage);

        var page = ctx.Render<Settings>();

        Assert.Contains("Couldn’t load storage health.", page.Find("section.sth").TextContent, StringComparison.Ordinal);
        Assert.NotNull(page.Find("[data-ss-row=fileStorageDeletionGraceHours]"));

        storage.Setup(client => client.GetVolumesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<IReadOnlyList<FileStorageVolumeStatus>>.Success([Volume()], HttpStatusCode.OK));
        page.Find("section.sth .sth-note button").Click();

        page.WaitForAssertion(() => Assert.Contains("Everything is working", page.Find("section.sth").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Refresh_is_never_disabled_so_focus_stays_on_it_and_a_click_in_flight_is_ignored()
    {
        await using var ctx = NewComponentContext();

        var panel = ctx.Render<StorageHealthPanel>(p => p
            .Add(x => x.Volumes, [Volume()])
            .Add(x => x.Refreshing, true)
            .Add(x => x.CheckedAt, DateTime.UtcNow));

        var refresh = panel.Find("button[aria-label='Refresh storage health']");
        Assert.False(refresh.HasAttribute("disabled"));
        Assert.Equal("true", refresh.GetAttribute("aria-disabled"));
    }

    [Fact]
    public async Task A_failed_refresh_keeps_the_data_and_says_how_old_it_is()
    {
        await using var ctx = NewPageContext([Volume()], out var storage);
        var page = ctx.Render<Settings>();
        storage.Setup(client => client.GetVolumesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<IReadOnlyList<FileStorageVolumeStatus>>.Failure(new HttpRequestException("down")));

        page.Find("button[aria-label='Refresh storage health']").Click();

        page.WaitForAssertion(() =>
        {
            var panel = page.Find("section.sth");
            Assert.Contains("Couldn’t refresh storage health.", panel.TextContent, StringComparison.Ordinal);
            Assert.Contains("Everything is working", panel.TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Retry_moves_focus_to_the_panel_once_the_result_is_in()
    {
        await using var ctx = NewPageContext(null, out var storage);
        var page = ctx.Render<Settings>();
        storage.Setup(client => client.GetVolumesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<IReadOnlyList<FileStorageVolumeStatus>>.Success([Volume()], HttpStatusCode.OK));

        page.Find("section.sth .sth-note button").Click();

        page.WaitForAssertion(() => Assert.Contains(ctx.JSInterop.Invocations,
            call => call.Identifier == "odsFocusById" && Equals(call.Arguments[0], StorageHealth.PanelId)));
    }

    [Fact]
    public async Task Used_space_is_clamped_to_zero_to_a_hundred()
    {
        await using var ctx = NewComponentContext();

        var panel = ctx.Render<StorageHealthPanel>(p => p.Add(x => x.Volumes, [Volume(v => { v.FreeBytes = 600; v.TotalBytes = 500; })]));

        Assert.Equal("primary: 0% used", panel.Find("[role=progressbar]").GetAttribute("aria-label"));
    }

    [Fact]
    public void The_grace_row_is_a_security_claim_hours_field_bounded_by_the_shared_pair()
    {
        var row = Settings.AllItems.Single(item => item.Key == "fileStorageDeletionGraceHours");

        Assert.Equal(Settings.SettingClaim.Security, row.Claim);
        Assert.Equal(Settings.SettingControl.Number, row.Control);
        Assert.Equal(SystemSettingsBounds.FileStorageDeletionGraceHoursMin, row.Min);
        Assert.Equal(SystemSettingsBounds.FileStorageDeletionGraceHoursMax, row.Max);
        Assert.Equal("hours", row.Unit);
        Assert.Equal("delete_sweep", row.Icon);
        Assert.Equal(nameof(SystemSettingsUpdate.FileStorageDeletionGraceHours), row.Field);
        Assert.Equal("Storage", Settings.Sections.Single(s => s.Items.Contains(row)).Group);
        Assert.Equal(Settings.StorageHealthPanelKey, Settings.Sections.Single(s => s.Items.Contains(row)).Panel);
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    private static BunitContext NewComponentContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    /// <param name="volumes">The endpoint's answer; null makes it fail.</param>
    private static BunitContext NewPageContext(
        IReadOnlyList<FileStorageVolumeStatus>? volumes, out Mock<IFileStorageApiClient> storage, IReadOnlyList<string>? claims = null)
    {
        var ctx = NewComponentContext();
        var settings = new Mock<ISystemSettingsApiClient>();
        settings.Setup(client => client.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<SystemSettingsDto>.Success(
                new SystemSettingsDto { EmailSmtpHost = "smtp.example.test", FileStorageDeletionGraceHours = 24 }, HttpStatusCode.OK));
        var secrets = new Mock<ISecretSettingsApiClient>();
        secrets.Setup(client => client.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<SecretSettingStatusDto>>.Success([], HttpStatusCode.OK));
        storage = new Mock<IFileStorageApiClient>();
        storage.Setup(client => client.GetVolumesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(volumes is null
                ? ApiResult<IReadOnlyList<FileStorageVolumeStatus>>.Failure(new HttpRequestException("down"))
                : ApiResult<IReadOnlyList<FileStorageVolumeStatus>>.Success(volumes, HttpStatusCode.OK));

        ctx.Services.AddSingleton(settings.Object);
        ctx.Services.AddSingleton(secrets.Object);
        ctx.Services.AddSingleton(ExchangeRateSyncFakes.Client().Object);
        ctx.Services.AddSingleton(storage.Object);
        ctx.Services.AddSingleton(new Mock<IDataExportApiClient>().Object);
        ctx.Services.AddSingleton(new Mock<IWeatherApiClient>().Object);
        ctx.Services.AddSingleton(new Mock<IPageStateService>().Object);
        ctx.Services.AddSingleton(new Mock<IImportLimitsCache>().Object);
        ctx.Services.AddSingleton(new Mock<IUploadLimitsCache>().Object);
        ctx.Services.AddSingleton(new Mock<IAccountLimitsCache>().Object);
        ctx.Services.AddSingleton(new Mock<IContractLimitsCache>().Object);
        ctx.Services.AddSingleton(new Mock<IPropertyLimitsCache>().Object);
        ctx.Services.AddSingleton(new Mock<IFileAnalysisDisclosureCache>().Object);
        ctx.Services.AddSingleton<AuthenticationStateProvider>(new StubAuthenticationStateProvider(claims ??
        [
            PermissionClaims.SystemSettingsRead,
            PermissionClaims.SystemSettingsUpdate,
            PermissionClaims.SystemSettingsSecurityUpdate,
            PermissionClaims.FileStorageRead,
        ]));

        Settings.InteractiveCheck = static () => true;
        return ctx;
    }

    private sealed class StubAuthenticationStateProvider(IReadOnlyList<string> permissions) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                permissions.Select(permission => new Claim(PermissionClaims.Type, permission)), "Test"))));
    }
}
