using Odyssey.Dtos.Finance;
using Odyssey.Dtos;
using Xunit;
using Odyssey.Core.Finance;

namespace Odyssey.Core.Tests;

public class TransactionTagServiceTests
{
    [Fact]
    public async Task CreateAndGetTransactionTagRoundTrips_AsActive()
    {
        await using var context = TestContextFactory.Create();
        var service = new TransactionTagService(context);

        var created = await service.Create(new NewTransactionTag
        {
            Name = "Groceries",
            Description = "Food",
            Archived = true,
        });

        var fetched = await service.Get(created.TransactionTagId);
        Assert.NotNull(fetched);
        Assert.Null(fetched!.Archived);
    }

    [Fact]
    public async Task UpdateTransactionTag_ArchiveTransitions_AreCorrectAndIdempotent()
    {
        await using var context = TestContextFactory.Create();
        var service = new TransactionTagService(context);

        var created = await service.Create(new NewTransactionTag
        {
            Name = "Utilities",
            Description = "Bills",
            Archived = false,
        });

        var activeToActive = await service.Update(created.TransactionTagId, new NewTransactionTag
        {
            Name = created.Name,
            Description = created.Description,
            Archived = false,
        });
        Assert.Null(activeToActive!.Archived);

        var activeToArchived = await service.Update(created.TransactionTagId, new NewTransactionTag
        {
            Name = created.Name,
            Description = created.Description,
            Archived = true,
        });
        Assert.NotNull(activeToArchived!.Archived);
        var firstArchivedAt = activeToArchived.Archived;

        var archivedToArchived = await service.Update(created.TransactionTagId, new NewTransactionTag
        {
            Name = created.Name,
            Description = created.Description,
            Archived = true,
        });
        Assert.Equal(firstArchivedAt, archivedToArchived!.Archived);

        var archivedToActive = await service.Update(created.TransactionTagId, new NewTransactionTag
        {
            Name = created.Name,
            Description = created.Description,
            Archived = false,
        });
        Assert.Null(archivedToActive!.Archived);
    }

    [Fact]
    public async Task ListAsync_SortsByDescription()
    {
        await using var context = TestContextFactory.Create();
        var service = new TransactionTagService(context);

        await service.Create(new NewTransactionTag { Name = "Alpha", Description = "Zeta desc", Archived = false });
        await service.Create(new NewTransactionTag { Name = "Beta", Description = "Alpha desc", Archived = false });

        var result = await service.ListAsync(
            new TransactionTagsQueryParams { SortBy = TransactionTagSortBy.Description, SortDir = SortDirection.Asc });

        Assert.Equal("Alpha desc", result.Items[0].Description);
        Assert.Equal("Zeta desc", result.Items[1].Description);
    }

    [Fact]
    public async Task ListAsync_SortsByStatus_ActiveBeforeArchived()
    {
        await using var context = TestContextFactory.Create();
        var service = new TransactionTagService(context);

        var toArchive = await service.Create(new NewTransactionTag { Name = "Was Active", Description = "a", Archived = false });
        await service.Create(new NewTransactionTag { Name = "Still Active", Description = "b", Archived = false });
        await service.Update(toArchive.TransactionTagId, new NewTransactionTag
        {
            Name = toArchive.Name,
            Description = toArchive.Description,
            Archived = true,
        });

        var result = await service.ListAsync(
            new TransactionTagsQueryParams { SortBy = TransactionTagSortBy.Status, SortDir = SortDirection.Asc });

        Assert.Null(result.Items[0].Archived);      // active sorts first
        Assert.NotNull(result.Items[1].Archived);   // archived sinks to the bottom
    }
}
