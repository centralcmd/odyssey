using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;
using static Odyssey.Api.Tests.PropertyApiTestSupport;

namespace Odyssey.Api.Tests;

/// <summary>
/// Property CRUD and the list over HTTP (issue #167): the subtype invariant, the type-immutability
/// <c>422</c>, the non-upsert <c>PUT</c>, the delete cascade, and the server-side list contract.
/// </summary>
public class PropertiesApiTests
{
    private const string ActorUserId = "properties-actor-id";

    private static readonly string[] FullAccess =
    [
        PermissionClaims.PropertiesCreate,
        PermissionClaims.PropertiesRead,
        PermissionClaims.PropertiesUpdate,
        PermissionClaims.PropertiesDelete,
    ];

    // ── AC 1-2: create round-trips every field ────────────────────────────────

    /// <summary>AC 1 — a RealEstate body is created, located at GET /api/properties/{id}, and every detail field survives.</summary>
    [Fact]
    public async Task Post_RealEstate_ReturnsCreated_AtItsGetRoute_AndRoundTripsEveryDetailField()
    {
        await using var factory = await NewFactoryAsync(FullAccess);
        using var client = factory.CreateClient();

        var details = new RealEstateDetailsDto
        {
            Kind = RealEstateKind.Cabin,
            AddressLine = "Fjellveien 12",
            PostalCode = "5019",
            City = "Bergen",
            CountryCode = "NO",
            CadastralNumber = "164/22",
            LivingAreaSqm = 84.5m,
            PlotAreaSqm = 1200m,
            BuildYear = 1978,
        };
        var body = RealEstateBody() with
        {
            AcquiredDate = new DateTime(2015, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Notes = "Inherited",
            RealEstateDetails = details,
        };

        var response = await client.PostAsJsonAsync(PropertiesPath, body);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<ExistingProperty>())!;
        Assert.NotEqual(Guid.Empty, created.PropertyId);
        Assert.EndsWith(PropertyPath(created.PropertyId), response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);

        var fetched = (await client.GetFromJsonAsync<ExistingProperty>(response.Headers.Location))!;
        foreach (var property in new[] { created, fetched })
        {
            Assert.Equal(PropertyType.RealEstate, property.Type);
            Assert.Equal("Maple St house", property.Name);
            Assert.Equal("USD", property.CurrencyCode);
            Assert.Equal("Inherited", property.Notes);
            Assert.Equal(PropertyStatus.Owned, property.Status);
            Assert.Null(property.VehicleDetails);
            Assert.Equal(details, property.RealEstateDetails);
        }
    }

