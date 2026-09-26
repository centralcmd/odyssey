using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Odyssey.Api.Identity;
using Odyssey.Core.Finance;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

/// <summary>
/// A property's event log (issue #209): <c>api/properties/{propertyId}/events</c>, the shape of
/// <see cref="PropertyEstimatesController"/> and the behaviour of the contract event endpoints.
/// </summary>
/// <remarks>
/// <para>
/// Gated on the parent <c>properties.read</c> / <c>properties.update</c> — an event is property data in
/// the same trust boundary as the property's own notes, so no new claim and no sign-out. <c>DELETE</c>
/// is <c>properties.update</c>, not <c>properties.delete</c>: removing an entry edits the log.
/// </para>
/// <para>
/// Every <c>404</c> echoes route ids only — never a title, a note or a property detail (§7.6).
/// </para>
/// </remarks>
[ApiController]
[Route("api/properties")]
public class PropertyEventsController : ControllerBase
{
    private readonly PropertyEventService eventService;
    private readonly IUserDisplayNameResolver displayNames;

    public PropertyEventsController(PropertyEventService eventService, IUserDisplayNameResolver displayNames)
    {
        this.eventService = eventService;
        this.displayNames = displayNames;
    }

    [HttpGet("{propertyId}/events", Name = "GetPropertyEvents")]
    [Authorize(Policy = PermissionClaims.PropertiesRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ExistingPropertyEvent>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "List a property's events, with search, type/date/source filters, sorting and pagination.",
        Description = @"Newest first by default. The search term spans the title, the description AND
                        the notes. Events are NOT inlined on GET /api/properties/{id}.")]
    public async Task<IActionResult> GetEvents(
        [FromRoute(Name = "propertyId")] Guid propertyId,
        [FromQuery] PropertyEventsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var result = await eventService.ListAsync(propertyId, query, cancellationToken);
        if (result is null)
        {
            return this.NotFoundProblem($"Property ID {propertyId} was not found.");
        }

        await EnrichAuthorsAsync(result, cancellationToken);
        return Ok(result.Page);
    }

    [HttpPost("{propertyId}/events", Name = "PostPropertyEvent")]
    [Authorize(Policy = PermissionClaims.PropertiesUpdate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingPropertyEvent))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Record one event on a property.",
        Description = @"source, createdBy and createdAtUtc are server-stamped and ignored if present in
                        the body. occurredAt more than 60 seconds ahead of the server clock is a 422
                        keyed occurredAt. A type illegal for the property's type, or one only the server
                        records (Archived, Unarchived, AcquisitionDateCleared, DisposalReversed), is a
                        422 keyed type.")]
    public async Task<IActionResult> PostEvent(
        [FromRoute(Name = "propertyId")] Guid propertyId,
        [FromBody] NewPropertyEvent request,
        CancellationToken cancellationToken = default)
    {
        var created = await eventService.CreateAsync(
            propertyId, request, User.FindFirstValue(ClaimTypes.NameIdentifier), cancellationToken);
        if (created is null)
        {
            return this.NotFoundProblem($"Property ID {propertyId} was not found.");
        }

        // An event has no standalone GET, so the Location points at the log that now contains it.
        return CreatedAtRoute("GetPropertyEvents", new { propertyId }, await EnrichAuthorAsync(created, cancellationToken));
    }

    [HttpPut("{propertyId}/events/{eventId}", Name = "PutPropertyEvent")]
    [Authorize(Policy = PermissionClaims.PropertiesUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingPropertyEvent))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Replace one event on a property (FULL replacement).",
        Description = @"null means ""clear this field"", NOT ""leave unchanged"". A body omitting
                        description or notes CLEARS it, and one omitting type resets it to Other. A
                        system-only type may be kept on a row that already carries it, never introduced.
                        An event id belonging to another property or to a contract is a 404.")]
    public async Task<IActionResult> PutEvent(
        [FromRoute(Name = "propertyId")] Guid propertyId,
        [FromRoute(Name = "eventId")] Guid eventId,
        [FromBody] UpdatePropertyEvent request,
        CancellationToken cancellationToken = default)
    {
        var updated = await eventService.UpdateAsync(propertyId, eventId, request, cancellationToken);
        if (updated is null)
        {
            return this.NotFoundProblem($"Event ID {eventId} is not part of property ID {propertyId}.");
        }

        return Ok(await EnrichAuthorAsync(updated, cancellationToken));
    }

    [HttpDelete("{propertyId}/events/{eventId}", Name = "DeletePropertyEvent")]
    [Authorize(Policy = PermissionClaims.PropertiesUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Remove one event from a property's log (the property itself is untouched).")]
    public async Task<IActionResult> DeleteEvent(
        [FromRoute(Name = "propertyId")] Guid propertyId,
        [FromRoute(Name = "eventId")] Guid eventId,
        CancellationToken cancellationToken = default)
    {
        return await eventService.DeleteAsync(propertyId, eventId, cancellationToken)
            ? NoContent()
            : this.NotFoundProblem($"Event ID {eventId} is not part of property ID {propertyId}.");
    }

    /// <summary>
    /// Turns each event's raw author id into a display label in one batched lookup — the response
    /// carries a name and never a user id.
    /// </summary>
    private async Task EnrichAuthorsAsync(PropertyEventPage page, CancellationToken cancellationToken)
    {
        if (page.Page.Items.Count == 0)
        {
            return;
        }

        var names = await displayNames.ResolveAsync(User, page.AuthorIds.Values, cancellationToken);
        foreach (var item in page.Page.Items)
        {
            var authorId = page.AuthorIds.GetValueOrDefault(item.PropertyEventId);
            item.CreatedBy = string.IsNullOrWhiteSpace(authorId)
                ? UserDisplayNameResolver.UnknownUser
                : names.GetValueOrDefault(authorId, UserDisplayNameResolver.UnknownUser);
        }
    }

    private async Task<ExistingPropertyEvent> EnrichAuthorAsync(PropertyEventRead read, CancellationToken cancellationToken)
    {
        read.Event.CreatedBy = await displayNames.ResolveAsync(User, read.AuthorId, cancellationToken);
        return read.Event;
    }
}
