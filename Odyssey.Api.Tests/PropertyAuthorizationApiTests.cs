using System.Net;
using System.Net.Http.Json;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context.Authorization;
using Odyssey.Dtos.Authorization;
using Xunit;
using static Odyssey.Api.Tests.PropertyApiTestSupport;

namespace Odyssey.Api.Tests;

/// <summary>
/// The property authorization matrix over HTTP (issue #167, AC 10-11): each of the fourteen
/// claim-gated endpoints — the spec's thirteen plus the <c>/properties</c> page's summary is reachable by its gating claim alone, and by no combination of every OTHER
/// claim in the vocabulary.
/// </summary>
public class PropertyAuthorizationApiTests
{
    private const string ActorUserId = "property-authorization-actor-id";

    /// <summary>
    /// One row per claim-gated endpoint: its gating claim and the status a successful call returns
    /// against the fixture <see cref="SeedAsync"/> builds.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (string Claim, HttpStatusCode Success)> Endpoints =
        new Dictionary<string, (string, HttpStatusCode)>
        {
            ["GET list"] = (PermissionClaims.PropertiesRead, HttpStatusCode.OK),
            ["GET one"] = (PermissionClaims.PropertiesRead, HttpStatusCode.OK),
            ["GET summary"] = (PermissionClaims.PropertiesRead, HttpStatusCode.OK),
            ["POST property"] = (PermissionClaims.PropertiesCreate, HttpStatusCode.Created),
            ["PUT property"] = (PermissionClaims.PropertiesUpdate, HttpStatusCode.NoContent),
            ["DELETE property"] = (PermissionClaims.PropertiesDelete, HttpStatusCode.NoContent),
            ["GET estimates"] = (PermissionClaims.PropertiesEstimatesRead, HttpStatusCode.OK),
            ["GET current estimate"] = (PermissionClaims.PropertiesEstimatesRead, HttpStatusCode.OK),
            ["POST estimate"] = (PermissionClaims.PropertiesEstimatesWrite, HttpStatusCode.Created),
            ["PUT estimate"] = (PermissionClaims.PropertiesEstimatesWrite, HttpStatusCode.NoContent),
            ["DELETE estimate"] = (PermissionClaims.PropertiesEstimatesWrite, HttpStatusCode.NoContent),
            ["GET smart tags"] = (PermissionClaims.PropertiesRead, HttpStatusCode.OK),
            ["POST smart tag"] = (PermissionClaims.PropertiesUpdate, HttpStatusCode.Created),
            ["DELETE smart tag"] = (PermissionClaims.PropertiesUpdate, HttpStatusCode.NoContent),
        };

    public static TheoryData<string> EndpointNames() => [.. Endpoints.Keys];

    [Fact]
    public void TheMatrix_CoversFourteenEndpoints_AndAllSixPropertyClaims()
    {
        Assert.Equal(14, Endpoints.Count);
        Assert.Equal(
            new[]
            {
                PermissionClaims.PropertiesCreate, PermissionClaims.PropertiesRead, PermissionClaims.PropertiesUpdate,
                PermissionClaims.PropertiesDelete, PermissionClaims.PropertiesEstimatesRead,
                PermissionClaims.PropertiesEstimatesWrite,
            }.Order(),
            Endpoints.Values.Select(e => e.Claim).Distinct().Order());
    }

    /// <summary>AC 10 — the gating claim alone is enough, and the call does what it says.</summary>
    [Theory]
    [MemberData(nameof(EndpointNames))]
    public async Task Endpoint_WithOnlyItsGatingClaim_Succeeds(string endpoint)
    {
        var (claim, success) = Endpoints[endpoint];
        await using var factory = new ApiFactory([claim]);
        var fixture = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var response = await CallAsync(client, endpoint, fixture);

        Assert.True(success == response.StatusCode,
            $"{endpoint}: expected {success}, got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    /// <summary>
    /// AC 10 — every claim in <see cref="RolePermissions.AllClaims"/> EXCEPT the gating one is still a
    /// 403: no other claim, property or otherwise, reaches the endpoint.
    /// </summary>
    [Theory]
    [MemberData(nameof(EndpointNames))]
    public async Task Endpoint_WithEveryOtherClaim_ReturnsForbidden(string endpoint)
    {
        var (claim, _) = Endpoints[endpoint];
        await using var factory = new ApiFactory([.. RolePermissions.AllClaims.Where(c => c != claim)]);
        var fixture = await SeedAsync(factory);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(client, endpoint, fixture)).StatusCode);
    }

