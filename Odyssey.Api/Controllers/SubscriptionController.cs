using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Odyssey.Dtos.Authorization;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

[ApiController]
[Route("api/subscriptions")]
public class SubscriptionController : ControllerBase
{
    private readonly ILogger<SubscriptionController> logger;
    private readonly SubscriptionService service;

    public SubscriptionController(
        ILogger<SubscriptionController> logger,
        SubscriptionService service)
    {
        this.logger = logger;
        this.service = service;
    }

    [HttpGet(Name = "GetSubscriptions")]
    [Authorize(Policy = PermissionClaims.SubscriptionsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<SubscriptionListItem>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "List subscriptions (lean projection), with search, filtering, sorting and pagination.")]
    public async Task<IActionResult> Get(
        [FromQuery] SubscriptionsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        return Ok(await service.ListAsync(query, cancellationToken));
    }

    [HttpGet("summary", Name = "GetSubscriptionSummary")]
    [Authorize(Policy = PermissionClaims.SubscriptionsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(SubscriptionSummary))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Summary rollup: status/interval counts, multi-currency run-rate, and derived upcoming renewals.")]
    public async Task<IActionResult> GetSummary(
        [FromQuery(Name = "baseCurrency")][StringLength(3, ErrorMessage = "baseCurrency must be a 3-letter ISO 4217 code.")] string? baseCurrency = null,
        CancellationToken cancellationToken = default)
    {
        return Ok(await service.GetSummary(baseCurrency, cancellationToken));
    }

    [HttpGet("{id}", Name = "GetSubscription")]
    [Authorize(Policy = PermissionClaims.SubscriptionsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingSubscription))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Get one subscription.")]
    public async Task<IActionResult> Get(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        var subscription = await service.Get(id, cancellationToken);
        return subscription is null ? this.NotFoundProblem($"Subscription ID {id} not found.") : Ok(subscription);
    }

    [HttpPost(Name = "PostSubscription")]
    [Authorize(Policy = PermissionClaims.SubscriptionsCreate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingSubscription))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Create a subscription.")]
    public async Task<IActionResult> Post(
        [FromBody] NewSubscription request, CancellationToken cancellationToken = default)
    {
        var created = await service.Create(request, cancellationToken);
        return CreatedAtRoute("GetSubscription", new { id = created.SubscriptionId }, created);
    }

    [HttpPut("{id}", Name = "PutSubscription")]
    [Authorize(Policy = PermissionClaims.SubscriptionsUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingSubscription))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Update a subscription's fields, including the pause/archive toggles.")]
    public async Task<IActionResult> Put(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] UpdateSubscription request, CancellationToken cancellationToken = default)
    {
        var updated = await service.Update(id, request, cancellationToken);
        return updated is null ? this.NotFoundProblem($"Subscription ID {id} not found.") : Ok(updated);
    }

    [HttpDelete("{id}", Name = "DeleteSubscription")]
    [Authorize(Policy = PermissionClaims.SubscriptionsDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Permanently delete a subscription.")]
    public async Task<IActionResult> Delete(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        return await service.Delete(id, cancellationToken) ? NoContent() : this.NotFoundProblem($"Subscription ID {id} not found.");
    }
}
