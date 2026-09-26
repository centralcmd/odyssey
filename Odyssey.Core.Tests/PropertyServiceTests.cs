using Odyssey.Core;
using Odyssey.Core.Finance;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// Fast coverage for <see cref="PropertyService"/> (issue #167): the subtype invariant, the
/// immutability of <c>Type</c>, normalization, the derived status, cascade-on-delete under the EF
/// InMemory provider, and the list's filters and sorts — the value sort's nulls-last rule included.
/// </summary>
public class PropertyServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static PropertyService Service(Odyssey.Context.OdysseyContext context) =>
        new(context, new FixedTimeProvider(Now));

    [Fact]
    public async Task Create_RealEstate_RoundTripsEveryDetailField_AndNormalizesTheCountryCode()
    {
        await using var context = TestContextFactory.Create();
        var service = Service(context);

        var created = await service.Create(PropertyTestData.House());
        var read = await service.Get(created.PropertyId);

        Assert.NotNull(read);
        Assert.Equal(PropertyType.RealEstate, read!.Type);
        Assert.Equal(PropertyStatus.Owned, read.Status);
        Assert.Null(read.VehicleDetails);
        var details = read.RealEstateDetails!;
        Assert.Equal(RealEstateKind.House, details.Kind);
        Assert.Equal("Storgata 14", details.AddressLine);
        Assert.Equal("0155", details.PostalCode);
        Assert.Equal("Oslo", details.City);
        Assert.Equal("NO", details.CountryCode);
        Assert.Equal("208/451", details.CadastralNumber);
        Assert.Equal(142.5m, details.LivingAreaSqm);
        Assert.Equal(410m, details.PlotAreaSqm);
        Assert.Equal(1968, details.BuildYear);
        Assert.Equal(Now.UtcDateTime, read.CreatedAt);
    }

    [Fact]
    public async Task Create_Vehicle_UppercasesAndStripsTheIdentifiers()
    {
        await using var context = TestContextFactory.Create();
        var service = Service(context);

        var created = await service.Create(PropertyTestData.Car());

        Assert.Null(created.RealEstateDetails);
        Assert.Equal("EL12345", created.VehicleDetails!.RegistrationNumber);
        Assert.Equal("YV1XZ00000A000001", created.VehicleDetails.Vin);
        Assert.Equal(1, context.VehicleDetails.Count());
        Assert.Equal(0, context.RealEstateDetails.Count());
    }

    [Fact]
    public async Task Create_WrongOrDoubledDetails_IsRejectedForDirectCallers()
    {
        await using var context = TestContextFactory.Create();
        var service = Service(context);

        var wrong = PropertyTestData.House() with { RealEstateDetails = null, VehicleDetails = PropertyTestData.Car().VehicleDetails };
        var both = PropertyTestData.House() with { VehicleDetails = PropertyTestData.Car().VehicleDetails };

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(wrong));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(both));
        Assert.Empty(context.Properties);
    }

    [Fact]
    public async Task Create_UnsupportedCurrency_IsRejected()
    {
        await using var context = TestContextFactory.Create();

        await Assert.ThrowsAsync<DomainValidationException>(
            () => Service(context).Create(PropertyTestData.House(currency: "ZZZ")));
    }

    [Fact]
    public async Task Create_FutureBuildYear_AndFarFutureModelYear_AreRejected()
    {
        await using var context = TestContextFactory.Create();
        var service = Service(context);

        var house = PropertyTestData.House();
        house.RealEstateDetails!.BuildYear = Now.Year + 1;
        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(house));

        var car = PropertyTestData.Car();
        car.VehicleDetails!.ModelYear = Now.Year + 2;
        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(car));

        // Next year's model is on sale this year, so exactly one year ahead is accepted.
        car.VehicleDetails.ModelYear = Now.Year + 1;
        await service.Create(car);
    }

    [Fact]
    public async Task Create_DisposalBeforeAcquisition_IsRejected()
    {
        await using var context = TestContextFactory.Create();
        var house = PropertyTestData.House();
        house.DisposedDate = house.AcquiredDate!.Value.AddDays(-1);

        await Assert.ThrowsAsync<DomainValidationException>(() => Service(context).Create(house));
    }

    [Fact]
    public async Task Update_ChangingTheType_Is422_AndLeavesThePropertyUntouched()
    {
        await using var context = TestContextFactory.Create();
        var service = Service(context);
        var created = await service.Create(PropertyTestData.House());

        var retyped = PropertyTestData.Car("Renamed");
        var error = await Assert.ThrowsAsync<DomainUnprocessableException>(
            () => service.Update(created.PropertyId, retyped));

        Assert.Contains(nameof(NewProperty.Type), error.Errors!.Keys);
        var read = await service.Get(created.PropertyId);
        Assert.Equal("Storgata 14", read!.Name);
        Assert.NotNull(read.RealEstateDetails);
        Assert.Equal(0, context.VehicleDetails.Count());
    }

    [Fact]
    public async Task Update_SameType_ReplacesEveryField()
    {
        await using var context = TestContextFactory.Create();
        var service = Service(context);
        var created = await service.Create(PropertyTestData.House());

        var put = PropertyTestData.House("Storgata 16");
        put.RealEstateDetails!.City = "Bergen";
        put.Archived = true;
        var updated = await service.Update(created.PropertyId, put);

        Assert.Equal("Storgata 16", updated!.Name);
        Assert.Equal("Bergen", updated.RealEstateDetails!.City);
        Assert.Equal(PropertyStatus.Archived, updated.Status);
        Assert.Equal(1, context.RealEstateDetails.Count());
    }

    [Fact]
    public async Task Update_UnknownId_ReturnsNull_AndCreatesNothing()
    {
        await using var context = TestContextFactory.Create();

        Assert.Null(await Service(context).Update(Guid.NewGuid(), PropertyTestData.House()));
        Assert.Empty(context.Properties);
    }

    [Fact]
    public async Task Update_CurrencyChangeWithEstimates_IsRejected()
    {
        await using var context = TestContextFactory.Create();
        var service = Service(context);
        var created = await service.Create(PropertyTestData.House());
        await new PropertyEstimateService(context).Create(created.PropertyId, new NewPropertyEstimate
        {
            Value = 1m,
            EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Update(created.PropertyId, PropertyTestData.House(currency: "EUR")));
    }

    [Fact]
    public async Task Delete_CascadesDetailsEstimatesAndSmartTags_ButNotTheTag()
    {
        await using var context = TestContextFactory.Create();
        var service = Service(context);
        var created = await service.Create(PropertyTestData.House());
        await new PropertyEstimateService(context).Create(created.PropertyId, new NewPropertyEstimate
        {
            Value = 1m,
            EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        var tag = new Odyssey.Context.TransactionTag { Name = "Maintenance" };
        context.TransactionTags.Add(tag);
        await context.SaveChangesAsync();
        await new PropertySmartTagService(context, new FakePropertyLimitsLookup())
            .AddSmartTag(created.PropertyId, tag.TransactionTagId);

        Assert.True(await service.Delete(created.PropertyId, userId: null));

        Assert.Empty(context.Properties);
        Assert.Empty(context.RealEstateDetails);
        Assert.Empty(context.PropertyEstimates);
        Assert.Empty(context.PropertySmartTags);
        Assert.Single(context.TransactionTags);
        Assert.False(await service.Delete(created.PropertyId, userId: null));
    }

    [Theory]
    [InlineData(null, null, PropertyStatus.Owned)]
    [InlineData(null, -1, PropertyStatus.Disposed)]
    [InlineData(null, 0, PropertyStatus.Disposed)]
    [InlineData(null, 1, PropertyStatus.Owned)]
    [InlineData(-5, -1, PropertyStatus.Archived)]
    public void DeriveStatus_ArchivedWins_ThenAPastDisposal(int? archivedDaysAgo, int? disposedOffsetDays, PropertyStatus expected)
    {
        var now = Now.UtcDateTime;
        DateTime? archived = archivedDaysAgo is { } a ? now.AddDays(a) : null;
        DateTime? disposed = disposedOffsetDays is { } d ? now.AddDays(d) : null;

        Assert.Equal(expected, PropertyService.DeriveStatus(archived, disposed, now));
    }

    [Fact]
    public async Task List_FiltersBySearchTypeAndStatus_AndCountsBeforeTheWindow()
    {
        await using var context = TestContextFactory.Create();
        var service = Service(context);
        await service.Create(PropertyTestData.House("Alpha house"));
        await service.Create(PropertyTestData.House("Beta house"));
        var sold = PropertyTestData.Car("Old car");
        sold.DisposedDate = Now.UtcDateTime.AddDays(-10);
        await service.Create(sold);
        var archived = PropertyTestData.Car("Boat-ish");
        archived.Archived = true;
        await service.Create(archived);

        var vehicles = await service.ListAsync(new PropertiesQueryParams { Types = [PropertyType.Vehicle] });
        Assert.Equal(2, vehicles.TotalCount);

        var disposed = await service.ListAsync(new PropertiesQueryParams { Statuses = [PropertyStatus.Disposed] });
        Assert.Equal("Old car", Assert.Single(disposed.Items).Name);

        var owned = await service.ListAsync(new PropertiesQueryParams { Statuses = [PropertyStatus.Owned] });
        Assert.Equal(["Alpha house", "Beta house"], owned.Items.Select(p => p.Name));

        // A detail field is searchable, not only the base columns.
        var byCity = await service.ListAsync(new PropertiesQueryParams { Search = "oslo" });
        Assert.Equal(2, byCity.TotalCount);

        var window = await service.ListAsync(new PropertiesQueryParams { Offset = 1, Limit = 1 });
        Assert.Equal(4, window.TotalCount);
        Assert.Single(window.Items);
    }

    [Theory]
    [InlineData(SortDirection.Asc)]
    [InlineData(SortDirection.Desc)]
    public async Task List_SortByValue_PutsPropertiesWithoutAnEstimateLast_InBothDirections(SortDirection direction)
    {
        await using var context = TestContextFactory.Create();
        var service = Service(context);
        var estimates = new PropertyEstimateService(context, new FixedTimeProvider(Now));
        var cheap = await service.Create(PropertyTestData.House("Cheap"));
        var dear = await service.Create(PropertyTestData.House("Dear"));
        await service.Create(PropertyTestData.House("Unvalued"));
        var past = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await estimates.Create(cheap.PropertyId, new NewPropertyEstimate { Value = 100m, EffectiveFrom = past });
        await estimates.Create(dear.PropertyId, new NewPropertyEstimate { Value = 900m, EffectiveFrom = past });
        // Older and superseded by the 100 entry above — a MAX(value) or first-created shortcut would sort "Cheap" last.
        await estimates.Create(cheap.PropertyId, new NewPropertyEstimate { Value = 5000m, EffectiveFrom = past.AddDays(-1) });
        // Not yet in force — ignored by the as-of-now resolution.
        await estimates.Create(cheap.PropertyId, new NewPropertyEstimate { Value = 99999m, EffectiveFrom = Now.UtcDateTime.AddYears(1) });

        var page = await service.ListAsync(new PropertiesQueryParams { SortBy = PropertySortBy.Value, SortDir = direction });

        var names = page.Items.Select(p => p.Name).ToList();
        Assert.Equal(direction == SortDirection.Asc ? ["Cheap", "Dear", "Unvalued"] : ["Dear", "Cheap", "Unvalued"], names);
    }

    [Fact]
    public async Task List_DefaultSort_IsNameAscending()
    {
        await using var context = TestContextFactory.Create();
        var service = Service(context);
        await service.Create(PropertyTestData.House("Charlie"));
        await service.Create(PropertyTestData.House("Alpha"));
        await service.Create(PropertyTestData.Car("Bravo"));

        var page = await service.ListAsync(new PropertiesQueryParams());

        Assert.Equal(["Alpha", "Bravo", "Charlie"], page.Items.Select(p => p.Name));
    }
}
