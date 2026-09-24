using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;
using static Odyssey.Api.Tests.PropertyApiTestSupport;

namespace Odyssey.Api.Tests;

/// <summary>
/// The property smart-tag endpoints and <c>/api/property-limits</c> over HTTP (issue #167, AC 15, 16,
/// 22): the add/remove status contract, the admin-editable cap and its eviction, the claim-free limits
/// read, and the tag-delete blocker.
/// </summary>
public class PropertySmartTagsApiTests
{
    private const string ActorUserId = "property-smart-tags-actor-id";

    private static readonly string[] ReadWrite = [PermissionClaims.PropertiesRead, PermissionClaims.PropertiesUpdate];

    // ── The read ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_APropertyWithNoSmartTags_ReturnsOkAndEmpty()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var propertyId = await SeedPropertyAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(SmartTagsPath(propertyId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await response.Content.ReadFromJsonAsync<List<ExistingTransactionTag>>())!);
    }

    [Fact]
    public async Task List_AMissingProperty_ReturnsNotFound()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await EnsureCreatedAsync(factory);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(SmartTagsPath(Guid.NewGuid()))).StatusCode);
    }

    // ── AC 15: the add ────────────────────────────────────────────────────────

    /// <summary>AC 15 — the first add is a 201 whose Location resolves to the list; a repeat is a 409 and leaves one row.</summary>
    [Fact]
    public async Task Post_TheSamePairTwice_ReturnsCreatedThenConflict()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var propertyId = await SeedPropertyAsync(factory);
        var tagId = await SeedTagAsync(factory, "Upkeep");
        using var client = factory.CreateClient();

        var first = await client.PostAsync(SmartTagPath(propertyId, tagId), null);
        var second = await client.PostAsync(SmartTagPath(propertyId, tagId), null);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(tagId, (await first.Content.ReadFromJsonAsync<ExistingTransactionTag>())!.TransactionTagId);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(first.Headers.Location)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(1, await CountAsync<PropertySmartTag>(factory));
    }

    /// <summary>AC 15 — an archived tag is retired vocabulary: 422, no row.</summary>
    [Fact]
    public async Task Post_AnArchivedTag_ReturnsUnprocessable_AndCreatesNoRow()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var propertyId = await SeedPropertyAsync(factory);
        var tagId = await SeedTagAsync(factory, "Retired", archived: true);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsync(SmartTagPath(propertyId, tagId), null)).StatusCode);
        Assert.Equal(0, await CountAsync<PropertySmartTag>(factory));
    }

    [Fact]
    public async Task Post_AMissingPropertyOrTag_ReturnsNotFound()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var propertyId = await SeedPropertyAsync(factory);
        var tagId = await SeedTagAsync(factory);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(SmartTagPath(Guid.NewGuid(), tagId), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(SmartTagPath(propertyId, Guid.NewGuid()), null)).StatusCode);
        Assert.Equal(0, await CountAsync<PropertySmartTag>(factory));
    }

    /// <summary>
    /// AC 15 — at the configured cap the add is a 422 whose detail names the EFFECTIVE number. The cap
    /// is deliberately not the shipped default, so a service reading a constant would fail here.
    /// </summary>
    [Fact]
    public async Task Post_AtTheConfiguredCap_ReturnsUnprocessable_AndNamesTheEffectiveCap()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await SystemSettingsSeed.SetAsync(factory.Services, SystemSettingsKeys.PropertyMaxSmartTagsPerProperty, "3");
        var propertyId = await SeedPropertyAsync(factory);
        using var client = factory.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            var tagId = await SeedTagAsync(factory, $"cap-{i}");
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsync(SmartTagPath(propertyId, tagId), null)).StatusCode);
        }

        var response = await client.PostAsync(SmartTagPath(propertyId, await SeedTagAsync(factory, "over-cap")), null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var detail = await ReadDetailAsync(response) ?? "";
        Assert.Contains("3", detail, StringComparison.Ordinal);
        Assert.DoesNotContain(SystemSettingsDefaults.PropertyMaxSmartTagsPerProperty.ToString(), detail, StringComparison.Ordinal);
        Assert.Equal(3, await CountAsync<PropertySmartTag>(factory));
    }

    /// <summary>
    /// AC 16 — lowering the cap through <c>PUT /api/system-settings</c> evicts the cached limit: the very
    /// next add is refused at the new number, and <c>/api/property-limits</c> serves it. The limits read
    /// happens FIRST, so the cache is warm with the old value when the save lands.
    /// </summary>
    [Fact]
    public async Task LoweringTheCap_ViaSystemSettings_IsEnforcedOnTheNextAdd_AndServedByTheLimitsEndpoint()
    {
        await using var factory = new ApiFactory(
            [.. ReadWrite, PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate]);
        var propertyId = await SeedPropertyAsync(factory);
        using var client = factory.CreateClient();

        var before = await client.GetFromJsonAsync<PropertyLimitsDto>(LimitsPath);
        Assert.Equal(SystemSettingsDefaults.PropertyMaxSmartTagsPerProperty, before!.MaxSmartTagsPerProperty);
        for (var i = 0; i < 2; i++)
        {
            var tagId = await SeedTagAsync(factory, $"pre-{i}");
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsync(SmartTagPath(propertyId, tagId), null)).StatusCode);
        }

        var save = await client.PutAsJsonAsync("/api/system-settings",
            new SystemSettingsUpdate { PropertyMaxSmartTagsPerProperty = 2 });
        Assert.True(save.StatusCode == HttpStatusCode.OK, await save.Content.ReadAsStringAsync());

        var response = await client.PostAsync(SmartTagPath(propertyId, await SeedTagAsync(factory, "third")), null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("2", await ReadDetailAsync(response) ?? "", StringComparison.Ordinal);

        var after = await client.GetFromJsonAsync<PropertyLimitsDto>(LimitsPath);
        Assert.Equal(2, after!.MaxSmartTagsPerProperty);
    }

    /// <summary>The setting's write bound is the filter-array ceiling, enforced by model validation.</summary>
    [Fact]
    public async Task PutSystemSettings_AboveTheFilterArrayCeiling_IsRejected()
    {
        await using var factory = new ApiFactory(
            [PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate]);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/system-settings",
            new SystemSettingsUpdate { PropertyMaxSmartTagsPerProperty = ListDefaults.MaxFilterArrayLength + 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/system-settings",
            new SystemSettingsUpdate { PropertyMaxSmartTagsPerProperty = ListDefaults.MaxFilterArrayLength })).StatusCode);
    }

    // ── The remove ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_AnExistingLink_ReturnsNoContent_AndLeavesTheTagAndProperty()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var propertyId = await SeedPropertyAsync(factory);
        var tagId = await SeedTagAsync(factory);
        await LinkSmartTagAsync(factory, propertyId, tagId);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(SmartTagPath(propertyId, tagId))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(SmartTagPath(propertyId, tagId))).StatusCode);

        Assert.Equal(0, await CountAsync<PropertySmartTag>(factory));
        Assert.Equal(1, await CountAsync<TransactionTag>(factory));
        Assert.Equal(1, await CountAsync<Property>(factory));
    }

    // ── AC 16: /api/property-limits ───────────────────────────────────────────

    [Fact]
    public async Task PropertyLimits_Unauthenticated_ReturnsUnauthorized()
    {
        await using var factory = new ApiFactory(permissions: null);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(LimitsPath)).StatusCode);
    }

    /// <summary>AC 16 — claim-free: a principal holding zero claims gets the effective cap (here, the compiled default for an absent row).</summary>
    [Fact]
    public async Task PropertyLimits_ForACallerWithNoClaims_ReturnsTheEffectiveCap()
    {
        await using var factory = new ApiFactory(permissions: []);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(LimitsPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<PropertyLimitsDto>();
        Assert.Equal(SystemSettingsDefaults.PropertyMaxSmartTagsPerProperty, dto!.MaxSmartTagsPerProperty);
    }

    [Fact]
    public async Task PropertyLimits_WithAConfiguredValue_ReturnsIt()
    {
        await using var factory = new ApiFactory(permissions: []);
        await SystemSettingsSeed.SetAsync(factory.Services, SystemSettingsKeys.PropertyMaxSmartTagsPerProperty, "7");
        using var client = factory.CreateClient();

        Assert.Equal(7, (await client.GetFromJsonAsync<PropertyLimitsDto>(LimitsPath))!.MaxSmartTagsPerProperty);
    }

    /// <summary>An unusable stored value is degraded: the display surface fails closed with 503.</summary>
    [Fact]
    public async Task PropertyLimits_WithAnUnusableStoredValue_Returns503()
    {
        await using var factory = new ApiFactory(permissions: []);
        await SystemSettingsSeed.SetAsync(factory.Services, SystemSettingsKeys.PropertyMaxSmartTagsPerProperty, "not-a-number");
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync(LimitsPath)).StatusCode);
    }

    /// <summary>A degraded read never loosens the cap: adds are still refused at the shipped default.</summary>
    [Fact]
    public async Task Post_WithADegradedLimitsRead_IsStillRefusedAtTheConservativeCap()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await SystemSettingsSeed.SetAsync(factory.Services, SystemSettingsKeys.PropertyMaxSmartTagsPerProperty, "not-a-number");
        var propertyId = await SeedPropertyAsync(factory);
        for (var i = 0; i < SystemSettingsDefaults.PropertyMaxSmartTagsPerProperty; i++)
            await LinkSmartTagAsync(factory, propertyId, await SeedTagAsync(factory, $"degraded-{i}"));
        using var client = factory.CreateClient();

        var response = await client.PostAsync(SmartTagPath(propertyId, await SeedTagAsync(factory, "over-cap")), null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // ── AC 22: the tag-delete blocker ─────────────────────────────────────────

    /// <summary>
    /// AC 22 — the EF InMemory tier enforces no foreign keys, so this is the service's pre-check. The
    /// message names the COUNT and never the property.
    /// </summary>
    [Fact]
    public async Task DeletingATagThatIsAPropertySmartTag_ReturnsConflictNamingTheCountNotTheProperty()
    {
        await using var factory = new ApiFactory([.. ReadWrite, PermissionClaims.TransactionTagsDelete]);
        var propertyId = await SeedPropertyAsync(factory, "Fjellveien cabin");
        var tagId = await SeedTagAsync(factory, "Upkeep");
        await LinkSmartTagAsync(factory, propertyId, tagId);
        using var client = factory.CreateClient();

        var response = await client.DeleteAsync($"/api/transaction-tags/{tagId}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("1 property", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Fjellveien cabin", body, StringComparison.Ordinal);
        Assert.Equal(1, await CountAsync<TransactionTag>(factory));
        Assert.Equal(1, await CountAsync<PropertySmartTag>(factory));
    }

    [Fact]
    public async Task DeletingATagThatIsASmartTagOnTwoProperties_PluralisesTheCount()
    {
        await using var factory = new ApiFactory([.. ReadWrite, PermissionClaims.TransactionTagsDelete]);
        var first = await SeedPropertyAsync(factory, "First");
        var second = await SeedPropertyAsync(factory, "Second");
        var tagId = await SeedTagAsync(factory);
        await LinkSmartTagAsync(factory, first, tagId);
        await LinkSmartTagAsync(factory, second, tagId);
        using var client = factory.CreateClient();

        var response = await client.DeleteAsync($"/api/transaction-tags/{tagId}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("2 properties", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeletingATagAfterItsLinkIsRemoved_Succeeds()
    {
        await using var factory = new ApiFactory([.. ReadWrite, PermissionClaims.TransactionTagsDelete]);
        var propertyId = await SeedPropertyAsync(factory);
        var tagId = await SeedTagAsync(factory);
        await LinkSmartTagAsync(factory, propertyId, tagId);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(SmartTagPath(propertyId, tagId))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/transaction-tags/{tagId}")).StatusCode);
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);
}
