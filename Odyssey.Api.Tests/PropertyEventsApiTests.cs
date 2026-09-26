using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Context.Authorization;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;
using static Odyssey.Api.Tests.PropertyApiTestSupport;
using ContextPropertyEventType = Odyssey.Context.PropertyEventType;
using ContextEventSource = Odyssey.Context.ContractEventSource;
using PropertyEventType = Odyssey.Dtos.Finance.PropertyEventType;
using ContractEventSource = Odyssey.Dtos.Finance.ContractEventSource;

namespace Odyssey.Api.Tests;

/// <summary>
/// The four property-scoped event endpoints (issue #209) over real HTTP: the claim gates, the
/// author label, model binding against over-posting, owner scoping across the shared table, the list
/// surface, and the system rows <c>PUT /api/properties/{id}</c> records.
/// </summary>
public class PropertyEventsApiTests
{
    private const string ActorUserId = "property-events-actor-id";

    private static readonly DateTime FixedNow = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private static readonly string[] ReadOnly = [PermissionClaims.PropertiesRead];

    private static readonly string[] ReadWrite =
        [PermissionClaims.PropertiesRead, PermissionClaims.PropertiesCreate, PermissionClaims.PropertiesUpdate];

    private static string Events(Guid propertyId) => $"{PropertyPath(propertyId)}/events";

    private static object Body(
        PropertyEventType type = PropertyEventType.Other,
        string title = "Something happened",
        DateTime? occurredAt = null,
        string? description = null,
        string? notes = null) => new
    {
        type,
        title,
        description,
        notes,
        occurredAt = occurredAt ?? FixedNow.AddDays(-1),
    };

