using System.Globalization;
using System.Net;
using Bunit;
using Moq;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Xunit;
using static Odyssey.Client.Tests.SettingsPageHarness;

namespace Odyssey.Client.Tests;

/// <summary>
/// System settings → News (issue #473 §3.6, §5.4, AC 23; issue #508 §8.5, AC 35): eleven rows, ten without the security
/// claim, every bound from the API, the image size bounded by the published ceiling, and the allow-list's
/// confirm-before-save. The two OPML import rows follow the import caps: the entry cap on the ordinary claim, the size
/// cap on the security one (amendment v5).
/// </summary>
[Collection(SettingsPageCollection.Name)]
public class NewsSettingsRowsTests : IDisposable
{
    private static readonly string[] NewsKeys =
    [
        "newsDefaultFetchIntervalMinutes", "newsMaxFeeds", "newsImageMaxPerArticle", "newsImageMaxSizeKb",
        "newsImageStoreMaxGiB", "newsTextStoreMaxGiB", "newsFeedMaxArticlesPerDay", "newsFeedMaxImageMbPerDay",
        "newsOpmlMaxImportEntries", "newsOpmlMaxImportMegabytes", Settings.NewsAllowListKey,
    ];

    public void Dispose() => Settings.InteractiveCheck = static () => OperatingSystem.IsBrowser();

    private static IReadOnlyList<string> Rendered(IRenderedComponent<Settings> page) =>
        [.. NewsKeys.Where(key => page.FindAll($"#ss-ttl-{key}").Count == 1)];

    [Fact]
    public void The_news_group_declares_eleven_rows_with_the_rss_icon()
    {
        var section = Settings.Sections.Single(s => s.Group == "News");

        Assert.Equal(MudBlazor.Icons.Material.Filled.RssFeed, section.Icon);
        Assert.Equal(NewsKeys, section.Items.Select(i => i.Key));
        string[] security = [Settings.NewsAllowListKey, "newsOpmlMaxImportMegabytes"];
        Assert.All(section.Items.Where(i => security.Contains(i.Key)), item => Assert.Equal(Settings.SettingClaim.Security, item.Claim));
        Assert.All(section.Items.Where(i => !security.Contains(i.Key)),
            item => Assert.Equal(Settings.SettingClaim.Count, item.Claim));
    }

    [Fact]
    public async Task An_administrator_sees_eleven_rows()
    {
        await using var ctx = NewPageContext(SettingsClient(Valid()).Object);

        var page = ctx.Render<Settings>();

        Assert.Equal(NewsKeys, Rendered(page));
    }

