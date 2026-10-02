using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos;
using Swashbuckle.AspNetCore.Annotations;
using Odyssey.Api.Identity;
using Odyssey.Core.Finance;
using Odyssey.Core.Identity;

namespace Odyssey.Api.Controllers;

/// <summary>
/// A contract's own event log. Split out of <c>ContractController</c> along the Property precedent, one controller per
/// sub-resource under the same route prefix; the routes and route names are unchanged (issue #287 M2).
/// </summary>
[ApiController]
[Route("api/contracts")]
public sealed class ContractEventsController : ControllerBase
{
    private readonly ContractEventService eventService;
    private readonly IUserDisplayNameResolver displayNames;

    public ContractEventsController(
        ContractEventService eventService,
        IUserDisplayNameResolver displayNames)
    {
        this.eventService = eventService;
        this.displayNames = displayNames;
    }

    // ── Events (the contract's own log) ──────────────────────────────────────────
    //
    // Gated on the parent contracts.read / contracts.update, matching the party, term and file
    // sub-resources (issue #138 §7.2). An event is contract data in the same trust boundary as the
    // contract's own description, so a separate claim would draw a boundary the data does not have —
    // and reusing the parent claims means no RolePermissions change and therefore no sign-out/sign-in.
    //
    // DELETE is deliberately contracts.update, not contracts.delete: removing an entry is editing the
    // contract's log, not deleting a contract.

    [HttpGet("{id}/events", Name = "GetContractEvents")]
    [Authorize(Policy = PermissionClaims.ContractsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ExistingContractEvent>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "List a contract's events, with search, type/date filters, sorting and pagination.",
        Description = @"Newest first by default. The search term spans the title, the description AND
                        the notes: all three sit behind contracts.read and are returned to the same
                        callers, so the notes field being absent from the timeline is a presentation
                        rule and never an access one. Events are NOT inlined on GET /api/contracts/{id}
                        — an event log grows without bound, so it is paged on its own route.")]
    public async Task<IActionResult> GetEvents(
        [FromRoute(Name = "id")] Guid id,
        [FromQuery] ContractEventsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var result = await eventService.ListAsync(id, query, cancellationToken);
        if (result is null)
        {
            return this.NotFoundProblem($"Contract ID {id} not found.");
        }

        await EnrichAuthorsAsync(result, cancellationToken);
        return Ok(result.Page);
    }

    [HttpPost("{id}/events", Name = "PostContractEvent")]
    [Authorize(Policy = PermissionClaims.ContractsUpdate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingContractEvent))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Record one event on a contract.",
        Description = @"createdBy and createdAtUtc are server-stamped and ignored if present in the
                        body. occurredAt must not be in the future — a 60-second forward tolerance
                        allows for a client clock that runs slightly fast, and beyond that the request
                        is a 400 keyed to the field. Writing to an archived contract is permitted,
                        matching the party, term and file writes.")]
    public async Task<IActionResult> PostEvent(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] NewContractEvent request,
        CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        var created = await eventService.CreateAsync(
            id, request, userId, cancellationToken);
        if (created is null)
        {
            return this.NotFoundProblem($"Contract ID {id} not found.");
        }

        var dto = await EnrichAuthorAsync(created, cancellationToken);
        // An event has no standalone GET (it is only ever read through its contract's log), so the 201
        // Location points at that log — the addressable collection that now contains it. Mirrors the
        // party, term and renewal endpoints.
        return CreatedAtRoute("GetContractEvents", new { id }, dto);
    }

    [HttpPut("{id}/events/{eventId}", Name = "PutContractEvent")]
    [Authorize(Policy = PermissionClaims.ContractsUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingContractEvent))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Replace one event on a contract (FULL replacement).",
        Description = @"null means ""clear this field"", NOT ""leave unchanged"" — the opposite of
                        PUT /api/contracts/{id}. A body omitting description or notes CLEARS it, and one
                        omitting type resets it to Other. createdBy and createdAtUtc are never rewritten
                        by an update. An event id that exists but belongs to a different contract is a
                        404, never a 403 and never a silent success.")]
    public async Task<IActionResult> PutEvent(
        [FromRoute(Name = "id")] Guid id,
        [FromRoute(Name = "eventId")] Guid eventId,
        [FromBody] UpdateContractEvent request,
        CancellationToken cancellationToken = default)
    {
        var updated = await eventService.UpdateAsync(id, eventId, request, cancellationToken);
        if (updated is null)
        {
            return this.NotFoundProblem($"Event ID {eventId} is not part of contract ID {id}.");
        }

        return Ok(await EnrichAuthorAsync(updated, cancellationToken));
    }

    [HttpDelete("{id}/events/{eventId}", Name = "DeleteContractEvent")]
    [Authorize(Policy = PermissionClaims.ContractsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Remove one event from a contract's log (the contract itself is untouched).")]
    public async Task<IActionResult> DeleteEvent(
        [FromRoute(Name = "id")] Guid id,
        [FromRoute(Name = "eventId")] Guid eventId,
        CancellationToken cancellationToken = default)
    {
        return await eventService.DeleteAsync(id, eventId, cancellationToken)
            ? NoContent()
            : this.NotFoundProblem($"Event ID {eventId} is not part of contract ID {id}.");
    }


    /// <summary>
    /// Turns each event's raw author id into a display label, in one batched lookup for the whole page.
    /// The ids arrive beside the DTOs rather than on them (see <c>ContractEventPage</c>), so the
    /// response carries a name and never a user id — deliberately unlike
    /// <c>ExistingTransactionFile.AttachedByUserId</c> and friends, which this surface does not join.
    /// </summary>
    private async Task EnrichAuthorsAsync(ContractEventPage page, CancellationToken cancellationToken)
    {
        if (page.Page.Items.Count == 0)
        {
            return;
        }

        var names = await displayNames.ResolveAsync(User, page.AuthorIds.Values, cancellationToken);
        foreach (var item in page.Page.Items)
        {
            var authorId = page.AuthorIds.GetValueOrDefault(item.ContractEventId);
            item.CreatedBy = string.IsNullOrWhiteSpace(authorId)
                ? UserDisplayNameResolver.UnknownUser
                : names.GetValueOrDefault(authorId, UserDisplayNameResolver.UnknownUser);
        }
    }

    /// <summary>The single-event form of <see cref="EnrichAuthorsAsync"/>.</summary>
    private async Task<ExistingContractEvent> EnrichAuthorAsync(
        ContractEventRead read, CancellationToken cancellationToken)
    {
        read.Event.CreatedBy = await displayNames.ResolveAsync(User, read.AuthorId, cancellationToken);
        return read.Event;
    }
}
