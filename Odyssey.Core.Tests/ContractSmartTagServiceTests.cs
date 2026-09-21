using Odyssey.Core;
using Odyssey.Context;
using Xunit;
using Odyssey.Core.Finance;
using Context = Odyssey.Context;

namespace Odyssey.Core.Tests;

/// <summary>
/// Fast unit coverage for the contract smart-tag throw branches, the insertion ordering and the
/// per-contract cap (issue #166).
/// </summary>
public class ContractSmartTagServiceTests
{
    /// <summary>
    /// The cap the fake lookup serves. Deliberately NOT the shipped 20: a test that only passed at the
    /// default would pass equally well against a service that read a constant instead of the setting.
    /// </summary>
    private const int SmartTagCap = 3;

    /// <summary>A movable clock, so a test can stamp links out of insertion order.</summary>
    private sealed class MovableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static async Task<Guid> SeedContract(OdysseyContext context, string name = "Broadband")
    {
        var contract = new Contract
        {
            Name = name,
            Type = Context.ContractType.Subscription,
            CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Contracts.Add(contract);
        await context.SaveChangesAsync();
        return contract.ContractId;
    }

    private static async Task<Guid> SeedTag(OdysseyContext context, string name = "Streaming", bool archived = false)
    {
        var tag = new TransactionTag
        {
            Name = name,
            Archived = archived ? DateTime.UtcNow : null,
        };
        context.TransactionTags.Add(tag);
        await context.SaveChangesAsync();
        return tag.TransactionTagId;
    }

