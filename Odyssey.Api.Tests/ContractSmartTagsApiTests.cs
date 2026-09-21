using System.Net;
using System.Net.Http.Json;
using Odyssey.Context;
using Odyssey.Context.Authorization;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Odyssey.Api.Tests.Infrastructure;

namespace Odyssey.Api.Tests;

/// <summary>
/// The contract smart-tag endpoints over HTTP (issue #166): the authorization matrix, the status-code
/// contract, the cap, the list count and the claim-free limits endpoint.
/// </summary>
public class ContractSmartTagsApiTests
{
    private const string ActorUserId = "contract-smart-tags-actor-id";
    private const string ContractsPath = "/api/contracts";
    private const string LimitsPath = "/api/contract-limits";

    private static string SmartTagsPath(Guid contractId) => $"{ContractsPath}/{contractId}/smart-tags";

    private static string SmartTagPath(Guid contractId, Guid tagId) =>
        $"{SmartTagsPath(contractId)}/{tagId}";

    private static readonly string[] ReadOnly = [PermissionClaims.ContractsRead];

    private static readonly string[] ReadWrite =
        [PermissionClaims.ContractsRead, PermissionClaims.ContractsUpdate];

    // ── AC 10: authorization, per endpoint ────────────────────────────────────

    [Fact]
    public async Task List_Unauthenticated_ReturnsUnauthorized()
    {
        await using var factory = await NewFactoryAsync(permissions: null);
        using var client = factory.CreateClient();

        Assert.Equal(
            HttpStatusCode.Unauthorized, (await client.GetAsync(SmartTagsPath(Guid.NewGuid()))).StatusCode);
    }

    [Fact]
    public async Task List_WithoutContractsRead_ReturnsForbidden()
    {
        await using var factory = await NewFactoryAsync(permissions: []);
        using var client = factory.CreateClient();

        Assert.Equal(
            HttpStatusCode.Forbidden, (await client.GetAsync(SmartTagsPath(Guid.NewGuid()))).StatusCode);
    }

    /// <summary>
    /// Every claim in the vocabulary EXCEPT the two these endpoints gate on — so a principal that may
    /// create and destroy whole contracts is shown to have no reach into one's smart tags.
    /// </summary>
    [Fact]
    public async Task EveryEndpoint_WithEveryOtherClaim_ReturnsForbidden()
    {
        string[] everythingElse =
            [.. RolePermissions.AllClaims.Where(c =>
                c is not (PermissionClaims.ContractsRead or PermissionClaims.ContractsUpdate))];

        await using var factory = await NewFactoryAsync(everythingElse);
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(SmartTagsPath(id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(SmartTagPath(id, id), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync(SmartTagPath(id, id))).StatusCode);
    }

    [Fact]
    public async Task Writes_WithReadOnlyPermission_ReturnForbidden_WhileTheReadSucceeds()
    {
        await using var factory = await NewFactoryAsync(ReadOnly);
        var contractId = await SeedContractDirectlyAsync(factory);
        var tagId = await SeedTagAsync(factory);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(SmartTagsPath(contractId))).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden, (await client.PostAsync(SmartTagPath(contractId, tagId), null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden, (await client.DeleteAsync(SmartTagPath(contractId, tagId))).StatusCode);
    }

    // ── AC 1-3: the read ──────────────────────────────────────────────────────

    /// <summary>AC 2 — an empty list is a healthy 200, never a 404.</summary>
    [Fact]
    public async Task List_AContractWithNoSmartTags_ReturnsOkAndEmpty()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        var contractId = await SeedContractDirectlyAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(SmartTagsPath(contractId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await response.Content.ReadFromJsonAsync<List<ExistingTransactionTag>>())!);
    }

    [Fact]
    public async Task List_AMissingContract_ReturnsNotFound()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();

