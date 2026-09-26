using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Finance;
using Xunit;
using ContextPropertyEventType = Odyssey.Context.PropertyEventType;
using ContextEventSource = Odyssey.Context.ContractEventSource;
using DtoPropertyEventType = Odyssey.Dtos.Finance.PropertyEventType;
using DtoEventSource = Odyssey.Dtos.Finance.ContractEventSource;

namespace Odyssey.Core.Tests;

/// <summary>
/// Fast coverage for <see cref="PropertyEventService"/> (issue #209): the matrix and system-only
/// rules, the shared field rules, owner scoping across the shared table, and the list surface.
/// </summary>
public class PropertyEventServiceTests
{
    private static readonly DateTime FixedNow = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);
    private const string Author = "property-events-author";

    private readonly OdysseyContext context = TestContextFactory.Create();

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private PropertyEventService Service() => new(context, new FixedTimeProvider(FixedNow));

    private async Task<Guid> SeedPropertyAsync(PropertyType type = PropertyType.RealEstate)
    {
        var property = new Property
        {
            Name = type == PropertyType.RealEstate ? "House" : "Car",
            Description = "d",
            Type = type,
            CurrencyCode = "USD",
            CreatedAt = FixedNow,
            UpdatedAt = FixedNow,
        };
        if (type == PropertyType.RealEstate)
            property.RealEstateDetails = new RealEstateDetails { Kind = RealEstateKind.House };
        else
            property.VehicleDetails = new VehicleDetails { Kind = VehicleKind.Car };

        context.Properties.Add(property);
        await context.SaveChangesAsync();
        return property.PropertyId;
    }

    private static NewPropertyEvent New(
        DtoPropertyEventType type = DtoPropertyEventType.Other,
        string title = "Something happened",
        DateTime? occurredAt = null,
        string? description = null,
        string? notes = null) => new()
    {
        Type = type,
        Title = title,
        Description = description,
        Notes = notes,
        OccurredAt = occurredAt ?? FixedNow.AddDays(-1),
    };

    private static UpdatePropertyEvent Update(
        DtoPropertyEventType type = DtoPropertyEventType.Other,
        string title = "Edited",
        DateTime? occurredAt = null) => new()
    {
        Type = type,
        Title = title,
        OccurredAt = occurredAt ?? FixedNow.AddDays(-2),
    };

    // ── Create ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_StampsSourceAuthorAndClock_AndTrimsFreeText()
    {
        var propertyId = await SeedPropertyAsync();

        var read = await Service().CreateAsync(
            propertyId, New(DtoPropertyEventType.Repair, "  Fixed the gutter  ", description: "   ", notes: " n "), Author);

        Assert.NotNull(read);
        Assert.Equal(Author, read!.AuthorId);
        var e = read.Event;
        Assert.Equal(propertyId, e.PropertyId);
        Assert.Equal(DtoPropertyEventType.Repair, e.Type);
        Assert.Equal(DtoEventSource.User, e.Source);
        Assert.Equal("Fixed the gutter", e.Title);
        Assert.Null(e.Description);
        Assert.Equal("n", e.Notes);
        Assert.Equal(FixedNow, e.CreatedAtUtc);
    }

    [Fact]
    public async Task Create_OnAnUnknownProperty_ReturnsNull()
    {
        Assert.Null(await Service().CreateAsync(Guid.NewGuid(), New(), Author));
    }

    /// <summary>AC 4 — a vehicle-only type is legal on a vehicle and a 422 on real estate.</summary>
    [Fact]
    public async Task Create_TypeSpecificMember_FollowsTheMatrix()
    {
        var car = await SeedPropertyAsync(PropertyType.Vehicle);
        var house = await SeedPropertyAsync(PropertyType.RealEstate);

        Assert.NotNull(await Service().CreateAsync(car, New(DtoPropertyEventType.TyreChange), Author));

        var refused = await Assert.ThrowsAsync<DomainUnprocessableException>(
            () => Service().CreateAsync(house, New(DtoPropertyEventType.TyreChange), Author));
        Assert.Contains("type", refused.Errors!.Keys);
        Assert.Equal(1, await context.PropertyEvents.CountAsync());
    }

    /// <summary>AC 5 — a system-only member is refused on create.</summary>
    [Theory]
    [InlineData(DtoPropertyEventType.Archived)]
    [InlineData(DtoPropertyEventType.Unarchived)]
    [InlineData(DtoPropertyEventType.AcquisitionDateCleared)]
    [InlineData(DtoPropertyEventType.DisposalReversed)]
    public async Task Create_SystemOnlyType_Is422KeyedType(DtoPropertyEventType type)
    {
        var propertyId = await SeedPropertyAsync();

        var refused = await Assert.ThrowsAsync<DomainUnprocessableException>(
            () => Service().CreateAsync(propertyId, New(type), Author));

        Assert.Contains("type", refused.Errors!.Keys);
        Assert.Empty(context.PropertyEvents);
    }

    /// <summary>AC 9 — 59 s ahead is accepted; 61 s ahead is a 422 keyed occurredAt.</summary>
    [Fact]
    public async Task Create_FutureBound_UsesTheSharedTolerance()
    {
        var propertyId = await SeedPropertyAsync();

        Assert.NotNull(await Service().CreateAsync(propertyId, New(occurredAt: FixedNow.AddSeconds(59)), Author));

        var refused = await Assert.ThrowsAsync<DomainUnprocessableException>(
            () => Service().CreateAsync(propertyId, New(occurredAt: FixedNow.AddSeconds(61)), Author));
        Assert.Contains("occurredAt", refused.Errors!.Keys);
        Assert.Equal(ContractEventService.FutureTolerance, EventFieldRules.FutureTolerance);
    }

    [Fact]
    public async Task Create_WhitespaceTitle_Is400KeyedTitle()
    {
        var propertyId = await SeedPropertyAsync();

        var refused = await Assert.ThrowsAsync<DomainValidationException>(
            () => Service().CreateAsync(propertyId, New(title: "   "), Author));

        Assert.Contains("title", refused.Errors!.Keys);
    }

    /// <summary>AC 3a, service half — an explicit Other (108) is what is persisted, never the column default.</summary>
    [Fact]
    public async Task Create_Other_PersistsOneHundredAndEight()
    {
        var propertyId = await SeedPropertyAsync();

        await Service().CreateAsync(propertyId, New(DtoPropertyEventType.Other), Author);

        var stored = await context.PropertyEvents.AsNoTracking().SingleAsync();
        Assert.Equal(ContextPropertyEventType.Other, stored.Type);
        Assert.Equal(108, (int)stored.Type);
    }

    // ── Update ───────────────────────────────────────────────────────────────

    /// <summary>AC 5 — a system row may keep its system-only type through a PUT, and stays System.</summary>
    [Fact]
    public async Task Update_OnASystemRow_MayKeepItsSystemOnlyType_AndSourceStaysSystem()
    {
        var propertyId = await SeedPropertyAsync();
        var system = await SeedSystemEventAsync(propertyId, ContextPropertyEventType.Archived);

        var updated = await Service().UpdateAsync(
            propertyId, system, Update(DtoPropertyEventType.Archived, "Archived — sold the house"));

        Assert.NotNull(updated);
        Assert.Equal(DtoPropertyEventType.Archived, updated!.Event.Type);
        Assert.Equal(DtoEventSource.System, updated.Event.Source);
        Assert.Equal("Archived — sold the house", updated.Event.Title);
    }

    [Fact]
    public async Task Update_CannotIntroduceADifferentSystemOnlyType()
    {
        var propertyId = await SeedPropertyAsync();
        var system = await SeedSystemEventAsync(propertyId, ContextPropertyEventType.Archived);
        var user = (await Service().CreateAsync(propertyId, New(), Author))!.Event.PropertyEventId;

        await Assert.ThrowsAsync<DomainUnprocessableException>(
            () => Service().UpdateAsync(propertyId, user, Update(DtoPropertyEventType.Archived)));
        await Assert.ThrowsAsync<DomainUnprocessableException>(
            () => Service().UpdateAsync(propertyId, system, Update(DtoPropertyEventType.Unarchived)));
    }

    [Fact]
    public async Task Update_IsAFullReplacement_AndNeverRewritesAttribution()
    {
        var propertyId = await SeedPropertyAsync();
        var created = (await Service().CreateAsync(
            propertyId, New(DtoPropertyEventType.Repair, description: "d", notes: "n"), Author))!;

        var later = new PropertyEventService(context, new FixedTimeProvider(FixedNow.AddDays(3)));
        var updated = (await later.UpdateAsync(propertyId, created.Event.PropertyEventId, Update()))!;

        Assert.Equal(DtoPropertyEventType.Other, updated.Event.Type);
        Assert.Null(updated.Event.Description);
        Assert.Null(updated.Event.Notes);
        Assert.Equal(Author, updated.AuthorId);
        Assert.Equal(FixedNow, updated.Event.CreatedAtUtc);
    }

    /// <summary>AC 8 — another property's event and a contract's event are both "not on this property".</summary>
    [Fact]
    public async Task UpdateAndDelete_AcrossOwners_ReturnNotFound_AndTouchNothing()
    {
        var a = await SeedPropertyAsync();
        var b = await SeedPropertyAsync();
        var onB = (await Service().CreateAsync(b, New(title: "On B"), Author))!.Event.PropertyEventId;
        var contractEvent = await SeedContractEventAsync();

        Assert.Null(await Service().UpdateAsync(a, onB, Update(title: "Hijacked")));
        Assert.Null(await Service().UpdateAsync(a, contractEvent, Update(title: "Hijacked")));
        Assert.False(await Service().DeleteAsync(a, onB));
        Assert.False(await Service().DeleteAsync(a, contractEvent));

        Assert.Equal("On B", (await context.PropertyEvents.AsNoTracking().SingleAsync()).Title);
        Assert.Equal("Contract row", (await context.ContractEvents.AsNoTracking().SingleAsync()).Title);
    }

    [Fact]
    public async Task Delete_RemovesASystemRowToo()
    {
        var propertyId = await SeedPropertyAsync();
        var system = await SeedSystemEventAsync(propertyId, ContextPropertyEventType.Acquired);

        Assert.True(await Service().DeleteAsync(propertyId, system));
        Assert.Empty(context.PropertyEvents);
    }

    // ── List (AC 18) ─────────────────────────────────────────────────────────

    [Fact]
    public async Task List_IsPropertyScoped_AndNeverReturnsContractRows()
    {
        var a = await SeedPropertyAsync();
        var b = await SeedPropertyAsync();
        await Service().CreateAsync(a, New(title: "On A"), Author);
        await Service().CreateAsync(b, New(title: "On B"), Author);
        await SeedContractEventAsync();

        var page = (await Service().ListAsync(a, new PropertyEventsQueryParams()))!;

        Assert.Equal("On A", Assert.Single(page.Page.Items).Title);
        Assert.Equal(1, page.Page.TotalCount);
    }

    [Fact]
    public async Task List_OnAnUnknownProperty_ReturnsNull()
    {
        Assert.Null(await Service().ListAsync(Guid.NewGuid(), new PropertyEventsQueryParams()));
    }

    [Fact]
    public async Task List_FiltersBySearchTypesWindowAndSource()
    {
        var propertyId = await SeedPropertyAsync(PropertyType.Vehicle);
        await Service().CreateAsync(propertyId, New(DtoPropertyEventType.TyreChange, "Winter tyres", FixedNow.AddDays(-10), notes: "studded"), Author);
        await Service().CreateAsync(propertyId, New(DtoPropertyEventType.Serviced, "Service", FixedNow.AddDays(-40)), Author);
        await SeedSystemEventAsync(propertyId, ContextPropertyEventType.Acquired, FixedNow.AddDays(-400));

        async Task<List<string>> Titles(PropertyEventsQueryParams q) =>
            [.. (await Service().ListAsync(propertyId, q))!.Page.Items.Select(i => i.Title)];

        Assert.Equal(["Winter tyres"], await Titles(new() { Search = "studded" }));
        Assert.Equal(["Service"], await Titles(new() { Types = [DtoPropertyEventType.Serviced] }));
        Assert.Equal(["Winter tyres", "Service"], await Titles(new() { From = FixedNow.AddDays(-50) }));
        Assert.Equal(["Property acquired"], await Titles(new() { To = FixedNow.AddDays(-100) }));
        Assert.Equal(["Property acquired"], await Titles(new() { Source = DtoEventSource.System }));
        Assert.Equal(["Winter tyres", "Service"], await Titles(new() { Source = DtoEventSource.User }));
    }

    [Fact]
    public async Task List_SortsByEveryKey_AndPages()
    {
        var propertyId = await SeedPropertyAsync();
        await Service().CreateAsync(propertyId, New(DtoPropertyEventType.Repair, "B repair", FixedNow.AddDays(-1)), Author);
        await Service().CreateAsync(propertyId, New(DtoPropertyEventType.Acquired, "C acquired", FixedNow.AddDays(-3)), Author);
        await Service().CreateAsync(propertyId, New(DtoPropertyEventType.Valued, "A valued", FixedNow.AddDays(-2)), Author);

        async Task<List<string>> Titles(PropertyEventsQueryParams q) =>
            [.. (await Service().ListAsync(propertyId, q))!.Page.Items.Select(i => i.Title)];

        Assert.Equal(["B repair", "A valued", "C acquired"], await Titles(new()));
        Assert.Equal(["A valued", "B repair", "C acquired"], await Titles(new() { SortBy = PropertyEventSortBy.Title }));
        Assert.Equal(["C acquired", "A valued", "B repair"], await Titles(new() { SortBy = PropertyEventSortBy.Type }));
        Assert.Equal(3, (await Titles(new() { SortBy = PropertyEventSortBy.CreatedAtUtc })).Count);

        var page = (await Service().ListAsync(propertyId, new PropertyEventsQueryParams { Offset = 1, Limit = 1 }))!;
        Assert.Equal("A valued", Assert.Single(page.Page.Items).Title);
        Assert.Equal(3, page.Page.TotalCount);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<Guid> SeedSystemEventAsync(
        Guid propertyId, ContextPropertyEventType type, DateTime? occurredAt = null)
    {
        var entity = new PropertyEvent
        {
            PropertyId = propertyId,
            Type = type,
            Source = ContextEventSource.System,
            Title = type == ContextPropertyEventType.Acquired ? "Property acquired" : "Property archived",
            OccurredAt = occurredAt ?? FixedNow.AddDays(-5),
            CreatedAtUtc = FixedNow,
        };
        context.PropertyEvents.Add(entity);
        await context.SaveChangesAsync();
        return entity.EventId;
    }

    private async Task<Guid> SeedContractEventAsync()
    {
        var contract = new Contract { Name = "Lease", Type = Odyssey.Context.ContractType.Rental, CreatedAtUtc = FixedNow };
        context.Contracts.Add(contract);
        var entity = new ContractEvent
        {
            ContractId = contract.ContractId,
            Title = "Contract row",
            OccurredAt = FixedNow.AddDays(-1),
            CreatedAtUtc = FixedNow,
        };
        context.ContractEvents.Add(entity);
        await context.SaveChangesAsync();
        return entity.EventId;
    }
}