    // ── Create ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_ValidBody_Returns201WithTheAuthorLabel_AndTheListLocation()
    {
        await using var factory = new ApiFactory(ReadWrite);
        // The store first, so the reference currencies are seeded before the actor row creates it.
        await EnsureCreatedAsync(factory);
        await factory.SeedActorUserAsync(displayName: "Kari Nordmann");
        var propertyId = await SeedPropertyAsync(factory, type: PropertyType.Vehicle);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            Events(propertyId), Body(PropertyEventType.TyreChange, "Winter tyres on", notes: "Next change mid-April"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.EndsWith(Events(propertyId), response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
        var created = (await response.Content.ReadFromJsonAsync<ExistingPropertyEvent>())!;
        Assert.NotEqual(Guid.Empty, created.PropertyEventId);
        Assert.Equal(propertyId, created.PropertyId);
        Assert.Equal(PropertyEventType.TyreChange, created.Type);
        Assert.Equal(ContractEventSource.User, created.Source);
        Assert.Equal("Kari Nordmann", created.CreatedBy);
        Assert.Equal(FixedNow, created.CreatedAtUtc);
    }

    /// <summary>AC 4 — the same vehicle-only type on a real-estate property is a 422 keyed type.</summary>
    [Fact]
    public async Task Post_AVehicleOnlyTypeOnRealEstate_Is422KeyedType()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var propertyId = await SeedPropertyAsync(factory, type: PropertyType.RealEstate);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Events(propertyId), Body(PropertyEventType.TyreChange));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("type", await ReadErrorKeysAsync(response), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(0, await CountAsync<PropertyEvent>(factory));
    }

    /// <summary>AC 5 — a system-only type is refused on POST.</summary>
    [Fact]
    public async Task Post_ASystemOnlyType_Is422KeyedType()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var propertyId = await SeedPropertyAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Events(propertyId), Body(PropertyEventType.Archived));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("type", await ReadErrorKeysAsync(response), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Post_AnUndefinedTypeOrABlankTitle_Is400()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var propertyId = await SeedPropertyAsync(factory);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync(Events(propertyId), Body((PropertyEventType)8))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync(Events(propertyId), Body(title: "   "))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync(Events(propertyId), Body(title: new string('x', 257)))).StatusCode);
    }

    /// <summary>AC 9 — 59 s ahead is accepted, 61 s ahead is a 422 keyed occurredAt.</summary>
    [Fact]
    public async Task Post_TheFutureBound_IsSixtySeconds()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var propertyId = await SeedPropertyAsync(factory);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync(Events(propertyId), Body(occurredAt: FixedNow.AddSeconds(59)))).StatusCode);

        var late = await client.PostAsJsonAsync(Events(propertyId), Body(occurredAt: FixedNow.AddSeconds(61)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, late.StatusCode);
        Assert.Contains("occurredAt", await ReadErrorKeysAsync(late), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// AC 7 — a body carrying an owner id, a source, an author or a creation time changes none of them.
    /// </summary>
    [Fact]
    public async Task Post_OverPostedServerFields_AreIgnored()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var propertyId = await SeedPropertyAsync(factory);
        var otherProperty = await SeedPropertyAsync(factory, name: "Other");
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Events(propertyId), new
        {
            type = PropertyEventType.Repair,
            title = "Fixed",
            occurredAt = FixedNow.AddDays(-1),
            propertyId = otherProperty,
            contractId = Guid.NewGuid(),
            source = 1,
            createdBy = "someone-else",
            createdByUserId = "someone-else",
            createdAtUtc = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var stored = await ReadAsync(factory, c => c.PropertyEvents.AsNoTracking().SingleAsync());
        Assert.Equal(propertyId, stored.PropertyId);
        Assert.Equal(ContextEventSource.User, stored.Source);
        Assert.Equal(ActorUserId, stored.CreatedByUserId);
        Assert.Equal(FixedNow, stored.CreatedAtUtc);
        Assert.Equal(0, await CountAsync<ContractEvent>(factory));
    }

    /// <summary>AC 3a, API half — an explicit Other is persisted as 108, never the shared column default.</summary>
    [Fact]
    public async Task Post_WithTypeOmitted_PersistsOther()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var propertyId = await SeedPropertyAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            Events(propertyId), new { title = "Untyped", occurredAt = FixedNow.AddDays(-1) });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var stored = await ReadAsync(factory, c => c.PropertyEvents.AsNoTracking().SingleAsync());
        Assert.Equal(ContextPropertyEventType.Other, stored.Type);
    }

    [Fact]
    public async Task Post_OnAnUnknownProperty_Is404EchoingOnlyTheId()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await EnsureCreatedAsync(factory);
        using var client = factory.CreateClient();
        var unknown = Guid.NewGuid();

        var response = await client.PostAsJsonAsync(Events(unknown), Body(title: "Secret title"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var detail = await ReadDetailAsync(response) ?? "";
        Assert.Contains(unknown.ToString(), detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Secret title", detail, StringComparison.Ordinal);
    }

    // ── Update and delete ────────────────────────────────────────────────────

    /// <summary>AC 5 — a PUT on a system Archived row may keep the type; source stays System.</summary>
    [Fact]
    public async Task Put_OnASystemRow_KeepingItsType_Returns200_AndSourceStaysSystem()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var propertyId = await SeedPropertyAsync(factory);
        var systemId = await SeedEventAsync(factory, propertyId, ContextPropertyEventType.Archived, ContextEventSource.System);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(
            $"{Events(propertyId)}/{systemId}", Body(PropertyEventType.Archived, "Archived after the sale"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<ExistingPropertyEvent>())!;
        Assert.Equal(PropertyEventType.Archived, updated.Type);
        Assert.Equal(ContractEventSource.System, updated.Source);
        Assert.Equal("Archived after the sale", updated.Title);
    }

    [Fact]
    public async Task Put_IntroducingASystemOnlyType_Is422()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var propertyId = await SeedPropertyAsync(factory);
        var userId = await SeedEventAsync(factory, propertyId, ContextPropertyEventType.Repair, ContextEventSource.User);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(
            $"{Events(propertyId)}/{userId}", Body(PropertyEventType.DisposalReversed));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    /// <summary>AC 8 — another property's event, or a contract's, is a 404 on both PUT and DELETE.</summary>
    [Fact]
    public async Task PutAndDelete_AcrossOwners_Are404()
    {
        await using var factory = new ApiFactory([.. ReadWrite, PermissionClaims.ContractsRead, PermissionClaims.ContractsUpdate]);
        var a = await SeedPropertyAsync(factory, name: "A");
        var b = await SeedPropertyAsync(factory, name: "B");
        var onB = await SeedEventAsync(factory, b, ContextPropertyEventType.Repair, ContextEventSource.User);
        var (contractId, contractEventId) = await SeedContractEventAsync(factory);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"{Events(a)}/{onB}", Body())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"{Events(a)}/{contractEventId}", Body())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{Events(a)}/{onB}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{Events(a)}/{contractEventId}")).StatusCode);

        // And the other way round: a property event id on a contract route.
        var onContractRoute = await client.PutAsJsonAsync(
            $"/api/contracts/{contractId}/events/{onB}",
            new { type = 8, title = "Hijacked", occurredAt = FixedNow.AddDays(-1) });
        Assert.Equal(HttpStatusCode.NotFound, onContractRoute.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/contracts/{contractId}/events/{onB}")).StatusCode);

        Assert.Equal(1, await CountAsync<PropertyEvent>(factory));
        Assert.Equal(1, await CountAsync<ContractEvent>(factory));
    }

    [Fact]
    public async Task Delete_RemovesTheEvent_Returns204()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var propertyId = await SeedPropertyAsync(factory);
        var eventId = await SeedEventAsync(factory, propertyId, ContextPropertyEventType.Acquired, ContextEventSource.System);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Events(propertyId)}/{eventId}")).StatusCode);
        Assert.Equal(0, await CountAsync<PropertyEvent>(factory));
    }

    // ── Read ─────────────────────────────────────────────────────────────────

    /// <summary>AC 16 — an author whose row is gone reads "Unknown user", never a blank or an id.</summary>
    [Fact]
    public async Task Get_AnEventWithNoAuthor_ReadsUnknownUser()
    {
        await using var factory = new ApiFactory(ReadOnly);
        var propertyId = await SeedPropertyAsync(factory);
        await SeedEventAsync(factory, propertyId, ContextPropertyEventType.Repair, ContextEventSource.User);
        using var client = factory.CreateClient();

        var items = await client.GetPagedItemsAsync<ExistingPropertyEvent>(Events(propertyId));

        Assert.Equal("Unknown user", Assert.Single(items).CreatedBy);
    }

    [Fact]
    public async Task Get_OnAnUnknownProperty_Is404()
    {
        await using var factory = new ApiFactory(ReadOnly);
        await EnsureCreatedAsync(factory);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Events(Guid.NewGuid()))).StatusCode);
    }

    /// <summary>AC 18 — filters, all four sort keys, paging, and a 400 on an out-of-range limit.</summary>
    [Fact]
    public async Task Get_SupportsTheListSurface()
    {
        await using var factory = new ApiFactory(ReadOnly);
        var propertyId = await SeedPropertyAsync(factory, type: PropertyType.Vehicle);
        await SeedEventAsync(factory, propertyId, ContextPropertyEventType.TyreChange, ContextEventSource.User, "Winter tyres", FixedNow.AddDays(-5));
        await SeedEventAsync(factory, propertyId, ContextPropertyEventType.Serviced, ContextEventSource.User, "Service", FixedNow.AddDays(-50));
        await SeedEventAsync(factory, propertyId, ContextPropertyEventType.Acquired, ContextEventSource.System, "Property acquired", FixedNow.AddDays(-500));
        using var client = factory.CreateClient();

        async Task<List<string>> Titles(string query) =>
            [.. (await client.GetPagedItemsAsync<ExistingPropertyEvent>($"{Events(propertyId)}?{query}")).Select(e => e.Title)];

        Assert.Equal(["Winter tyres", "Service", "Property acquired"], await Titles(""));
        Assert.Equal(["Service"], await Titles("search=serv"));
        Assert.Equal(["Winter tyres"], await Titles($"types={(int)PropertyEventType.TyreChange}"));
        Assert.Equal(["Winter tyres", "Service"], await Titles($"from={FixedNow.AddDays(-60):O}"));
        Assert.Equal(["Property acquired"], await Titles($"to={FixedNow.AddDays(-100):O}"));
        Assert.Equal(["Property acquired"], await Titles("source=System"));
        Assert.Equal(["Property acquired", "Service", "Winter tyres"], await Titles("sortBy=Title"));
        Assert.Equal(["Property acquired", "Service", "Winter tyres"], await Titles("sortBy=Type"));
        Assert.Equal(3, (await Titles("sortBy=CreatedAtUtc")).Count);
        Assert.Equal(["Property acquired"], await Titles("sortBy=OccurredAt&sortDir=asc&limit=1"));
        Assert.Equal(["Service"], await Titles("offset=1&limit=1"));

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{Events(propertyId)}?limit=100000")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{Events(propertyId)}?sortBy=Nope")).StatusCode);
    }

    // ── Claims (AC 17) ───────────────────────────────────────────────────────

    /// <summary>
    /// AC 17 — the Guest role's real claim set: it lists, and every write is a 403.
    /// </summary>
    [Fact]
    public async Task Guest_CanList_AndIsForbiddenEveryWrite()
    {
        await using var factory = new ApiFactory(RolePermissions.GuestClaims);
        var propertyId = await SeedPropertyAsync(factory);
        var eventId = await SeedEventAsync(factory, propertyId, ContextPropertyEventType.Repair, ContextEventSource.User);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Events(propertyId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Events(propertyId), Body())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"{Events(propertyId)}/{eventId}", Body())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"{Events(propertyId)}/{eventId}")).StatusCode);
    }

    [Fact]
    public async Task WithoutPropertiesRead_TheListIs403()
    {
        await using var factory = new ApiFactory(
            [.. RolePermissions.AllClaims.Where(c => c is not (PermissionClaims.PropertiesRead or PermissionClaims.PropertiesUpdate))]);
        var propertyId = await SeedPropertyAsync(factory);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Events(propertyId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Events(propertyId), Body())).StatusCode);
    }

    // ── System events through the property endpoints (AC 10, 11, 15) ─────────

    [Fact]
    public async Task PutProperty_Archiving_WritesOneArchivedEvent_AttributedToTheCaller()
    {
        await using var factory = new ApiFactory(ReadWrite);
        // The store first, so the reference currencies are seeded before the actor row creates it.
        await EnsureCreatedAsync(factory);
        await factory.SeedActorUserAsync(displayName: "Kari Nordmann");
        var propertyId = await SeedPropertyAsync(factory);
        using var client = factory.CreateClient();
        var body = RealEstateBody();
        body.Archived = true;

        var put = await client.PutAsJsonAsync(PropertyPath(propertyId), body);
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());

        var e = Assert.Single(await client.GetPagedItemsAsync<ExistingPropertyEvent>(Events(propertyId)));
        Assert.Equal(PropertyEventType.Archived, e.Type);
        Assert.Equal(ContractEventSource.System, e.Source);
        Assert.Equal("Kari Nordmann", e.CreatedBy);
        Assert.Equal(FixedNow, e.OccurredAt);
    }

    [Fact]
    public async Task PostProperty_WithAnAcquiredDate_WritesOneAcquiredEvent()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await EnsureCreatedAsync(factory);
        using var client = factory.CreateClient();
        var body = VehicleBody();
        body.AcquiredDate = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        var created = (await (await client.PostAsJsonAsync(PropertiesPath, body)).Content.ReadFromJsonAsync<ExistingProperty>())!;

        var e = Assert.Single(await client.GetPagedItemsAsync<ExistingPropertyEvent>(Events(created.PropertyId)));
        Assert.Equal(PropertyEventType.Acquired, e.Type);
        Assert.Equal(body.AcquiredDate, e.OccurredAt);
    }

    [Fact]
    public async Task DeleteProperty_CascadesItsEvents()
    {
        await using var factory = new ApiFactory([.. ReadWrite, PermissionClaims.PropertiesDelete]);
        var propertyId = await SeedPropertyAsync(factory);
        var survivor = await SeedPropertyAsync(factory, name: "Survivor");
        await SeedEventAsync(factory, propertyId, ContextPropertyEventType.Repair, ContextEventSource.User);
        await SeedEventAsync(factory, survivor, ContextPropertyEventType.Repair, ContextEventSource.User);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(PropertyPath(propertyId))).StatusCode);

        var remaining = await ReadAsync(factory, c => c.PropertyEvents.AsNoTracking().ToListAsync());
        Assert.Equal(survivor, Assert.Single(remaining).PropertyId);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<Guid> SeedEventAsync(
        OdysseyApiFactory factory, Guid propertyId, ContextPropertyEventType type, ContextEventSource source,
        string title = "Seeded", DateTime? occurredAt = null)
    {
        var entity = new PropertyEvent
        {
            PropertyId = propertyId,
            Type = type,
            Source = source,
            Title = title,
            OccurredAt = occurredAt ?? FixedNow.AddDays(-3),
            CreatedAtUtc = (occurredAt ?? FixedNow.AddDays(-3)).AddHours(1),
        };
        await ReadAsync(factory, async c =>
        {
            c.PropertyEvents.Add(entity);
            return await c.SaveChangesAsync();
        });
        return entity.EventId;
    }

    private static async Task<(Guid ContractId, Guid EventId)> SeedContractEventAsync(OdysseyApiFactory factory)
    {
        var contract = new Contract { Name = "Lease", Type = Odyssey.Context.ContractType.Rental, CreatedAtUtc = FixedNow };
        var entity = new ContractEvent { ContractId = contract.ContractId, Title = "Contract row", OccurredAt = FixedNow.AddDays(-1), CreatedAtUtc = FixedNow };
        await ReadAsync(factory, async c =>
        {
            c.Contracts.Add(contract);
            entity.ContractId = contract.ContractId;
            c.ContractEvents.Add(entity);
            return await c.SaveChangesAsync();
        });
        return (contract.ContractId, entity.EventId);
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(
            permissions,
            ActorUserId,
            configuration: null,
            configureServices: services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedTimeProvider(FixedNow));
            });

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