    [Fact]
    public async Task Without_the_security_claim_the_allow_list_row_is_not_rendered()
    {
        await using var ctx = NewPageContext(SettingsClient(Valid()).Object,
            claims: [PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate]);

        var page = ctx.Render<Settings>();

        Assert.Equal(NewsKeys[..10], Rendered(page));
        Assert.DoesNotContain(Settings.NewsAllowListAdvisory, page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_image_size_maximum_is_the_lower_of_the_range_and_the_published_ceiling()
    {
        var dto = Valid();
        dto.NewsImageMaxSizeKbCeiling = 2048;
        await using var ctx = NewPageContext(SettingsClient(dto).Object);

        var page = ctx.Render<Settings>();

        Assert.Equal("2048", page.Find("#ss-in-newsImageMaxSizeKb").GetAttribute("max"));
        Assert.Contains($"At most {2048.ToString("N0", CultureInfo.CurrentCulture)} KB", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(Settings.MaxToken, page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_ceiling_above_the_range_does_not_widen_it()
    {
        var dto = Valid();
        dto.NewsImageMaxSizeKbCeiling = SystemSettingsBounds.NewsImageMaxSizeKbMax * 4;
        await using var ctx = NewPageContext(SettingsClient(dto).Object);

        var page = ctx.Render<Settings>();

        Assert.Equal(
            SystemSettingsBounds.NewsImageMaxSizeKbMax.ToString(CultureInfo.InvariantCulture),
            page.Find("#ss-in-newsImageMaxSizeKb").GetAttribute("max"));
    }

    [Fact]
    public async Task The_allow_list_row_carries_its_standing_advisory()
    {
        await using var ctx = NewPageContext(SettingsClient(Valid()).Object);

        var page = ctx.Render<Settings>();

        Assert.Contains(Settings.NewsAllowListAdvisory, page.Find($"#ss-advisory-{Settings.NewsAllowListKey}").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Saving_a_changed_allow_list_confirms_first_and_sends_nothing_until_confirmed()
    {
        var settings = SettingsClient(Valid());
        SystemSettingsUpdate? sent = null;
        settings.Setup(client => client.UpdateAsync(It.IsAny<SystemSettingsUpdate>(), It.IsAny<CancellationToken>()))
            .Callback((SystemSettingsUpdate update, CancellationToken _) => sent = update)
            .ReturnsAsync(ApiResult<SystemSettingsDto>.Success(Valid(), HttpStatusCode.OK));
        await using var ctx = NewPageContext(settings.Object);
        var page = ctx.Render<Settings>();

        page.Find($"#ss-in-{Settings.NewsAllowListKey}").Input("nas.lan, 192.168.1.20:8080");
        page.FindAll("button").Single(b => b.TextContent.Contains("Save changes", StringComparison.Ordinal)).Click();

        var dialog = AllowListDialog(page);
        Assert.True(dialog.Instance.Open);
        settings.Verify(client => client.UpdateAsync(It.IsAny<SystemSettingsUpdate>(), It.IsAny<CancellationToken>()), Times.Never);

        await page.InvokeAsync(() => dialog.Instance.OnSubmit!());

        page.WaitForAssertion(() => Assert.NotNull(sent));
        Assert.Equal("nas.lan, 192.168.1.20:8080", sent!.NewsPrivateHostAllowList);
        settings.Verify(client => client.UpdateAsync(It.IsAny<SystemSettingsUpdate>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Cancelling_the_confirmation_keeps_the_edit_and_sends_nothing()
    {
        var settings = SettingsClient(Valid());
        await using var ctx = NewPageContext(settings.Object);
        var page = ctx.Render<Settings>();

        page.Find($"#ss-in-{Settings.NewsAllowListKey}").Input("nas.lan");
        page.FindAll("button").Single(b => b.TextContent.Contains("Save changes", StringComparison.Ordinal)).Click();
        var dialog = AllowListDialog(page);
        await page.InvokeAsync(() => dialog.Instance.OpenChanged.InvokeAsync(false));

        Assert.False(AllowListDialog(page).Instance.Open);
        Assert.Equal("nas.lan", page.Find($"#ss-in-{Settings.NewsAllowListKey}").GetAttribute("value"));
        settings.Verify(client => client.UpdateAsync(It.IsAny<SystemSettingsUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_save_that_leaves_the_allow_list_alone_is_not_gated()
    {
        var settings = SettingsClient(Valid());
        settings.Setup(client => client.UpdateAsync(It.IsAny<SystemSettingsUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<SystemSettingsDto>.Success(Valid(), HttpStatusCode.OK));
        await using var ctx = NewPageContext(settings.Object);
        var page = ctx.Render<Settings>();

        page.Find("#ss-in-newsMaxFeeds").Input("150");
        page.FindAll("button").Single(b => b.TextContent.Contains("Save changes", StringComparison.Ordinal)).Click();

        Assert.False(AllowListDialog(page).Instance.Open);
        page.WaitForAssertion(() => settings.Verify(
            client => client.UpdateAsync(It.IsAny<SystemSettingsUpdate>(), It.IsAny<CancellationToken>()), Times.Once));
    }

    [Fact]
    public void The_diff_lists_added_and_removed_grants_in_the_stored_spelling()
    {
        var change = Settings.DiffAllowList("nas.lan, old.lan", "NAS.lan, 192.168.1.20:8080");

        Assert.NotNull(change);
        Assert.Equal(["192.168.1.20:8080"], change!.Added);
        Assert.Equal(["old.lan"], change.Removed);
        Assert.Equal("The worker will be allowed to fetch from: 192.168.1.20:8080.", Settings.AllowListAddedSentence(change));
        Assert.Equal("The worker will no longer be allowed to fetch from: old.lan.", Settings.AllowListRemovedSentence(change));
    }

    [Fact]
    public void A_spelling_only_change_is_no_change() =>
        Assert.Null(Settings.DiffAllowList("nas.lan", " NAS.LAN "));

    private static IRenderedComponent<OdsFormDialog> AllowListDialog(IRenderedComponent<Settings> page) =>
        page.FindComponents<OdsFormDialog>().Single(d => d.Instance.Title == "Change the private hosts news may reach?");

    /// <summary>A DTO every row accepts as loaded (the EmailTransportSurfaceTests helper's shape).</summary>
    private static SystemSettingsDto Valid()
    {
        var dto = new SystemSettingsDto();
        var properties = typeof(SystemSettingsDto).GetProperties().ToDictionary(p => p.Name, StringComparer.Ordinal);
        foreach (var property in properties.Values.Where(p => p.PropertyType == typeof(int)))
        {
            if (property.Name.EndsWith("Ceiling", StringComparison.Ordinal))
                property.SetValue(dto, 1_000_000);
            else if (property.Name.EndsWith("Floor", StringComparison.Ordinal))
                property.SetValue(dto, 1);
        }

        foreach (var item in Settings.AllItems)
        {
            if (item.Field is not { } field || !properties.TryGetValue(field, out var property))
                continue;
            if (property.PropertyType == typeof(int))
                property.SetValue(dto, Math.Max(item.Min, 1));
            else if (property.PropertyType == typeof(decimal))
                property.SetValue(dto, item.DecimalMin);
            else if (property.PropertyType == typeof(string) && !item.AllowEmpty)
                property.SetValue(dto, "x");
        }

        dto.EmailSmtpHost = "smtp.old.test";
        dto.EmailSmtpPort = 587;
        dto.EmailUseStartTls = true;
        return dto;
    }
}