    [Fact]
    public async Task GetSmartTags_ReturnsNullForMissingContract()
    {
        await using var context = TestContextFactory.Create();
        var service = new ContractSmartTagService(context, new FakeContractLimitsLookup());

        Assert.Null(await service.GetSmartTags(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetSmartTags_ReturnsEmptyListForAnUnwatchedContract()
    {
        await using var context = TestContextFactory.Create();
        var service = new ContractSmartTagService(context, new FakeContractLimitsLookup());
        var contractId = await SeedContract(context);

        // Empty is a healthy state, distinct from the missing-contract null above.
        Assert.Empty((await service.GetSmartTags(contractId))!);
    }

    [Fact]
    public async Task AddSmartTag_MissingContract_ThrowsNotFound()
    {
        await using var context = TestContextFactory.Create();
        var service = new ContractSmartTagService(context, new FakeContractLimitsLookup());
        var tagId = await SeedTag(context);

        await Assert.ThrowsAsync<DomainNotFoundException>(() => service.AddSmartTag(Guid.NewGuid(), tagId));
    }

    [Fact]
    public async Task AddSmartTag_MissingTag_ThrowsNotFound()
    {
        await using var context = TestContextFactory.Create();
        var service = new ContractSmartTagService(context, new FakeContractLimitsLookup());
        var contractId = await SeedContract(context);

        await Assert.ThrowsAsync<DomainNotFoundException>(() => service.AddSmartTag(contractId, Guid.NewGuid()));
    }

    [Fact]
    public async Task AddSmartTag_ArchivedTag_ThrowsUnprocessable()
    {
        await using var context = TestContextFactory.Create();
        var service = new ContractSmartTagService(context, new FakeContractLimitsLookup());
        var contractId = await SeedContract(context);
        var tagId = await SeedTag(context, archived: true);

        await Assert.ThrowsAsync<DomainUnprocessableException>(() => service.AddSmartTag(contractId, tagId));
        Assert.Empty(context.ContractSmartTags);
    }

    [Fact]
    public async Task AddSmartTag_Duplicate_ThrowsConflict()
    {
        await using var context = TestContextFactory.Create();
        var service = new ContractSmartTagService(context, new FakeContractLimitsLookup());
        var contractId = await SeedContract(context);
        var tagId = await SeedTag(context);

        await service.AddSmartTag(contractId, tagId);

        await Assert.ThrowsAsync<DomainConflictException>(() => service.AddSmartTag(contractId, tagId));
        Assert.Single(context.ContractSmartTags);
    }

    [Fact]
    public async Task AddSmartTag_Persists_AndReturnsTag()
    {
        await using var context = TestContextFactory.Create();
        var service = new ContractSmartTagService(context, new FakeContractLimitsLookup());
        var contractId = await SeedContract(context);
        var tagId = await SeedTag(context, "Utilities");

        var result = await service.AddSmartTag(contractId, tagId);

        Assert.Equal(tagId, result.TransactionTagId);
        Assert.Equal("Utilities", result.Name);
        Assert.Single((await service.GetSmartTags(contractId))!);
    }

    /// <summary>
    /// The list endpoint promises oldest association first, and it promises it by <c>AddedAt</c> — not
    /// by whatever order the provider happens to return rows in. A fake <see cref="TimeProvider"/>
    /// stamps the links in the reverse of the order they are inserted, so an implementation ordering by
    /// anything else fails here.
    /// </summary>
    [Fact]
    public async Task GetSmartTags_OrdersByAddedAt_NotInsertionOrder()
    {
        await using var context = TestContextFactory.Create();
        var clock = new MovableTimeProvider(new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero));
        var service = new ContractSmartTagService(context, new FakeContractLimitsLookup(), clock);
        var contractId = await SeedContract(context);

        var third = await SeedTag(context, "third");
        var second = await SeedTag(context, "second");
        var first = await SeedTag(context, "first");

        await service.AddSmartTag(contractId, third);
        clock.Now = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        await service.AddSmartTag(contractId, second);
        clock.Now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await service.AddSmartTag(contractId, first);

        var names = (await service.GetSmartTags(contractId))!.Select(t => t.Name).ToList();
        Assert.Equal(["first", "second", "third"], names);
    }

    [Fact]
    public async Task AddSmartTag_AtCap_ThrowsUnprocessable_AndNamesTheEffectiveCap()
    {
        await using var context = TestContextFactory.Create();
        var service = new ContractSmartTagService(context, new FakeContractLimitsLookup(SmartTagCap));
        var contractId = await SeedContract(context);

        for (var i = 0; i < SmartTagCap; i++)
        {
            await service.AddSmartTag(contractId, await SeedTag(context, $"tag-{i}"));
        }

        var overCapTag = await SeedTag(context, "over-cap");
        var exception = await Assert.ThrowsAsync<DomainUnprocessableException>(
            () => service.AddSmartTag(contractId, overCapTag));

        // The effective number, not the shipped 20 — the message is what tells a user what to do.
        Assert.Contains(SmartTagCap.ToString(), exception.Message);
        Assert.Equal(SmartTagCap, context.ContractSmartTags.Count());
    }

    /// <summary>
    /// A degraded read must never LOOSEN the cap: enforcement keeps working on the conservative number
    /// while the display endpoint fails closed with a <c>503</c>.
    /// </summary>
    [Fact]
    public async Task AddSmartTag_DegradedLimits_StillEnforcesTheConservativeCap()
    {
        await using var context = TestContextFactory.Create();
        var limits = new FakeContractLimitsLookup { Limits = new ContractLimits(1, IsDegraded: true) };
        var service = new ContractSmartTagService(context, limits);
        var contractId = await SeedContract(context);

        await service.AddSmartTag(contractId, await SeedTag(context, "first"));

        var second = await SeedTag(context, "second");
        await Assert.ThrowsAsync<DomainUnprocessableException>(
            () => service.AddSmartTag(contractId, second));
    }

    [Fact]
    public async Task RemoveSmartTag_RemovesAssociation_AndLeavesTheTagIntact()
    {
        await using var context = TestContextFactory.Create();
        var service = new ContractSmartTagService(context, new FakeContractLimitsLookup());
        var contractId = await SeedContract(context);
        var tagId = await SeedTag(context);
        await service.AddSmartTag(contractId, tagId);

        Assert.True(await service.RemoveSmartTag(contractId, tagId));
        Assert.Empty((await service.GetSmartTags(contractId))!);
        Assert.NotNull(context.TransactionTags.Find(tagId));
    }

    [Fact]
    public async Task RemoveSmartTag_ReturnsFalseWhenNotAssociated()
    {
        await using var context = TestContextFactory.Create();
        var service = new ContractSmartTagService(context, new FakeContractLimitsLookup());
        var contractId = await SeedContract(context);

        Assert.False(await service.RemoveSmartTag(contractId, Guid.NewGuid()));
    }

    /// <summary>
    /// A link is scoped by BOTH ids: a tag watched by another contract is not removable through this
    /// contract's route.
    /// </summary>
    [Fact]
    public async Task RemoveSmartTag_DoesNotReachAnotherContractsLink()
    {
        await using var context = TestContextFactory.Create();
        var service = new ContractSmartTagService(context, new FakeContractLimitsLookup());
        var mine = await SeedContract(context, "mine");
        var theirs = await SeedContract(context, "theirs");
        var tagId = await SeedTag(context);
        await service.AddSmartTag(theirs, tagId);

        Assert.False(await service.RemoveSmartTag(mine, tagId));
        Assert.Single((await service.GetSmartTags(theirs))!);
    }
}