    [Theory]
    [MemberData(nameof(EndpointNames))]
    public async Task Endpoint_Unauthenticated_ReturnsUnauthorized(string endpoint)
    {
        await using var factory = new ApiFactory(permissions: null);
        var fixture = await SeedAsync(factory);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(client, endpoint, fixture)).StatusCode);
    }

    /// <summary>
    /// AC 11 — the User role (and Guest, which holds the same two property claims) can read the list
    /// and the estimates, and is refused every property write, estimate write and smart-tag write.
    /// </summary>
    [Fact]
    public async Task UserRole_CanReadListAndEstimates_ButCannotWrite()
    {
        await using var factory = new ApiFactory(RolePermissions.UserClaims);
        var fixture = await SeedAsync(factory);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await CallAsync(client, "GET list", fixture)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(client, "GET summary", fixture)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(client, "GET estimates", fixture)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(client, "GET current estimate", fixture)).StatusCode);

        foreach (var write in new[]
                 {
                     "POST property", "PUT property", "DELETE property", "POST smart tag", "DELETE smart tag",
                     "POST estimate", "PUT estimate", "DELETE estimate",
                 })
        {
            Assert.True(HttpStatusCode.Forbidden == (await CallAsync(client, write, fixture)).StatusCode, write);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed record Fixture(Guid PropertyId, Guid EstimateId, Guid LinkedTagId, Guid UnlinkedTagId);

    /// <summary>A property with one estimate, one linked smart tag and one free tag.</summary>
    private static async Task<Fixture> SeedAsync(ApiFactory factory)
    {
        var propertyId = await SeedPropertyAsync(factory);
        var estimateId = await SeedEstimateAsync(
            factory, propertyId, 1000m, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var linked = await SeedTagAsync(factory, "Linked");
        var unlinked = await SeedTagAsync(factory, "Unlinked");
        await LinkSmartTagAsync(factory, propertyId, linked);
        return new Fixture(propertyId, estimateId, linked, unlinked);
    }

    private static Task<HttpResponseMessage> CallAsync(HttpClient client, string endpoint, Fixture f) => endpoint switch
    {
        "GET list" => client.GetAsync(PropertiesPath),
        "GET one" => client.GetAsync(PropertyPath(f.PropertyId)),
        "GET summary" => client.GetAsync(SummaryPath),
        "POST property" => client.PostAsJsonAsync(PropertiesPath, VehicleBody()),
        "PUT property" => client.PutAsJsonAsync(PropertyPath(f.PropertyId), RealEstateBody("Renamed")),
        "DELETE property" => client.DeleteAsync(PropertyPath(f.PropertyId)),
        "GET estimates" => client.GetAsync(EstimatesPath(f.PropertyId)),
        "GET current estimate" => client.GetAsync(CurrentEstimatePath(f.PropertyId)),
        "POST estimate" => client.PostAsJsonAsync(
            EstimatesPath(f.PropertyId), EstimateBody(2000m, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))),
        "PUT estimate" => client.PutAsJsonAsync(
            EstimatePath(f.PropertyId, f.EstimateId), EstimateBody(1500m, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc))),
        "DELETE estimate" => client.DeleteAsync(EstimatePath(f.PropertyId, f.EstimateId)),
        "GET smart tags" => client.GetAsync(SmartTagsPath(f.PropertyId)),
        "POST smart tag" => client.PostAsync(SmartTagPath(f.PropertyId, f.UnlinkedTagId), null),
        "DELETE smart tag" => client.DeleteAsync(SmartTagPath(f.PropertyId, f.LinkedTagId)),
        _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, null),
    };

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);
}
