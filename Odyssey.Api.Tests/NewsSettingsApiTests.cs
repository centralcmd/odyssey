using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The nine news-reader settings over HTTP (issue #470 §11.1): the shipped defaults, the derived ceiling of the image
/// size cap (AC 56, 61, 63), the claim split that puts the private-host allow-list on the security claim, and the
/// allow-list's shape rule. The guard tests (registry, bounds, seed) cover the recipe itself.
/// </summary>
public class NewsSettingsApiTests
{
    private const string Path = "/api/system-settings";
    private const string ActorUserId = "47047047-0470-4470-8470-470470470470";

    private static readonly string[] ReadAndOrdinary = [PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate];

    private static readonly string[] ReadAndSecurity = [PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsSecurityUpdate];

    [Fact]
    public async Task Get_serves_the_shipped_news_defaults()
    {
        await using var factory = new ApiFactory(ReadAndOrdinary);
        using var client = factory.CreateClient();

        var dto = (await client.GetFromJsonAsync<SystemSettingsDto>(Path))!;

        Assert.Equal(
            (60, 200, 5, 5120, 50, 20, 500, 200, ""),
            (dto.NewsDefaultFetchIntervalMinutes, dto.NewsMaxFeeds, dto.NewsImageMaxPerArticle, dto.NewsImageMaxSizeKb,
             dto.NewsImageStoreMaxGiB, dto.NewsTextStoreMaxGiB, dto.NewsFeedMaxArticlesPerDay, dto.NewsFeedMaxImageMbPerDay,
             dto.NewsPrivateHostAllowList));
    }

    /// <summary>AC 61: the ceiling is <c>MaxFileSizeBytes / 1024</c> — 64 MiB by default, so 65,536 KiB.</summary>
    [Fact]
    public async Task Get_publishes_the_image_size_ceiling_from_the_file_size_limit()
    {
        await using var factory = new ApiFactory(ReadAndOrdinary);
        using var client = factory.CreateClient();

        var dto = (await client.GetFromJsonAsync<SystemSettingsDto>(Path))!;

        Assert.Equal(Odyssey.Core.Internet.News.NewsImageSizeCeiling.Kb(new Odyssey.Core.Finance.FileStorageOptions().MaxFileSizeBytes),
            dto.NewsImageMaxSizeKbCeiling);
    }

    /// <summary>AC 56: above the ceiling is refused on PUT and the message names it; at the ceiling is accepted.</summary>
    [Fact]
    public async Task Put_above_the_derived_ceiling_is_refused_and_at_it_is_accepted()
    {
        await using var factory = new ApiFactory(ReadAndOrdinary, maxFileSizeBytes: 1024 * 1024);
        using var client = factory.CreateClient();

        var refused = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate { NewsImageMaxSizeKb = 1025 });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var body = await refused.Content.ReadAsStringAsync();
        Assert.Contains(nameof(SystemSettingsUpdate.NewsImageMaxSizeKb), body, StringComparison.Ordinal);
        Assert.Contains("1024", body, StringComparison.Ordinal);

        var accepted = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate { NewsImageMaxSizeKb = 1024 });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var dto = (await accepted.Content.ReadFromJsonAsync<SystemSettingsDto>())!;
        Assert.Equal(1024, dto.NewsImageMaxSizeKb);
        Assert.Equal(1024, dto.NewsImageMaxSizeKbCeiling);
    }

    /// <summary>AC 63: with a file limit below 64 KiB the ceiling is the setting's floor, so a value can still be saved.</summary>
    [Fact]
    public async Task A_file_limit_below_64_KiB_still_leaves_the_floor_settable()
    {
        await using var factory = new ApiFactory(ReadAndOrdinary, maxFileSizeBytes: 16 * 1024);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate { NewsImageMaxSizeKb = SystemSettingsBounds.NewsImageMaxSizeKbMin });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(SystemSettingsBounds.NewsImageMaxSizeKbMin, (await response.Content.ReadFromJsonAsync<SystemSettingsDto>())!.NewsImageMaxSizeKbCeiling);
    }

    [Theory]
    [InlineData(nameof(SystemSettingsUpdate.NewsDefaultFetchIntervalMinutes), 14)]
    [InlineData(nameof(SystemSettingsUpdate.NewsDefaultFetchIntervalMinutes), 1441)]
    [InlineData(nameof(SystemSettingsUpdate.NewsMaxFeeds), 1001)]
    [InlineData(nameof(SystemSettingsUpdate.NewsImageMaxPerArticle), 21)]
    [InlineData(nameof(SystemSettingsUpdate.NewsImageMaxSizeKb), 63)]
    [InlineData(nameof(SystemSettingsUpdate.NewsImageStoreMaxGiB), 0)]
    [InlineData(nameof(SystemSettingsUpdate.NewsTextStoreMaxGiB), 1001)]
    [InlineData(nameof(SystemSettingsUpdate.NewsFeedMaxArticlesPerDay), 9)]
    [InlineData(nameof(SystemSettingsUpdate.NewsFeedMaxImageMbPerDay), 10241)]
    public async Task Put_outside_a_static_range_is_a_400(string field, int value)
    {
        await using var factory = new ApiFactory(ReadAndOrdinary);
        using var client = factory.CreateClient();

        var response = await client.PutAsync(Path, JsonContent.Create(new Dictionary<string, int> { [field] = value }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>§11.1: the eight ordinary keys take <c>system-settings.update</c>.</summary>
    [Fact]
    public async Task The_ordinary_claim_writes_the_eight_ordinary_news_keys()
    {
        await using var factory = new ApiFactory(ReadAndOrdinary);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate
        {
            NewsDefaultFetchIntervalMinutes = 120, NewsMaxFeeds = 300, NewsImageMaxPerArticle = 0, NewsImageMaxSizeKb = 2048,
            NewsImageStoreMaxGiB = 10, NewsTextStoreMaxGiB = 5, NewsFeedMaxArticlesPerDay = 50, NewsFeedMaxImageMbPerDay = 20,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = (await response.Content.ReadFromJsonAsync<SystemSettingsDto>())!;
        Assert.Equal((120, 300, 0, 2048, 10, 5, 50, 20),
            (dto.NewsDefaultFetchIntervalMinutes, dto.NewsMaxFeeds, dto.NewsImageMaxPerArticle, dto.NewsImageMaxSizeKb,
             dto.NewsImageStoreMaxGiB, dto.NewsTextStoreMaxGiB, dto.NewsFeedMaxArticlesPerDay, dto.NewsFeedMaxImageMbPerDay));
    }

    /// <summary>§7.9, §11.1: the allow-list decides which private addresses the worker may fetch, so it needs the security claim.</summary>
    [Fact]
    public async Task The_allow_list_needs_the_security_claim_and_writes_nothing_without_it()
    {
        await using var factory = new ApiFactory(ReadAndOrdinary);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate { NewsPrivateHostAllowList = "nas.lan" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(nameof(SystemSettingsUpdate.NewsPrivateHostAllowList), await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await using var scope = factory.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<OdysseyContext>().SystemSettings.AsNoTracking()
            .SingleAsync(s => s.Key == SystemSettingsKeys.NewsPrivateHostAllowList);
        Assert.Equal("", stored.Value);
    }

    [Fact]
    public async Task The_allow_list_is_stored_canonical_and_can_be_emptied_again()
    {
        await using var factory = new ApiFactory(ReadAndSecurity);
        using var client = factory.CreateClient();

        var saved = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate { NewsPrivateHostAllowList = " NAS.lan, 192.168.1.20:8080 " });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("nas.lan, 192.168.1.20:8080", (await saved.Content.ReadFromJsonAsync<SystemSettingsDto>())!.NewsPrivateHostAllowList);

        var emptied = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate { NewsPrivateHostAllowList = "" });
        Assert.Equal(HttpStatusCode.OK, emptied.StatusCode);
        Assert.Equal("", (await emptied.Content.ReadFromJsonAsync<SystemSettingsDto>())!.NewsPrivateHostAllowList);
    }

    [Theory]
    [InlineData("http://nas.lan")]
    [InlineData("nas.lan/feed")]
    [InlineData("user@nas.lan")]
    [InlineData("nas.lan:99999")]
    public async Task A_malformed_allow_list_is_a_400_keyed_on_the_field(string value)
    {
        await using var factory = new ApiFactory(ReadAndSecurity);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(Path, new SystemSettingsUpdate { NewsPrivateHostAllowList = value });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(nameof(SystemSettingsUpdate.NewsPrivateHostAllowList), await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>AC 78 on the fast tier: the totals row is part of the seed, so a fresh store already has it.</summary>
    [Fact]
    public async Task A_fresh_store_holds_the_single_news_totals_row()
    {
        await using var factory = new ApiFactory(ReadAndOrdinary);
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var row = await context.NewsStorageTotals.AsNoTracking().SingleAsync();
        Assert.Equal((NewsStorageTotal.SingletonId, 0L, 0L), (row.Id, row.ImageBytes, row.TextAndSourceBytes));
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions, long? maxFileSizeBytes = null)
        : OdysseyApiFactory(permissions, ActorUserId, maxFileSizeBytes is { } bytes
            ? new Dictionary<string, string?> { ["FileStorage:MaxFileSizeBytes"] = bytes.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            : null);
}
