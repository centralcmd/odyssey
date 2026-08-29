using Odyssey.Dtos.Finance;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Odyssey.Api.Controllers;
using Odyssey.Core.Finance;

namespace Odyssey.Api.Tests;

public class TransactionTagControllerTests
{
    [Fact]
    public async Task Put_WhenTransactionTagIsMissing_ReturnsCreatedAtGetRouteWithCreatedId()
    {
        await using var context = TestContextFactory.Create();
        var service = new TransactionTagService(context);
        var controller = new TransactionTagController(NullLogger<TransactionTagController>.Instance, service);

        var missingId = Guid.NewGuid();
        var result = await controller.Put(missingId, new NewTransactionTag
        {
            Name = "Created from Put",
            Description = "Created",
            Archived = false,
        });

        var createdResult = Assert.IsType<CreatedAtRouteResult>(result);
        Assert.Equal("GetTransactionTag", createdResult.RouteName);

        var createdId = Assert.IsType<Guid>(createdResult.RouteValues!["id"]);
        Assert.NotEqual(missingId, createdId);
    }

    [Fact]
    public async Task Put_WhenTransactionTagExists_UpdatesInPlaceAndReturnsNoContent()
    {
        // The other branch of the upsert PUT: an existing id updates in place (204), it does NOT
        // create a second tag.
        await using var context = TestContextFactory.Create();
        var service = new TransactionTagService(context);
        var controller = new TransactionTagController(NullLogger<TransactionTagController>.Instance, service);

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

        // The upsert updated the row rather than inserting a new one.
        Assert.Single((await service.ListAsync(new TransactionTagsQueryParams())).Items);
    }
}