        Assert.Equal(
            HttpStatusCode.NotFound, (await client.GetAsync(SmartTagsPath(Guid.NewGuid()))).StatusCode);
    }

    /// <summary>
    /// AC 1 — ordered by <c>AddedAt</c> ascending. The three links are stamped in the reverse of the
    /// order they are inserted, so an implementation ordering by anything else fails here.
    /// </summary>
    [Fact]
    public async Task List_OrdersByAddedAtAscending_NotByInsertionOrder()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        var contractId = await SeedContractDirectlyAsync(factory);
        using var client = factory.CreateClient();

        var third = await SeedTagAsync(factory, "third");
        var second = await SeedTagAsync(factory, "second");
        var first = await SeedTagAsync(factory, "first");
        foreach (var tagId in new[] { third, second, first })
        {
            Assert.Equal(
                HttpStatusCode.Created, (await client.PostAsync(SmartTagPath(contractId, tagId), null)).StatusCode);
        }

        await StampAsync(factory, contractId, third, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc));
        await StampAsync(factory, contractId, second, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        await StampAsync(factory, contractId, first, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var tags = await client.GetFromJsonAsync<List<ExistingTransactionTag>>(SmartTagsPath(contractId));

        Assert.Equal(["first", "second", "third"], tags!.Select(t => t.Name));
    }

    // ── AC 4-7: the add ───────────────────────────────────────────────────────

    [Fact]
    public async Task Post_AValidTag_ReturnsCreatedWithTheTagAndALocationResolvingToTheList()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        var contractId = await SeedContractDirectlyAsync(factory);
        var tagId = await SeedTagAsync(factory, "Streaming");
        using var client = factory.CreateClient();

        var response = await client.PostAsync(SmartTagPath(contractId, tagId), null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ExistingTransactionTag>();
        Assert.Equal(tagId, created!.TransactionTagId);
        Assert.Equal("Streaming", created.Name);

        // The Location header must resolve to endpoint 1, not to a link-row route that does not exist.
        var location = response.Headers.Location!.ToString();
        Assert.EndsWith(SmartTagsPath(contractId), location, StringComparison.OrdinalIgnoreCase);
        var followed = await client.GetAsync(location);
        Assert.Equal(HttpStatusCode.OK, followed.StatusCode);
    }

    [Fact]
    public async Task Post_ARepeatOfTheSamePair_ReturnsConflict_AndLeavesOneRow()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        var contractId = await SeedContractDirectlyAsync(factory);
        var tagId = await SeedTagAsync(factory);
        using var client = factory.CreateClient();

        Assert.Equal(
            HttpStatusCode.Created, (await client.PostAsync(SmartTagPath(contractId, tagId), null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Conflict, (await client.PostAsync(SmartTagPath(contractId, tagId), null)).StatusCode);

        Assert.Equal(1, await CountLinksAsync(factory));
    }

    [Fact]
    public async Task Post_AnArchivedTag_ReturnsUnprocessableEntity_AndCreatesNoRow()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        var contractId = await SeedContractDirectlyAsync(factory);
        var tagId = await SeedTagAsync(factory, "Retired", archived: true);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(SmartTagPath(contractId, tagId), null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, await CountLinksAsync(factory));
    }

    [Fact]
    public async Task Post_AMissingContract_ReturnsNotFound()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        var tagId = await SeedTagAsync(factory);
        using var client = factory.CreateClient();

        Assert.Equal(
            HttpStatusCode.NotFound, (await client.PostAsync(SmartTagPath(Guid.NewGuid(), tagId), null)).StatusCode);
    }

    [Fact]
    public async Task Post_AMissingTag_ReturnsNotFound()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        var contractId = await SeedContractDirectlyAsync(factory);
        using var client = factory.CreateClient();

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PostAsync(SmartTagPath(contractId, Guid.NewGuid()), null)).StatusCode);
    }

    /// <summary>
    /// AC 7 — the SERVER is the control, and the message names the EFFECTIVE number. The cap under
    /// test is deliberately not the shipped 20: a test that only passed at the default would pass
    /// equally well against a service that had gone back to reading a constant.
    /// </summary>
    [Fact]
    public async Task Post_OverTheConfiguredCap_ReturnsUnprocessable_AndNamesTheEffectiveCap()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        await SystemSettingsSeed.SetAsync(
            factory.Services, SystemSettingsKeys.ContractMaxSmartTagsPerContract, "3");
        var contractId = await SeedContractDirectlyAsync(factory);
        using var client = factory.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            var tagId = await SeedTagAsync(factory, $"cap-{i}");
            Assert.Equal(
                HttpStatusCode.Created, (await client.PostAsync(SmartTagPath(contractId, tagId), null)).StatusCode);
        }

        var overCap = await SeedTagAsync(factory, "over-cap");
        var response = await client.PostAsync(SmartTagPath(contractId, overCap), null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("3", body, StringComparison.Ordinal);
        Assert.DoesNotContain("20", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// AC 12 — mass assignment. Neither write binds a body, so a populated one naming a different
    /// contract or tag, or nesting a renamed tag, changes nothing: the link created is the one the
    /// ROUTE names, and no <c>TransactionTag</c> row is mutated.
    /// </summary>
    [Fact]
    public async Task Post_WithAPopulatedBody_HonoursTheRouteOnly_AndMutatesNoTag()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        var contractId = await SeedContractDirectlyAsync(factory);
        var otherContractId = await SeedContractDirectlyAsync(factory, "Other contract");
        var tagId = await SeedTagAsync(factory, "Streaming");
        var otherTagId = await SeedTagAsync(factory, "Utilities");
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(SmartTagPath(contractId, tagId), new
        {
            contractId = otherContractId,
            tagId = otherTagId,
            transactionTagId = otherTagId,
            transactionTag = new { transactionTagId = tagId, name = "Renamed", description = "injected" },
            addedAt = new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var link = await context.ContractSmartTags.AsNoTracking().SingleAsync();
        Assert.Equal(contractId, link.ContractId);
        Assert.Equal(tagId, link.TransactionTagId);
        Assert.NotEqual(new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc), link.AddedAt);
        Assert.Equal("Streaming", (await context.TransactionTags.AsNoTracking().SingleAsync(t => t.TransactionTagId == tagId)).Name);
        Assert.Equal("Utilities", (await context.TransactionTags.AsNoTracking().SingleAsync(t => t.TransactionTagId == otherTagId)).Name);
    }

    // ── AC 8-9: the remove ────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_AnExistingLink_ReturnsNoContent_AndLeavesTheTagAndContractIntact()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        var contractId = await SeedContractDirectlyAsync(factory);
        var tagId = await SeedTagAsync(factory);
        using var client = factory.CreateClient();
        await client.PostAsync(SmartTagPath(contractId, tagId), null);

        Assert.Equal(
            HttpStatusCode.NoContent, (await client.DeleteAsync(SmartTagPath(contractId, tagId))).StatusCode);

        Assert.Equal(0, await CountLinksAsync(factory));
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.True(await context.TransactionTags.AnyAsync(t => t.TransactionTagId == tagId));
        Assert.True(await context.Contracts.AnyAsync(c => c.ContractId == contractId));
    }

    [Fact]
    public async Task Delete_APairThatIsNotLinked_ReturnsNotFound()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        var contractId = await SeedContractDirectlyAsync(factory);
        var tagId = await SeedTagAsync(factory);
        using var client = factory.CreateClient();

        Assert.Equal(
            HttpStatusCode.NotFound, (await client.DeleteAsync(SmartTagPath(contractId, tagId))).StatusCode);
    }

    // ── AC 11: the contract-delete cascade, on the InMemory tier ──────────────

    /// <summary>
    /// The EF InMemory provider enforces no foreign keys at all, so this proves the
    /// <c>.Include(c =&gt; c.SmartTags)</c> in <c>ContractService.Delete</c> rather than the database
    /// <c>CASCADE</c> — the relational half is <c>Odyssey.IntegrationTests</c>'s.
    /// </summary>
    [Fact]
    public async Task DeletingAContract_RemovesItsLinks_AndLeavesTheTagsIntact()
    {
        await using var factory = await NewFactoryAsync(
            [.. ReadWrite, PermissionClaims.ContractsDelete]);
        var contractId = await SeedContractDirectlyAsync(factory);
        var tagId = await SeedTagAsync(factory);
        using var client = factory.CreateClient();
        await client.PostAsync(SmartTagPath(contractId, tagId), null);

        Assert.Equal(
            HttpStatusCode.NoContent, (await client.DeleteAsync($"{ContractsPath}/{contractId}")).StatusCode);

        Assert.Equal(0, await CountLinksAsync(factory));
        using var scope = factory.Services.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<OdysseyContext>()
            .TransactionTags.AnyAsync(t => t.TransactionTagId == tagId));
    }

    // ── AC 13: the list count ─────────────────────────────────────────────────

    [Fact]
    public async Task List_CarriesTheSmartTagCount_Correlated()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        var watched = await SeedContractDirectlyAsync(factory, "Watched");
        var unwatched = await SeedContractDirectlyAsync(factory, "Unwatched");
        using var client = factory.CreateClient();

        var before = (await client.GetFromJsonAsync<PagedResult<ContractListItem>>(ContractsPath))!;
        Assert.Equal(0, before.Items.Single(c => c.ContractId == watched).SmartTagCount);

        await client.PostAsync(SmartTagPath(watched, await SeedTagAsync(factory, "one")), null);
        await client.PostAsync(SmartTagPath(watched, await SeedTagAsync(factory, "two")), null);

        var after = (await client.GetFromJsonAsync<PagedResult<ContractListItem>>(ContractsPath))!;
        Assert.Equal(2, after.Items.Single(c => c.ContractId == watched).SmartTagCount);
        // The subquery is correlated, not a table-wide count leaking across rows.
        Assert.Equal(0, after.Items.Single(c => c.ContractId == unwatched).SmartTagCount);
    }

    // ── AC 14-15: /api/contract-limits ────────────────────────────────────────

    [Fact]
    public async Task ContractLimits_Unauthenticated_ReturnsUnauthorized()
    {
        await using var factory = await NewFactoryAsync(permissions: null);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(LimitsPath)).StatusCode);
    }

    /// <summary>
    /// Claim-free under <c>[Authorize]</c>: it returns one instance-wide integer, needed by roles that
    /// hold no system-settings claim at all.
    /// </summary>
    [Fact]
    public async Task ContractLimits_ForACallerWithNoClaims_ReturnsTheEffectiveCap()
    {
        await using var factory = await NewFactoryAsync(permissions: []);
        using var client = factory.CreateClient();

        var dto = await client.GetFromJsonAsync<ContractLimitsDto>(LimitsPath);

        Assert.Equal(SystemSettingsDefaults.ContractMaxSmartTagsPerContract, dto!.MaxSmartTagsPerContract);
    }

    /// <summary>
    /// An ABSENT row is healthy, not degraded — conflating the two <c>503</c>s every database whose
    /// settings rows have not been seeded.
    /// </summary>
    [Fact]
    public async Task ContractLimits_WithNoRowPresent_ReturnsOkWithTheCompiledDefault()
    {
        await using var factory = await NewFactoryAsync(permissions: []);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(LimitsPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<ContractLimitsDto>();
        Assert.Equal(SystemSettingsDefaults.ContractMaxSmartTagsPerContract, dto!.MaxSmartTagsPerContract);
    }

    [Fact]
    public async Task ContractLimits_WithAConfiguredValue_ReturnsIt()
    {
        await using var factory = await NewFactoryAsync(permissions: []);
        await SystemSettingsSeed.SetAsync(
            factory.Services, SystemSettingsKeys.ContractMaxSmartTagsPerContract, "7");
        using var client = factory.CreateClient();

        var dto = await client.GetFromJsonAsync<ContractLimitsDto>(LimitsPath);

        Assert.Equal(7, dto!.MaxSmartTagsPerContract);
    }

    /// <summary>
    /// A row present with an unusable value IS degraded — the display surface fails closed while the
    /// enforcement path keeps using the conservative number (asserted below).
    /// </summary>
    [Fact]
    public async Task ContractLimits_WithAnUnusableStoredValue_Returns503()
    {
        await using var factory = await NewFactoryAsync(permissions: []);
        await SystemSettingsSeed.SetAsync(
            factory.Services, SystemSettingsKeys.ContractMaxSmartTagsPerContract, "not-a-number");
        using var client = factory.CreateClient();

        Assert.Equal(
            HttpStatusCode.ServiceUnavailable, (await client.GetAsync(LimitsPath)).StatusCode);
    }

    /// <summary>
    /// AC 15 — a degraded read never LOOSENS the cap. With an unusable row the enforcement path falls
    /// back to <c>min(last-known-good, default)</c>, so adds are still refused at the shipped default
    /// even though the display endpoint is <c>503</c>ing.
    /// </summary>
    [Fact]
    public async Task Post_WithADegradedLimitsRead_IsStillRefusedAtTheConservativeCap()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        await SystemSettingsSeed.SetAsync(
            factory.Services, SystemSettingsKeys.ContractMaxSmartTagsPerContract, "not-a-number");
        var contractId = await SeedContractDirectlyAsync(factory);
        using var client = factory.CreateClient();

        for (var i = 0; i < SystemSettingsDefaults.ContractMaxSmartTagsPerContract; i++)
        {
            var tagId = await SeedTagAsync(factory, $"degraded-{i}");
            Assert.Equal(
                HttpStatusCode.Created, (await client.PostAsync(SmartTagPath(contractId, tagId), null)).StatusCode);
        }

        var overCap = await SeedTagAsync(factory, "over-cap");
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (await client.PostAsync(SmartTagPath(contractId, overCap), null)).StatusCode);
    }

    // ── AC 17: the tag-delete pre-check (§9.4), on the InMemory tier ──────────

    /// <summary>
    /// The EF InMemory tiers enforce no foreign keys, so without the pre-check this delete would
    /// silently succeed and orphan the link row. The message names the COUNT and no contract names.
    /// </summary>
    [Fact]
    public async Task DeletingATagThatIsAContractSmartTag_ReturnsConflict_AndTheTagSurvives()
    {
        await using var factory = await NewFactoryAsync(
            [.. ReadWrite, PermissionClaims.TransactionTagsDelete]);
        var first = await SeedContractDirectlyAsync(factory, "Maple St lease");
        var second = await SeedContractDirectlyAsync(factory, "Harbor Point parking");
        var tagId = await SeedTagAsync(factory, "Streaming");
        using var client = factory.CreateClient();
        await client.PostAsync(SmartTagPath(first, tagId), null);
        await client.PostAsync(SmartTagPath(second, tagId), null);

        var response = await client.DeleteAsync($"/api/transaction-tags/{tagId}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("2 contracts", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Maple St lease", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Harbor Point parking", body, StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<OdysseyContext>()
            .TransactionTags.AnyAsync(t => t.TransactionTagId == tagId));
    }

    [Fact]
    public async Task DeletingATagAfterItsLinksAreRemoved_Succeeds()
    {
        await using var factory = await NewFactoryAsync(
            [.. ReadWrite, PermissionClaims.TransactionTagsDelete]);
        var contractId = await SeedContractDirectlyAsync(factory);
        var tagId = await SeedTagAsync(factory);
        using var client = factory.CreateClient();
        await client.PostAsync(SmartTagPath(contractId, tagId), null);
        await client.DeleteAsync(SmartTagPath(contractId, tagId));

        Assert.Equal(
            HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/transaction-tags/{tagId}")).StatusCode);
    }

    // ── AC 19/21: the settings key's bound ────────────────────────────────────

    /// <summary>
    /// AC 21 — a <c>PUT</c> above the ceiling is rejected by model validation, and the ceiling is the
    /// filter-array length rather than a number transcribed from it.
    /// </summary>
    [Fact]
    public async Task PutSystemSettings_AboveTheFilterArrayCeiling_IsRejected()
    {
        await using var factory = await NewFactoryAsync(
            [PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate]);
        using var client = factory.CreateClient();

        var tooHigh = await client.PutAsJsonAsync("/api/system-settings", new SystemSettingsUpdate
        {
            ContractMaxSmartTagsPerContract = ListDefaults.MaxFilterArrayLength + 1,
        });
        Assert.Equal(HttpStatusCode.BadRequest, tooHigh.StatusCode);

        var atCeiling = await client.PutAsJsonAsync("/api/system-settings", new SystemSettingsUpdate
        {
            ContractMaxSmartTagsPerContract = ListDefaults.MaxFilterArrayLength,
        });
        Assert.Equal(HttpStatusCode.OK, atCeiling.StatusCode);
    }

    /// <summary>A saved cap reaches the limits endpoint on the next request — one key, evicted on write.</summary>
    [Fact]
    public async Task ContractLimits_AfterASave_ServesTheNewCap()
    {
        await using var factory = await NewFactoryAsync(
            [PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate]);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/system-settings",
            new SystemSettingsUpdate { ContractMaxSmartTagsPerContract = 4 })).StatusCode);

        var dto = await client.GetFromJsonAsync<ContractLimitsDto>(LimitsPath);
        Assert.Equal(4, dto!.MaxSmartTagsPerContract);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<ApiFactory> NewFactoryAsync(IReadOnlyCollection<string>? permissions)
    {
        var factory = new ApiFactory(permissions);
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OdysseyContext>().Database.EnsureCreatedAsync();
        return factory;
    }

    private static async Task<Guid> SeedContractDirectlyAsync(
        WebApplicationFactory<Program> factory, string name = "Maple St lease")
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        var contract = new Contract
        {
            Name = name,
            Type = Odyssey.Context.ContractType.Rental,
            StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Contracts.Add(contract);
        await context.SaveChangesAsync();
        return contract.ContractId;
    }

    private static async Task<Guid> SeedTagAsync(
        WebApplicationFactory<Program> factory, string name = "Tag", bool archived = false)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        var tag = new TransactionTag
        {
            Name = name,
            Archived = archived ? DateTime.UtcNow : null,
        };
        context.TransactionTags.Add(tag);
        await context.SaveChangesAsync();
        return tag.TransactionTagId;
    }

    /// <summary>
    /// Rewrites one link's <c>AddedAt</c> directly, so the ordering assertion does not depend on the
    /// resolution of the wall clock between two HTTP calls.
    /// </summary>
    private static async Task StampAsync(
        WebApplicationFactory<Program> factory, Guid contractId, Guid tagId, DateTime addedAt)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var link = await context.ContractSmartTags
            .SingleAsync(s => s.ContractId == contractId && s.TransactionTagId == tagId);
        link.AddedAt = addedAt;
        await context.SaveChangesAsync();
    }

    private static async Task<int> CountLinksAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OdysseyContext>()
            .ContractSmartTags.AsNoTracking().CountAsync();
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);
}