    /// <summary>AC 2 — the Vehicle counterpart of AC 1.</summary>
    [Fact]
    public async Task Post_Vehicle_ReturnsCreated_AtItsGetRoute_AndRoundTripsEveryDetailField()
    {
        await using var factory = await NewFactoryAsync(FullAccess);
        using var client = factory.CreateClient();

        var details = new VehicleDetailsDto
        {
            Kind = VehicleKind.Boat,
            RegistrationNumber = "EL12345",
            Vin = "YV1LW5547T2233445",
            Make = "Volvo",
            Model = "Penta",
            ModelYear = 2019,
            FirstRegisteredDate = new DateTime(2019, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        var response = await client.PostAsJsonAsync(PropertiesPath, VehicleBody() with { VehicleDetails = details });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<ExistingProperty>())!;
        Assert.EndsWith(PropertyPath(created.PropertyId), response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);

        var fetched = (await client.GetFromJsonAsync<ExistingProperty>(response.Headers.Location))!;
        foreach (var property in new[] { created, fetched })
        {
            Assert.Equal(PropertyType.Vehicle, property.Type);
            Assert.Null(property.RealEstateDetails);
            Assert.Equal(details, property.VehicleDetails);
        }
    }

    /// <summary>The service's documented identifier normalization: uppercased and whitespace-stripped.</summary>
    [Fact]
    public async Task Post_Vehicle_NormalizesRegistrationNumberAndVin()
    {
        await using var factory = await NewFactoryAsync(FullAccess);
        using var client = factory.CreateClient();

        var body = VehicleBody() with
        {
            VehicleDetails = new VehicleDetailsDto { Kind = VehicleKind.Car, RegistrationNumber = "el 123 45", Vin = " yv1 lw5 " },
        };

        var created = (await (await client.PostAsJsonAsync(PropertiesPath, body)).Content.ReadFromJsonAsync<ExistingProperty>())!;

        Assert.Equal("EL12345", created.VehicleDetails!.RegistrationNumber);
        Assert.Equal("YV1LW5", created.VehicleDetails.Vin);
    }

    // ── AC 3: the subtype invariant at model validation ───────────────────────

    /// <summary>AC 3 — RealEstate with only vehicle details is a 400 keyed on RealEstateDetails, and nothing is stored.</summary>
    [Fact]
    public async Task Post_RealEstateWithOnlyVehicleDetails_ReturnsBadRequestKeyedOnRealEstateDetails()
    {
        await using var factory = await NewFactoryAsync(FullAccess);
        using var client = factory.CreateClient();

        var body = RealEstateBody() with
        {
            RealEstateDetails = null,
            VehicleDetails = new VehicleDetailsDto { Kind = VehicleKind.Car },
        };

        var response = await client.PostAsJsonAsync(PropertiesPath, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(await ReadErrorKeysAsync(response),
            key => string.Equals(key, nameof(NewProperty.RealEstateDetails), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, await CountAsync<Property>(factory));
    }

    /// <summary>AC 3 — both sub-objects is a 400 too, keyed on the one that does not belong.</summary>
    [Fact]
    public async Task Post_RealEstateWithBothDetailObjects_ReturnsBadRequest()
    {
        await using var factory = await NewFactoryAsync(FullAccess);
        using var client = factory.CreateClient();

        var body = RealEstateBody() with { VehicleDetails = new VehicleDetailsDto { Kind = VehicleKind.Car } };

        var response = await client.PostAsJsonAsync(PropertiesPath, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(await ReadErrorKeysAsync(response),
            key => string.Equals(key, nameof(NewProperty.VehicleDetails), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, await CountAsync<Property>(factory));
    }

    /// <summary>An omitted type is a [Required] failure, never a silent bind to RealEstate (0).</summary>
    [Fact]
    public async Task Post_WithTypeOmitted_ReturnsBadRequestKeyedOnType()
    {
        await using var factory = await NewFactoryAsync(FullAccess);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(PropertiesPath, new
        {
            name = "No type",
            description = "desc",
            currencyCode = "USD",
            realEstateDetails = new { kind = (int)RealEstateKind.House },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(await ReadErrorKeysAsync(response),
            key => string.Equals(key, nameof(NewProperty.Type), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, await CountAsync<Property>(factory));
    }

    // ── AC 4-5: PUT ───────────────────────────────────────────────────────────

    /// <summary>AC 4 — a type change is a 422 keyed on Type, and the stored property is untouched.</summary>
    [Fact]
    public async Task Put_ChangingTheType_ReturnsUnprocessableKeyedOnType_AndLeavesThePropertyUnchanged()
    {
        await using var factory = await NewFactoryAsync(FullAccess);
        using var client = factory.CreateClient();
        var created = (await (await client.PostAsJsonAsync(PropertiesPath, RealEstateBody()))
            .Content.ReadFromJsonAsync<ExistingProperty>())!;

        var response = await client.PutAsJsonAsync(PropertyPath(created.PropertyId), VehicleBody("Now a car"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(await ReadErrorKeysAsync(response),
            key => string.Equals(key, "Type", StringComparison.OrdinalIgnoreCase));

        var after = await GetPropertyAsync(client, created.PropertyId);
        Assert.Equal(PropertyType.RealEstate, after.Type);
        Assert.Equal("Maple St house", after.Name);
        Assert.Equal(created.RealEstateDetails, after.RealEstateDetails);
        Assert.Null(after.VehicleDetails);
    }

    /// <summary>AC 4 — a same-type PUT is a 204 and replaces the fields.</summary>
    [Fact]
    public async Task Put_SameType_ReturnsNoContent_AndUpdatesTheFields()
    {
        await using var factory = await NewFactoryAsync(FullAccess);
        using var client = factory.CreateClient();
        var created = (await (await client.PostAsJsonAsync(PropertiesPath, RealEstateBody()))
            .Content.ReadFromJsonAsync<ExistingProperty>())!;

        var replacement = RealEstateBody("Renamed house") with
        {
            Notes = "Renovated",
            RealEstateDetails = new RealEstateDetailsDto { Kind = RealEstateKind.Apartment, City = "Oslo", BuildYear = 2001 },
        };
        var response = await client.PutAsJsonAsync(PropertyPath(created.PropertyId), replacement);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var after = await GetPropertyAsync(client, created.PropertyId);
        Assert.Equal("Renamed house", after.Name);
        Assert.Equal("Renovated", after.Notes);
        Assert.Equal(RealEstateKind.Apartment, after.RealEstateDetails!.Kind);
        Assert.Equal("Oslo", after.RealEstateDetails.City);
        Assert.Equal(2001, after.RealEstateDetails.BuildYear);
    }

    /// <summary>AC 5 — PUT is not an upsert: an unknown id is a 404 and nothing is created.</summary>
    [Fact]
    public async Task Put_UnknownId_ReturnsNotFound_AndCreatesNothing()
    {
        await using var factory = await NewFactoryAsync(FullAccess);
        using var client = factory.CreateClient();
        var unknownId = Guid.NewGuid();

        var response = await client.PutAsJsonAsync(PropertyPath(unknownId), RealEstateBody());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await CountAsync<Property>(factory));
        Assert.Equal(0, await CountAsync<RealEstateDetails>(factory));
    }

    [Fact]
    public async Task Get_UnknownId_ReturnsNotFound()
    {
        await using var factory = await NewFactoryAsync(FullAccess);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(PropertyPath(Guid.NewGuid()))).StatusCode);
    }

    // ── AC 6: DELETE ──────────────────────────────────────────────────────────

    /// <summary>
    /// AC 6 — the detail row, estimates and smart-tag links go with the property; the tags survive. On
    /// the InMemory tier this proves the service's <c>Include</c>s, not a database cascade.
    /// </summary>
    [Fact]
    public async Task Delete_RemovesDetailsEstimatesAndSmartTagLinks_AndLeavesTheTags()
    {
        await using var factory = await NewFactoryAsync(FullAccess);
        var propertyId = await SeedPropertyAsync(factory);
        var survivorId = await SeedPropertyAsync(factory, "Survivor");
        await SeedEstimateAsync(factory, propertyId, 1000m, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await SeedEstimateAsync(factory, survivorId, 2000m, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var tagId = await SeedTagAsync(factory, "Upkeep");
        await LinkSmartTagAsync(factory, propertyId, tagId);
        await LinkSmartTagAsync(factory, survivorId, tagId);
        using var client = factory.CreateClient();

        var response = await client.DeleteAsync(PropertyPath(propertyId));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(PropertyPath(propertyId))).StatusCode);
        await ReadAsync(factory, async context =>
        {
            Assert.False(await context.RealEstateDetails.AnyAsync(d => d.PropertyId == propertyId));
            Assert.False(await context.PropertyEstimates.AnyAsync(e => e.PropertyId == propertyId));
            Assert.False(await context.PropertySmartTags.AnyAsync(s => s.PropertyId == propertyId));
            Assert.True(await context.TransactionTags.AnyAsync(t => t.TransactionTagId == tagId));

            // The sibling property's rows are untouched.
            Assert.True(await context.PropertyEstimates.AnyAsync(e => e.PropertyId == survivorId));
            Assert.True(await context.PropertySmartTags.AnyAsync(s => s.PropertyId == survivorId));
            return true;
        });
    }

    [Fact]
    public async Task Delete_UnknownId_ReturnsNotFound()
    {
        await using var factory = await NewFactoryAsync(FullAccess);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(PropertyPath(Guid.NewGuid()))).StatusCode);
    }

    // ── AC 18: no over-posting through the detail sub-object ──────────────────

    /// <summary>
    /// AC 18 — a <c>propertyId</c> smuggled into <c>realEstateDetails</c> names another property; the
    /// detail row is still created under the NEW generated id and the other property is untouched.
    /// </summary>
    [Fact]
    public async Task Post_WithAPropertyIdInsideRealEstateDetails_CreatesUnderTheNewId_AndTouchesNoOtherProperty()
    {
        await using var factory = await NewFactoryAsync(FullAccess);
        var victimId = await SeedPropertyAsync(factory, "Victim", city: "Tromsø");
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(PropertiesPath, new
        {
            name = "Attacker",
            description = "desc",
            type = (int)PropertyType.RealEstate,
            currencyCode = "USD",
            propertyId = victimId,
            realEstateDetails = new { propertyId = victimId, kind = (int)RealEstateKind.Plot, city = "Injected" },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<ExistingProperty>())!;
        Assert.NotEqual(victimId, created.PropertyId);
        Assert.Equal("Injected", created.RealEstateDetails!.City);

        await ReadAsync(factory, async context =>
        {
            var details = await context.RealEstateDetails.AsNoTracking().ToListAsync();
            Assert.Equal(2, details.Count);
            Assert.Equal("Tromsø", details.Single(d => d.PropertyId == victimId).City);
            Assert.Equal(RealEstateKind.House, details.Single(d => d.PropertyId == victimId).Kind);
            Assert.Equal("Injected", details.Single(d => d.PropertyId == created.PropertyId).City);
            Assert.Equal("Victim", (await context.Properties.AsNoTracking().SingleAsync(p => p.PropertyId == victimId)).Name);
            return true;
        });
    }

    // ── AC 7: the list contract ───────────────────────────────────────────────

    /// <summary>AC 7 — search reaches the detail fields (city), not only the name.</summary>
    [Fact]
    public async Task List_Search_MatchesNameAndDetailFields()
    {
        await using var factory = await NewFactoryAsync(FullAccess);
        await SeedPropertyAsync(factory, "Harbour flat", city: "Stavanger");
        await SeedPropertyAsync(factory, "Cabin", city: "Geilo");
        await SeedPropertyAsync(factory, "Old Volvo", PropertyType.Vehicle);
        using var client = factory.CreateClient();

        var byCity = await GetPageAsync(client, "?search=stavanger");
        Assert.Equal(["Harbour flat"], byCity.Items.Select(p => p.Name));
        Assert.Equal(1, byCity.TotalCount);

        var byMake = await GetPageAsync(client, "?search=volvo");
        Assert.Equal(["Old Volvo"], byMake.Items.Select(p => p.Name));
    }

    /// <summary>AC 7 — the types filter binds both member names and ordinals.</summary>
    [Fact]
    public async Task List_TypesFilter_ReturnsOnlyTheRequestedSubtypes()
    {
        await using var factory = await NewFactoryAsync(FullAccess);
        await SeedPropertyAsync(factory, "House");
        await SeedPropertyAsync(factory, "Car", PropertyType.Vehicle);
        await SeedPropertyAsync(factory, "Boat", PropertyType.Vehicle);
        using var client = factory.CreateClient();

        var vehicles = await GetPageAsync(client, "?types=Vehicle");
        Assert.Equal(["Boat", "Car"], vehicles.Items.Select(p => p.Name));
        Assert.Equal(2, vehicles.TotalCount);

        var realEstate = await GetPageAsync(client, "?types=0");
        Assert.Equal(["House"], realEstate.Items.Select(p => p.Name));

        var both = await GetPageAsync(client, "?types=RealEstate&types=Vehicle");
        Assert.Equal(3, both.TotalCount);
    }

    /// <summary>AC 7 — the status filter reads the derived status: archived beats disposed, a future disposal is still owned.</summary>
    [Fact]
    public async Task List_StatusesFilter_UsesTheDerivedStatus()
    {
        await using var factory = await NewFactoryAsync(FullAccess);
        var past = DateTime.UtcNow.AddYears(-1);
        var future = DateTime.UtcNow.AddYears(1);
        await SeedPropertyAsync(factory, "Owned");
        await SeedPropertyAsync(factory, "Selling next year", disposed: future);
        await SeedPropertyAsync(factory, "Sold", disposed: past);
        await SeedPropertyAsync(factory, "Archived and sold", disposed: past, archived: true);
        await SeedPropertyAsync(factory, "Archived", archived: true);
        using var client = factory.CreateClient();

        var owned = await GetPageAsync(client, "?statuses=Owned");
        Assert.Equal(["Owned", "Selling next year"], owned.Items.Select(p => p.Name));
        Assert.All(owned.Items, p => Assert.Equal(PropertyStatus.Owned, p.Status));

        var disposed = await GetPageAsync(client, "?statuses=Disposed");
        Assert.Equal(["Sold"], disposed.Items.Select(p => p.Name));
        Assert.Equal(PropertyStatus.Disposed, disposed.Items.Single().Status);

        var archived = await GetPageAsync(client, "?statuses=Archived");
        Assert.Equal(["Archived", "Archived and sold"], archived.Items.Select(p => p.Name));
        Assert.All(archived.Items, p => Assert.Equal(PropertyStatus.Archived, p.Status));

        var ownedOrDisposed = await GetPageAsync(client, "?statuses=Owned&statuses=Disposed");
        Assert.Equal(3, ownedOrDisposed.TotalCount);
    }

    /// <summary>AC 7 — sortBy/sortDir over name, type and acquired date.</summary>
    [Fact]
    public async Task List_SortByAndSortDir_OrderTheRows()
    {
        await using var factory = await NewFactoryAsync(FullAccess);
        await SeedPropertyAsync(factory, "Bravo", PropertyType.Vehicle, acquired: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await SeedPropertyAsync(factory, "Alpha", acquired: new DateTime(2010, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await SeedPropertyAsync(factory, "Charlie", acquired: new DateTime(2015, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        using var client = factory.CreateClient();

        Assert.Equal(["Alpha", "Bravo", "Charlie"], (await GetPageAsync(client, "")).Items.Select(p => p.Name));
        Assert.Equal(["Charlie", "Bravo", "Alpha"],
            (await GetPageAsync(client, "?sortBy=name&sortDir=desc")).Items.Select(p => p.Name));
        Assert.Equal(["Alpha", "Charlie", "Bravo"],
            (await GetPageAsync(client, "?sortBy=acquired&sortDir=asc")).Items.Select(p => p.Name));
        Assert.Equal(["Bravo", "Charlie", "Alpha"],
            (await GetPageAsync(client, "?sortBy=acquired&sortDir=desc")).Items.Select(p => p.Name));
        Assert.Equal(PropertyType.Vehicle,
            (await GetPageAsync(client, "?sortBy=type&sortDir=desc")).Items.First().Type);
    }

    /// <summary>AC 7 — offset/limit slice the page while TotalCount counts every matching row, pre-slice.</summary>
    [Fact]
    public async Task List_OffsetAndLimit_SliceThePage_AndTotalCountIsPreSlice()
    {
        await using var factory = await NewFactoryAsync(FullAccess);
        foreach (var name in new[] { "A", "B", "C", "D", "E" })
            await SeedPropertyAsync(factory, name);
        await SeedPropertyAsync(factory, "Z car", PropertyType.Vehicle);
        using var client = factory.CreateClient();

        var page = await GetPageAsync(client, "?types=RealEstate&offset=1&limit=2");

        Assert.Equal(["B", "C"], page.Items.Select(p => p.Name));
        Assert.Equal(5, page.TotalCount);
        Assert.Equal(1, page.Offset);
        Assert.Equal(2, page.Limit);
    }

    // ── AC 8: unbindable or out-of-range list queries ─────────────────────────

    public static TheoryData<string> InvalidListQueries() =>
    [
        $"?limit={ListDefaults.MaxLimit + 1}",
        "?sortBy=bogus",
        "?types=Spaceship",
        "?statuses=Stolen",
        "?sortDir=sideways",
        "?offset=-1",
    ];

    /// <summary>AC 8 — each is rejected by model validation with a 400, never silently clamped or dropped.</summary>
    [Theory]
    [MemberData(nameof(InvalidListQueries))]
    public async Task List_InvalidQuery_ReturnsBadRequest(string query)
    {
        await using var factory = await NewFactoryAsync(FullAccess);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(PropertiesPath + query)).StatusCode);
    }

    /// <summary>AC 8 — a search one character over the cap is a 400; at the cap it is accepted.</summary>
    [Fact]
    public async Task List_SearchOverTheLengthCap_ReturnsBadRequest()
    {
        await using var factory = await NewFactoryAsync(FullAccess);
        using var client = factory.CreateClient();

        var tooLong = new string('a', ListDefaults.MaxSearchLength + 1);
        var atCap = new string('a', ListDefaults.MaxSearchLength);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{PropertiesPath}?search={tooLong}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{PropertiesPath}?search={atCap}")).StatusCode);
    }

    // ── AC 9: sortBy=value ────────────────────────────────────────────────────

    /// <summary>
    /// AC 9 — sorts by the estimate in force NOW (a superseded one and a future one are ignored), with
    /// the properties that have no current estimate last in BOTH directions.
    /// </summary>
    [Fact]
    public async Task List_SortByValue_UsesTheCurrentEstimate_WithNullsLastInBothDirections()
    {
        // Sorting by value ranks the properties by worth, so it needs the estimates claim as well.
        await using var factory = await NewFactoryAsync([.. FullAccess, PermissionClaims.PropertiesEstimatesRead]);
        var cheap = await SeedPropertyAsync(factory, "Cheap");
        var dear = await SeedPropertyAsync(factory, "Dear");
        await SeedPropertyAsync(factory, "Unvalued");
        var futureOnly = await SeedPropertyAsync(factory, "Valued next year");

        var utc = DateTimeKind.Utc;
        // Cheap was once the dearest; only its newer, lower estimate is in force.
        await SeedEstimateAsync(factory, cheap, 900_000m, new DateTime(2020, 1, 1, 0, 0, 0, utc));
        await SeedEstimateAsync(factory, cheap, 100_000m, new DateTime(2024, 1, 1, 0, 0, 0, utc));
        await SeedEstimateAsync(factory, dear, 300_000m, new DateTime(2023, 1, 1, 0, 0, 0, utc));
        await SeedEstimateAsync(factory, futureOnly, 1_000_000m, DateTime.UtcNow.AddYears(1));
        using var client = factory.CreateClient();

        var ascending = (await GetPageAsync(client, "?sortBy=value&sortDir=asc")).Items.Select(p => p.Name).ToList();
        var descending = (await GetPageAsync(client, "?sortBy=value&sortDir=desc")).Items.Select(p => p.Name).ToList();

        Assert.Equal(["Cheap", "Dear"], ascending.Take(2));
        Assert.Equal(["Dear", "Cheap"], descending.Take(2));
        Assert.Equal(["Unvalued", "Valued next year"], ascending.Skip(2).Order());
        Assert.Equal(["Unvalued", "Valued next year"], descending.Skip(2).Order());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<PagedResult<ExistingProperty>> GetPageAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync(PropertiesPath + query);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PagedResult<ExistingProperty>>())!;
    }

    private static async Task<ApiFactory> NewFactoryAsync(IReadOnlyCollection<string>? permissions)
    {
        var factory = new ApiFactory(permissions);
        await EnsureCreatedAsync(factory);
        return factory;
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);
}
