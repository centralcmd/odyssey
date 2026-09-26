using Odyssey.Core;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>Fast coverage for <see cref="PropertySmartTagService"/> (issue #167): throw branches, ordering and the cap.</summary>
public class PropertySmartTagServiceTests
{
    // Deliberately NOT the shipped 20, so a service reading a constant instead of the setting fails.
    private const int SmartTagCap = 3;

    private sealed class MovableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static async Task<Guid> SeedProperty(OdysseyContext context) =>
        (await new PropertyService(context).Create(PropertyTestData.House(), userId: null)).PropertyId;

    private static async Task<Guid> SeedTag(OdysseyContext context, string name, bool archived = false)
    {
        var tag = new TransactionTag { Name = name, Archived = archived ? DateTime.UtcNow : null };
        context.TransactionTags.Add(tag);
        await context.SaveChangesAsync();
        return tag.TransactionTagId;
    }

    [Fact]
    public async Task GetSmartTags_NullForAnUnknownProperty_EmptyForAnUnwatchedOne()
    {
        await using var context = TestContextFactory.Create();
        var service = new PropertySmartTagService(context, new FakePropertyLimitsLookup());

        Assert.Null(await service.GetSmartTags(Guid.NewGuid()));
        Assert.Empty((await service.GetSmartTags(await SeedProperty(context)))!);
    }

    [Fact]
    public async Task AddSmartTag_UnknownPropertyOrTag_IsNotFound()
    {
        await using var context = TestContextFactory.Create();
        var service = new PropertySmartTagService(context, new FakePropertyLimitsLookup());
        var propertyId = await SeedProperty(context);
        var tagId = await SeedTag(context, "Fuel");

        await Assert.ThrowsAsync<DomainNotFoundException>(() => service.AddSmartTag(Guid.NewGuid(), tagId));
        await Assert.ThrowsAsync<DomainNotFoundException>(() => service.AddSmartTag(propertyId, Guid.NewGuid()));
    }

    [Fact]
    public async Task AddSmartTag_Twice_IsAConflict_AndArchivedIsUnprocessable()
    {
        await using var context = TestContextFactory.Create();
        var service = new PropertySmartTagService(context, new FakePropertyLimitsLookup());
        var propertyId = await SeedProperty(context);
        var tagId = await SeedTag(context, "Fuel");
        var archivedId = await SeedTag(context, "Old", archived: true);

        await service.AddSmartTag(propertyId, tagId);
        await Assert.ThrowsAsync<DomainConflictException>(() => service.AddSmartTag(propertyId, tagId));
        await Assert.ThrowsAsync<DomainUnprocessableException>(() => service.AddSmartTag(propertyId, archivedId));
    }

    [Fact]
    public async Task AddSmartTag_AtTheCap_IsUnprocessable_AndNamesTheEffectiveNumber()
    {
        await using var context = TestContextFactory.Create();
        var service = new PropertySmartTagService(context, new FakePropertyLimitsLookup(SmartTagCap));
        var propertyId = await SeedProperty(context);
        for (var i = 0; i < SmartTagCap; i++)
            await service.AddSmartTag(propertyId, await SeedTag(context, $"Tag {i}"));

        var oneTooMany = await SeedTag(context, "One too many");

        var error = await Assert.ThrowsAsync<DomainUnprocessableException>(
            () => service.AddSmartTag(propertyId, oneTooMany));

        Assert.Contains($"at most {SmartTagCap} smart tags", error.Message);
    }

    [Fact]
    public async Task GetSmartTags_IsOldestAssociationFirst_AndRemoveReportsAbsence()
    {
        await using var context = TestContextFactory.Create();
        var clock = new MovableTimeProvider(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
        var service = new PropertySmartTagService(context, new FakePropertyLimitsLookup(), clock);
        var propertyId = await SeedProperty(context);
        var later = await SeedTag(context, "A-later");
        var earlier = await SeedTag(context, "Z-earlier");

        await service.AddSmartTag(propertyId, later);
        clock.Now = clock.Now.AddDays(-1);
        await service.AddSmartTag(propertyId, earlier);

        Assert.Equal(["Z-earlier", "A-later"], (await service.GetSmartTags(propertyId))!.Select(t => t.Name));
        Assert.True(await service.RemoveSmartTag(propertyId, earlier));
        Assert.False(await service.RemoveSmartTag(propertyId, earlier));
    }
}
