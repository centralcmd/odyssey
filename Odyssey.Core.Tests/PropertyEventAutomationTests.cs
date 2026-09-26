using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Finance;
using Xunit;
using ContextPropertyEventType = Odyssey.Context.PropertyEventType;
using ContextEventSource = Odyssey.Context.ContractEventSource;

namespace Odyssey.Core.Tests;

/// <summary>
/// Issue #209 §8.5 and AC 10–15 — the system events <see cref="PropertyService"/> records for its
/// three lifecycle fields, staged in the same save as the change.
/// </summary>
public class PropertyEventAutomationTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    private const string Actor = "property-automation-actor";

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }

    private static PropertyService Service(OdysseyContext context) => new(context, new FixedTimeProvider(Now));

    private static List<PropertyEvent> Events(OdysseyContext context, Guid propertyId) =>
        [.. context.PropertyEvents.AsNoTracking().Where(e => e.PropertyId == propertyId).OrderBy(e => e.Type)];

    /// <summary>AC 11 — a create with an acquired date writes one Acquired row at min(date, now).</summary>
    [Fact]
    public async Task Create_WithAnAcquiredDate_WritesOneAcquiredEvent_AttributedToTheCaller()
    {
        await using var context = TestContextFactory.Create();

        var created = await Service(context).Create(PropertyTestData.House(), Actor);

        var e = Assert.Single(Events(context, created.PropertyId));
        Assert.Equal(ContextPropertyEventType.Acquired, e.Type);
        Assert.Equal(ContextEventSource.System, e.Source);
        Assert.Equal(new DateTime(2019, 6, 1, 0, 0, 0, DateTimeKind.Utc), e.OccurredAt);
        Assert.Equal("Property acquired", e.Title);
        Assert.Equal("Acquired on 1 June 2019.", e.Description);
        Assert.Null(e.Notes);
        Assert.Equal(Actor, e.CreatedByUserId);
        Assert.Equal(Now, e.CreatedAtUtc);
    }

    [Fact]
    public async Task Create_WithAFutureAcquiredDate_ClampsTheEventToNow()
    {
        await using var context = TestContextFactory.Create();
        var body = PropertyTestData.House();
        body.AcquiredDate = Now.AddDays(10);

        var created = await Service(context).Create(body, Actor);

        Assert.Equal(Now, Assert.Single(Events(context, created.PropertyId)).OccurredAt);
    }

    [Fact]
    public async Task Create_WithNoLifecycleFields_WritesNothing()
    {
        await using var context = TestContextFactory.Create();

        var created = await Service(context).Create(PropertyTestData.Car(), Actor);

        Assert.Empty(Events(context, created.PropertyId));
    }

    [Fact]
    public async Task Create_ArchivedAndDisposed_WritesEachTransition()
    {
        await using var context = TestContextFactory.Create();
        var body = PropertyTestData.House();
        body.DisposedDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        body.Archived = true;

        var created = await Service(context).Create(body, Actor);

        Assert.Equal(
            [ContextPropertyEventType.Acquired, ContextPropertyEventType.Disposed, ContextPropertyEventType.Archived],
            Events(context, created.PropertyId).Select(e => e.Type));
    }

    /// <summary>AC 10 — archiving through PUT writes exactly one Archived row stamped at the archive moment.</summary>
    [Fact]
    public async Task Update_Archiving_WritesExactlyOneArchivedEvent()
    {
        await using var context = TestContextFactory.Create();
        var created = await Service(context).Create(PropertyTestData.Car(), userId: null);
        var body = PropertyTestData.Car();
        body.Archived = true;

        await Service(context).Update(created.PropertyId, body, Actor);
        // Re-sending the same state is not a transition.
        await Service(context).Update(created.PropertyId, body, Actor);

        var e = Assert.Single(Events(context, created.PropertyId));
        Assert.Equal(ContextPropertyEventType.Archived, e.Type);
        Assert.Equal(Now, e.OccurredAt);
        Assert.Equal("Property archived", e.Title);
        Assert.Equal(Actor, e.CreatedByUserId);
    }

    [Fact]
    public async Task Update_Unarchiving_WritesUnarchived()
    {
        await using var context = TestContextFactory.Create();
        var body = PropertyTestData.Car();
        body.Archived = true;
        var created = await Service(context).Create(body, userId: null);

        await Service(context).Update(created.PropertyId, PropertyTestData.Car(), Actor);

        Assert.Equal(
            [ContextPropertyEventType.Archived, ContextPropertyEventType.Unarchived],
            Events(context, created.PropertyId).Select(e => e.Type));
    }

    /// <summary>AC 11 — re-dating a non-null acquired date writes nothing.</summary>
    [Fact]
    public async Task Update_ReDatingAnAcquiredDate_WritesNothing()
    {
        await using var context = TestContextFactory.Create();
        var created = await Service(context).Create(PropertyTestData.House(), Actor);
        var body = PropertyTestData.House();
        body.AcquiredDate = new DateTime(2018, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await Service(context).Update(created.PropertyId, body, Actor);

        Assert.Single(Events(context, created.PropertyId));
    }

    /// <summary>AC 12 — clearing either date writes its reversal, at the server clock.</summary>
    [Fact]
    public async Task Update_ClearingTheDates_WritesTheirReversals()
    {
        await using var context = TestContextFactory.Create();
        var body = PropertyTestData.House();
        body.DisposedDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var created = await Service(context).Create(body, Actor);

        var cleared = PropertyTestData.House();
        cleared.AcquiredDate = null;
        cleared.DisposedDate = null;
        await Service(context).Update(created.PropertyId, cleared, Actor);

        var reversals = Events(context, created.PropertyId)
            .Where(e => e.Type is ContextPropertyEventType.AcquisitionDateCleared or ContextPropertyEventType.DisposalReversed)
            .ToList();
        Assert.Equal(2, reversals.Count);
        Assert.All(reversals, e => Assert.Equal(Now, e.OccurredAt));
        Assert.Contains(reversals, e => e.Title == "Disposal reversed");
        Assert.Contains(reversals, e => e.Title == "Acquired date cleared");
    }

    /// <summary>A refused write stages nothing — the detector runs after every validation.</summary>
    [Fact]
    public async Task Update_RefusedByValidation_WritesNoEvent()
    {
        await using var context = TestContextFactory.Create();
        var created = await Service(context).Create(PropertyTestData.Car(), Actor);
        var body = PropertyTestData.Car();
        body.Archived = true;
        body.VehicleDetails!.ModelYear = Now.Year + 5;

        await Assert.ThrowsAsync<DomainValidationException>(() => Service(context).Update(created.PropertyId, body, Actor));

        Assert.Empty(Events(context, created.PropertyId));
    }

    /// <summary>AC 10 — a failed save leaves neither the archive stamp nor its event.</summary>
    [Fact]
    public async Task Update_WhoseSaveFails_LeavesNeitherTheStampNorTheEvent()
    {
        var database = Guid.NewGuid().ToString();
        Guid propertyId;
        await using (var seed = NewContext(database))
        {
            seed.Currencies.Add(new Currency { CurrencyCode = "SEK", Name = "Krona", MinorUnits = 2, Symbol = "kr" });
            await seed.SaveChangesAsync();
            propertyId = (await Service(seed).Create(PropertyTestData.Car(), Actor)).PropertyId;
        }

        await using (var failing = NewContext(database, new FailingSave()))
        {
            var body = PropertyTestData.Car();
            body.Archived = true;
            await Assert.ThrowsAsync<InvalidOperationException>(() => Service(failing).Update(propertyId, body, Actor));
        }

        await using var verify = NewContext(database);
        Assert.Null((await verify.Properties.AsNoTracking().SingleAsync()).Archived);
        Assert.Empty(verify.PropertyEvents);
    }

    /// <summary>AC 13 — estimates are a separate claim; writing one records nothing on the log.</summary>
    [Fact]
    public async Task CreatingOrDeletingAnEstimate_WritesNoEvent()
    {
        await using var context = TestContextFactory.Create();
        var created = await Service(context).Create(PropertyTestData.Car(), Actor);
        var estimates = new PropertyEstimateService(context);

        var estimate = await estimates.Create(created.PropertyId, new NewPropertyEstimate
        {
            Value = 100m,
            EffectiveFrom = Now.AddDays(-1),
        });
        await estimates.Delete(created.PropertyId, estimate.PropertyEstimateId);

        Assert.Empty(Events(context, created.PropertyId));
    }

    /// <summary>The list and the single read carry the log's size, system rows included.</summary>
    [Fact]
    public async Task GetAndList_CarryTheEventCount()
    {
        await using var context = TestContextFactory.Create();
        var body = PropertyTestData.House();
        body.Archived = true;
        var created = await Service(context).Create(body, Actor);
        var bare = await Service(context).Create(PropertyTestData.Car(), Actor);

        Assert.Equal(2, (await Service(context).Get(created.PropertyId))!.EventCount);
        var listed = (await Service(context).ListAsync(new PropertiesQueryParams())).Items;
        Assert.Equal(2, listed.Single(p => p.PropertyId == created.PropertyId).EventCount);
        Assert.Equal(0, listed.Single(p => p.PropertyId == bare.PropertyId).EventCount);
    }

    /// <summary>AC 15 — the property delete takes its events with it on the InMemory tier.</summary>
    [Fact]
    public async Task Delete_CascadesTheEvents()
    {
        await using var context = TestContextFactory.Create();
        var created = await Service(context).Create(PropertyTestData.House(), Actor);
        Assert.Single(Events(context, created.PropertyId));

        Assert.True(await Service(context).Delete(created.PropertyId, Actor));

        Assert.Empty(context.PropertyEvents);
    }

    /// <summary>
    /// AC 14 — no generated title or description carries the property's free text or detail values.
    /// The catalogue's signature already takes none of them; this pins the outputs over a property
    /// seeded with distinctive values in every such field.
    /// </summary>
    [Fact]
    public async Task GeneratedProse_NeverContainsAPropertyDetail()
    {
        await using var context = TestContextFactory.Create();
        var house = PropertyTestData.House(name: "Zanzibar Villa");
        house.Description = "Quokka description";
        house.Notes = "Xylophone notes";
        house.RealEstateDetails!.AddressLine = "Wombat Lane 3";
        house.RealEstateDetails.CadastralNumber = "999/777";
        house.DisposedDate = Now.AddDays(-1);
        house.Archived = true;
        var car = PropertyTestData.Car(name: "Narwhal Wagon");
        car.VehicleDetails!.RegistrationNumber = "QQ55555";
        car.VehicleDetails.Vin = "VINVINVIN00000001";
        car.VehicleDetails.Make = "Pangolin";
        car.VehicleDetails.Model = "Okapi";
        car.AcquiredDate = Now.AddYears(-1);
        car.Archived = true;

        var h = await Service(context).Create(house, Actor);
        var c = await Service(context).Create(car, Actor);
        var cleared = PropertyTestData.Car(name: "Narwhal Wagon");
        cleared.VehicleDetails = car.VehicleDetails;
        cleared.AcquiredDate = null;
        await Service(context).Update(c.PropertyId, cleared, Actor);

        string[] forbidden =
        [
            "Zanzibar", "Quokka", "Xylophone", "Wombat", "999/777", "Oslo", "Storgata",
            "Narwhal", "QQ55555", "VINVINVIN", "Pangolin", "Okapi", "Daily driver",
        ];
        var prose = context.PropertyEvents.AsNoTracking()
            .Where(e => e.PropertyId == h.PropertyId || e.PropertyId == c.PropertyId)
            .AsEnumerable()
            .SelectMany(e => new[] { e.Title, e.Description ?? string.Empty })
            .ToList();

        Assert.True(prose.Count >= 10);
        Assert.All(prose, text => Assert.All(forbidden, word => Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void TheCatalogue_BoundsEveryString_AndCoversAllSixTransitions()
    {
        var descriptors =
            from stamp in Enum.GetValues<PropertyStamp>()
            from set in new[] { true, false }
            select PropertyEventCatalogue.Stamp(stamp, set, Now);

        var all = descriptors.ToList();
        Assert.Equal(6, all.Select(d => d.Type).Distinct().Count());
        Assert.All(all, d =>
        {
            Assert.InRange(d.Title.Length, 1, ContractEventCatalogue.MaxTitleLength);
            Assert.InRange(d.Description!.Length, 1, ContractEventCatalogue.MaxDescriptionLength);
            Assert.Equal(Now, d.OccurredAt);
        });
    }

    private static OdysseyContext NewContext(string database, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseInMemoryDatabase(database)
            .AddInterceptors(interceptors)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private sealed class FailingSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Forced save failure.");
    }
}
