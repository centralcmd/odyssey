using Odyssey.Dtos.Finance;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Odyssey.Api.Controllers;
using Odyssey.Core.Finance;

namespace Odyssey.Api.Tests;

public class TransactionTagControllerTests
{
    [Fact]
    public async Task Put_WhenTransactionTagIsMissing_ReturnsNotFoundAndCreatesNothing()
    {
        // Not an upsert (issue #239): creating through PUT bypassed transaction-tags.create.
        await using var context = TestContextFactory.Create();
        var service = new TransactionTagService(context);
        var controller = new TransactionTagsController(NullLogger<TransactionTagsController>.Instance, service);

        var result = await controller.Put(Guid.NewGuid(), new NewTransactionTag
        {
            Name = "Created from Put",
            Description = "Created",
            Archived = false,
        });

        var notFound = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, notFound.StatusCode);
        Assert.Empty((await service.ListAsync(new TransactionTagsQueryParams())).Items);
    }

    [Fact]
    public async Task Put_WhenTransactionTagExists_UpdatesInPlaceAndReturnsNoContent()
    {
        // An existing id updates in place (204); it does NOT create a second tag.
        await using var context = TestContextFactory.Create();
        var service = new TransactionTagService(context);
        var controller = new TransactionTagsController(NullLogger<TransactionTagsController>.Instance, service);

        var existing = await service.Create(new NewTransactionTag
        {
            Name = "Original",
            Description = "Original description",
            Archived = false,
        });

        var result = await controller.Put(existing.TransactionTagId, new NewTransactionTag
        {
            Name = "Renamed",
            Description = "Updated description",
            Archived = true,
        });

        Assert.IsType<NoContentResult>(result);

        var reloaded = await service.Get(existing.TransactionTagId);
        Assert.NotNull(reloaded);
        Assert.Equal("Renamed", reloaded!.Name);
        Assert.Equal("Updated description", reloaded.Description);
        Assert.NotNull(reloaded.Archived); // archiving stamps a timestamp

        // The PUT updated the row rather than inserting a new one.
        Assert.Single((await service.ListAsync(new TransactionTagsQueryParams())).Items);
    }
}
