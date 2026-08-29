using Odyssey.Core;
using Odyssey.Context;
using Xunit;
using Odyssey.Core.Finance;
using Context = Odyssey.Context;

namespace Odyssey.Core.Tests;

/// <summary>
/// Fast unit coverage for the smart-tag throw branches and the per-account cap that
/// <c>AccountSmartTagsApiTests</c> only reaches over HTTP (issue #240 M1).
/// </summary>
public class AccountSmartTagServiceTests
{
    /// <summary>
    /// The cap the fake lookup serves. A local constant rather than a reference to the deleted
    /// <c>AccountSmartTagService.MaxSmartTagsPerAccount</c>: the cap is admin-editable since issue #434
    /// (key 15) and the service reads it from <see cref="IAccountLimitsLookup"/>, so the number under
    /// test is whatever the fake was constructed with — see <c>AddSmartTag_AtCap_ThrowsUnprocessable</c>,
    /// which now pins a NON-default cap and so proves the setting is actually consulted.
    /// </summary>
    private const int SmartTagCap = 3;

    private static async Task<Guid> SeedAccount(OdysseyContext context)
    {
        var account = new Account
        {
            Name = "Checking",
            Description = "Primary",
            Opened = DateTime.UtcNow,
            AccountType = Context.AccountType.CheckingAccount,
            CurrencyCode = "USD",
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();
        return account.AccountId;
    }

    private static async Task<Guid> SeedTag(OdysseyContext context, string name = "Groceries", bool archived = false)
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
    public async Task GetSmartTags_ReturnsNullForMissingAccount()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountSmartTagService(context, new FakeAccountLimitsLookup());

        Assert.Null(await service.GetSmartTags(Guid.NewGuid()));
    }

    [Fact]
    public async Task AddSmartTag_MissingAccount_ThrowsNotFound()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountSmartTagService(context, new FakeAccountLimitsLookup());
        var tagId = await SeedTag(context);

        await Assert.ThrowsAsync<DomainNotFoundException>(() => service.AddSmartTag(Guid.NewGuid(), tagId));
    }

    [Fact]
    public async Task AddSmartTag_MissingTag_ThrowsNotFound()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountSmartTagService(context, new FakeAccountLimitsLookup());
        var accountId = await SeedAccount(context);

        await Assert.ThrowsAsync<DomainNotFoundException>(() => service.AddSmartTag(accountId, Guid.NewGuid()));
    }

    [Fact]
    public async Task AddSmartTag_ArchivedTag_ThrowsUnprocessable()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountSmartTagService(context, new FakeAccountLimitsLookup());
        var accountId = await SeedAccount(context);
        var tagId = await SeedTag(context, archived: true);

        await Assert.ThrowsAsync<DomainUnprocessableException>(() => service.AddSmartTag(accountId, tagId));
    }

    [Fact]
    public async Task AddSmartTag_Duplicate_ThrowsConflict()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountSmartTagService(context, new FakeAccountLimitsLookup());
        var accountId = await SeedAccount(context);
        var tagId = await SeedTag(context);

        await service.AddSmartTag(accountId, tagId);

        await Assert.ThrowsAsync<DomainConflictException>(() => service.AddSmartTag(accountId, tagId));
    }

    [Fact]
    public async Task AddSmartTag_Persists_AndReturnsTag()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountSmartTagService(context, new FakeAccountLimitsLookup());
        var accountId = await SeedAccount(context);
        var tagId = await SeedTag(context, "Salary");

        var result = await service.AddSmartTag(accountId, tagId);

        Assert.Equal(tagId, result.TransactionTagId);
        Assert.Equal("Salary", result.Name);
        var tags = await service.GetSmartTags(accountId);
        Assert.Single(tags!);
    }

    [Fact]
    public async Task AddSmartTag_AtCap_ThrowsUnprocessable()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountSmartTagService(context, new FakeAccountLimitsLookup(SmartTagCap));
        var accountId = await SeedAccount(context);

        // Fill to the cap; the (cap+1)-th add must be rejected. The cap is deliberately NOT the shipped
        // 20: a test that only passed at the default would pass equally well against a service that had
        // gone back to reading a constant.
        for (var i = 0; i < SmartTagCap; i++)
        {
            var tagId = await SeedTag(context, $"tag-{i}");
            await service.AddSmartTag(accountId, tagId);
        }

        var overCapTag = await SeedTag(context, "over-cap");
        await Assert.ThrowsAsync<DomainUnprocessableException>(() => service.AddSmartTag(accountId, overCapTag));
    }

    [Fact]
    public async Task RemoveSmartTag_RemovesAssociation()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountSmartTagService(context, new FakeAccountLimitsLookup());
        var accountId = await SeedAccount(context);
        var tagId = await SeedTag(context);
        await service.AddSmartTag(accountId, tagId);

        Assert.True(await service.RemoveSmartTag(accountId, tagId));
        Assert.Empty((await service.GetSmartTags(accountId))!);
    }

    [Fact]
    public async Task RemoveSmartTag_ReturnsFalseWhenNotAssociated()
    {
        await using var context = TestContextFactory.Create();
        var service = new AccountSmartTagService(context, new FakeAccountLimitsLookup());
        var accountId = await SeedAccount(context);

        Assert.False(await service.RemoveSmartTag(accountId, Guid.NewGuid()));
    }
}
