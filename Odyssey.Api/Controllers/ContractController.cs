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

namespace Odyssey.Api.Controllers;

[ApiController]
[Route("api/contracts")]
public class ContractController : ControllerBase
{
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "image/png",
        "image/jpeg",
        "image/webp",
    };

    private readonly ILogger<ContractController> logger;
    private readonly ContractService service;
    private readonly TermService termService;
    private readonly ContractEventService eventService;
    private readonly FileService fileService;
    private readonly IUserDisplayNameResolver displayNames;

    public ContractController(
        ILogger<ContractController> logger,
        ContractService service,
        TermService termService,
        ContractEventService eventService,
        FileService fileService,
        IUserDisplayNameResolver displayNames)
    {
        this.logger = logger;
        this.service = service;
        this.termService = termService;
        this.eventService = eventService;
        this.fileService = fileService;
        this.displayNames = displayNames;
    }

    // ── Contracts ────────────────────────────────────────────────────────────────

    [HttpGet(Name = "GetContracts")]
    [Authorize(Policy = PermissionClaims.ContractsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ContractListItem>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "List contracts (lean projection with derived status), with search, filtering, sorting and pagination.")]
    public async Task<IActionResult> Get(
        [FromQuery] ContractsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        return Ok(await service.ListAsync(query, cancellationToken));
    }

    [HttpGet("summary", Name = "GetContractSummary")]
    [Authorize(Policy = PermissionClaims.ContractsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ContractSummary))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary =
            "Summary rollup: counts by status and by type, the run rate, and the derived upcoming movements.",
        Description = @"The run rate is direction-aware (issue #159): 'monthly'/'yearly'/'byType' are the
                        OUTGOING side and keep their pre-#159 meaning, 'incomingMonthly'/'incomingYearly'/
                        'incomingByType' are the incoming side, and 'netMonthly'/'netYearly' are incoming
                        minus outgoing — the only signed figures in the payload. No gross ever mixes the
                        two. A gross is null when its own side has nothing convertible; the net is null
                        only when both are. One base currency is elected from both directions, and a
                        currency with no rate to it is named in 'unconvertedCurrencies' and excluded from
                        both sides and the net rather than folded in at 1:1.")]
    public async Task<IActionResult> GetSummary(
        [FromQuery(Name = "baseCurrency")][StringLength(3, ErrorMessage = "baseCurrency must be a 3-letter ISO 4217 code.")] string? baseCurrency = null,
        CancellationToken cancellationToken = default)
    {
        return Ok(await service.GetSummary(baseCurrency, cancellationToken));
    }

    [HttpGet("{id}", Name = "GetContract")]
    [Authorize(Policy = PermissionClaims.ContractsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingContract))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Get one contract with minimal party/file references and derived status.")]
    public async Task<IActionResult> Get(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        var contract = await service.Get(id, cancellationToken);
        if (contract is null)
        {
            return this.NotFoundProblem($"Contract ID {id} not found.");
        }

        await displayNames.EnrichFileAttributionAsync(User, [contract], cancellationToken);
        return Ok(contract);
    }

    [HttpPost(Name = "PostContract")]
    [Authorize(Policy = PermissionClaims.ContractsCreate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingContract))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Create a contract.")]
    public async Task<IActionResult> Post(
        [FromBody] NewContract request, CancellationToken cancellationToken = default)
    {
        // The caller's id travels to the service for the signature-transition log line (issue #145
        // §7.7) — the body accepts no user id, on this or any other contract endpoint. A Signed
        // transition can happen on POST as well as PUT, so both actions carry it.
        var created = await service.Create(
            request, User.FindFirstValue(ClaimTypes.NameIdentifier), cancellationToken);
        await displayNames.EnrichFileAttributionAsync(User, [created], cancellationToken);
        return CreatedAtRoute("GetContract", new { id = created.ContractId }, created);
    }

    [HttpPut("{id}", Name = "PutContract")]
    [Authorize(Policy = PermissionClaims.ContractsUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingContract))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Update a contract's fields, including its archive and pause state (no dedicated archive or pause endpoint).",
        Description = @"422 when the requested type would leave an existing party holding a role that type
rejects (issue #157). The body names each offending party by id, role and display name, and nothing is
written — re-role those parties or detach them first.")]
    public async Task<IActionResult> Put(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] UpdateContract request, CancellationToken cancellationToken = default)
    {
        // The structured offending-party list is shaped HERE, not thrown from the service:
        // DomainException carries a message, a code and a string -> string[] dictionary, none of which
        // can express a list of objects. The service keeps its own unconditional refusal as
        // defence-in-depth for non-HTTP callers, so a race that adds an illegal party between this
        // pre-check and the write is still refused — just with the flat message rather than the list.
        var orphaned = await service.FindPartiesRejectedByTypeAsync(id, request.Type, cancellationToken);
        if (orphaned.Count > 0)
        {
            var payload = new ContractTypeChangeBlockers
            {
                RequestedType = request.Type,
                Parties = [.. orphaned],
                LegalRoles = [.. ContractPartyRoleMatrix.LegalFor(request.Type)],
            };

            return this.UnprocessableEntityProblem(
                $"{orphaned.Count} part{(orphaned.Count == 1 ? "y" : "ies")} on this contract "
                + $"hold{(orphaned.Count == 1 ? "s" : "")} a role a {request.Type} contract cannot have. "
                + "Change the type after re-rolling them into a role this type allows, or detach them first.",
                new Dictionary<string, object?> { ["typeChange"] = payload });
        }

        var updated = await service.Update(
            id, request, User.FindFirstValue(ClaimTypes.NameIdentifier), cancellationToken);
        if (updated is null)
        {
            return this.NotFoundProblem($"Contract ID {id} not found.");
        }

        await displayNames.EnrichFileAttributionAsync(User, [updated], cancellationToken);
        return Ok(updated);
    }

    [HttpDelete("{id}", Name = "DeleteContract")]
    [Authorize(Policy = PermissionClaims.ContractsDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Permanently delete a contract (cascades party + file links; leaves the underlying records).")]
    public async Task<IActionResult> Delete(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        return await service.Delete(id, cancellationToken) ? NoContent() : this.NotFoundProblem($"Contract ID {id} not found.");
    }

    // ── Parties ──────────────────────────────────────────────────────────────────

    [HttpPost("{id}/parties", Name = "AddContractParty")]
    [Authorize(Policy = PermissionClaims.ContractsUpdate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingContractParty))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Add a party in a role, optionally for a term (exactly one of accountId/contactId).")]
    public async Task<IActionResult> AddParty(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] ContractPartyRequest request, CancellationToken cancellationToken = default)
    {
        var created = await service.AddParty(id, request, User.FindFirstValue(ClaimTypes.NameIdentifier), cancellationToken);
        // A party has no standalone GET (it is only ever read through its contract), so the 201
        // Location points at the contract — the addressable resource that now contains the new
        // party — while the body is the created party. Mirrors the contract file endpoints.
        return created is null
            ? this.NotFoundProblem($"Contract ID {id} not found.")
            : CreatedAtRoute("GetContract", new { id }, created);
    }

    [HttpPut("{id}/parties/{partyId}", Name = "UpdateContractParty")]
    [Authorize(Policy = PermissionClaims.ContractsUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingContractParty))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    // The edit enforces the same type x role matrix the add does, so it can answer 422 too — declared
    // here so the generated client and Swagger match reality (issue #169 §5 item 2). The party CAP is
    // deliberately not among the reasons: an in-place update is row-count-neutral and never re-checks
    // it, which is what keeps every party on an over-cap contract editable.
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Re-write one party: its role, its target, its dates, or any combination (full replacement).")]
    public async Task<IActionResult> UpdateParty(
        [FromRoute(Name = "id")] Guid id,
        [FromRoute(Name = "partyId")] Guid partyId,
        [FromBody] ContractPartyRequest request, CancellationToken cancellationToken = default)
    {
        // A missing contract throws its own 404 from the service; a null here is the narrower
        // PartyNotOnContract class. Neither carries a field key, which is what distinguishes both from
        // the inline PartyTargetNotFound the record picker renders (issue #121 §9).
        var updated = await service.UpdateParty(
            id, partyId, request, User.FindFirstValue(ClaimTypes.NameIdentifier), cancellationToken);
        return updated is null
            ? this.NotFoundProblem($"Party ID {partyId} is not part of contract ID {id}.")
            : Ok(updated);
    }

    [HttpDelete("{id}/parties/{partyId}", Name = "DeleteContractParty")]
    [Authorize(Policy = PermissionClaims.ContractsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Detach a party from a contract.")]
    public async Task<IActionResult> DeleteParty(
        [FromRoute(Name = "id")] Guid id,
        [FromRoute(Name = "partyId")] Guid partyId, CancellationToken cancellationToken = default)
    {
        return await service.DeleteParty(id, partyId, User.FindFirstValue(ClaimTypes.NameIdentifier), cancellationToken)
            ? NoContent()
            : this.NotFoundProblem($"Party ID {partyId} is not part of contract ID {id}.");
    }

    // ── Terms (rates & fees) ─────────────────────────────────────────────────────
    //
    // Gated on the parent contracts.read / contracts.update, NOT on a dedicated claim (issue #135
    // §7.2). That is this controller's own convention rather than a departure from it: parties and
    // file attach/download are already gated the same way. The account module's separate
    // accounts.terms.read/.write is the outlier being compared against. Reusing the parent claims
    // also means no RolePermissions change and therefore no sign-out/sign-in on deploy.
    //
    // Route names are distinct from the five account ones (GetTerms / GetCurrentTerms / PostTerm /
    // PutTerm / DeleteTerm) by construction — a collision is an ambiguous-route startup failure.

    [HttpGet("{id}/terms", Name = "GetContractTerms")]
    [Authorize(Policy = PermissionClaims.ContractsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(List<ExistingTerm>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Get the term (rate/fee) history for a contract.",
        Description = @"Lists the full term history for the contract, newest effective date first.
                        Optionally filtered by term kind and/or an as-of date. An archived contract's
                        history stays readable.")]
    public async Task<IActionResult> GetTerms(
        [FromRoute(Name = "id")] Guid id,
        [FromQuery(Name = "kind")] TermKind? kind = null,
        [FromQuery(Name = "asOf")] DateTime? asOf = null, CancellationToken cancellationToken = default)
    {
        var terms = await termService.GetContractHistory(id, kind, asOf, cancellationToken);
        return terms is null ? this.NotFoundProblem($"Contract ID {id} not found.") : Ok(terms);
    }

    [HttpGet("{id}/terms/current", Name = "GetCurrentContractTerms")]
    [Authorize(Policy = PermissionClaims.ContractsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(List<CurrentTerm>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Get the currently-effective term of each series on a contract.",
        Description = @"Returns the in-force entry of each (kind, label) series that has at least one
                        entry, as of now or the supplied as-of date. An empty array is a healthy
                        response — a contract with no recorded price is not a defect.")]
    public async Task<IActionResult> GetCurrentTerms(
        [FromRoute(Name = "id")] Guid id,
        [FromQuery(Name = "asOf")] DateTime? asOf = null, CancellationToken cancellationToken = default)
    {
        var terms = await termService.GetContractCurrent(id, asOf, cancellationToken);
        return terms is null ? this.NotFoundProblem($"Contract ID {id} not found.") : Ok(terms);
    }

    [HttpPost("{id}/terms", Name = "PostContractTerm")]
    [Authorize(Policy = PermissionClaims.ContractsUpdate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingTerm))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Create a term (rate/fee) entry on a contract.",
        Description = @"Fee and InterestRate only; ExpectedReturn prices invested principal, which a
                        contract does not hold. A money-valued term must name its currency — a contract
                        has none of its own to default from. 'direction' says which way the money moves
                        from the household's perspective; it is optional, omitting it means Outgoing,
                        and Incoming is refused on the two rate kinds.")]
    public async Task<IActionResult> PostTerm(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] NewTerm newTerm, CancellationToken cancellationToken = default)
    {
        // The acting user, threaded through exactly as every sibling contract write already does. Without
        // it the PriceChanged system event this write records would read "Unknown user" — which is
        // indistinguishable from a deleted author, so the defect would look like correct behaviour
        // (issue #154 §5.6).
        var term = await termService.CreateForContract(
            id, newTerm, User.FindFirstValue(ClaimTypes.NameIdentifier), cancellationToken);
        // A term has no standalone GET (it is only ever read through its owner), so the 201 Location
        // points at the contract's term list — the addressable collection that now contains it.
        // Mirrors the account term endpoint.
        return CreatedAtRoute("GetContractTerms", new { id }, term);
    }

    [HttpPut("{id}/terms/{termId}", Name = "PutContractTerm")]
    [Authorize(Policy = PermissionClaims.ContractsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Replace a term entry on a contract.",
        Description = @"The owner is not changeable through this endpoint — the route is the only
                        thing that names it. A term id belonging to an account or to a different
                        contract is a 404, never a 403 and never a silent success.

                        This is a FULL replace and 'direction' is not exempt: omitting it resets the
                        term to Outgoing, exactly as omitting 'label', 'currencyCode', 'interval' or
                        'anchorDate' already clears those. That matters more here than elsewhere, since
                        it moves the amount from one side of the household's net to the other — read
                        the term back and send its direction with the replacement.")]
    public async Task<IActionResult> PutTerm(
        [FromRoute(Name = "id")] Guid id,
        [FromRoute(Name = "termId")] Guid termId,
        [FromBody] NewTerm putTerm, CancellationToken cancellationToken = default)
    {
        var updated = await termService.UpdateForContract(
            id, termId, putTerm, User.FindFirstValue(ClaimTypes.NameIdentifier), cancellationToken);
        return updated
            ? NoContent()
            // Deliberately does not reveal which owner DOES hold the id (issue #135 §9).
            : this.NotFoundProblem($"Term ID {termId} is not attached to contract ID {id}.");
    }

    [HttpDelete("{id}/terms/{termId}", Name = "DeleteContractTerm")]
    [Authorize(Policy = PermissionClaims.ContractsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Delete a term entry from a contract (the contract itself is untouched).")]
    public async Task<IActionResult> DeleteTerm(
        [FromRoute(Name = "id")] Guid id,
        [FromRoute(Name = "termId")] Guid termId, CancellationToken cancellationToken = default)
    {
        return await termService.DeleteForContract(
            id, termId, User.FindFirstValue(ClaimTypes.NameIdentifier), cancellationToken)
            ? NoContent()
            : this.NotFoundProblem($"Term ID {termId} is not attached to contract ID {id}.");
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
        var created = await eventService.CreateAsync(
            id, request, User.FindFirstValue(ClaimTypes.NameIdentifier), cancellationToken);
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

    // ── Files ────────────────────────────────────────────────────────────────────

    [HttpPost("{id}/files", Name = "AttachContractFile")]
    [Authorize(Policy = PermissionClaims.ContractsUpdate)]
    [Authorize(Policy = PermissionClaims.FilesRead)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Attach an already-uploaded document to a contract (requires contracts.update + files.read).")]
    public async Task<IActionResult> AttachFile(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] AttachContractFileRequest request, CancellationToken cancellationToken = default)
    {
        // Check the (cheap) contract existence before the file-metadata lookup + allow-list, so
        // attaching to a missing contract 404s without a wasted file read.
        if (!await service.Exists(id, cancellationToken))
        {
            return this.NotFoundProblem($"Contract ID {id} not found.");
        }

        if (await ValidateAttachableFile(request.FileMetadataId, cancellationToken) is { } problem)
        {
            return problem;
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        var created = await service.AttachFile(id, request, userId, cancellationToken);
        return created is null
            ? this.NotFoundProblem($"Contract ID {id} not found.")
            : CreatedAtRoute("DownloadContractFile", new { id, fileId = request.FileMetadataId }, null);
    }

    [HttpGet("{id}/files", Name = "GetContractFiles")]
    [Authorize(Policy = PermissionClaims.ContractsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(List<ExistingContractFile>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "List the documents attached to a contract.",
        Description = "Unpaged and bounded by the per-contract file cap. A contract with no documents " +
                      "returns an empty array, never a 404.")]
    public async Task<IActionResult> GetFiles(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        var files = await service.GetFiles(id, cancellationToken);
        if (files is null)
        {
            return this.NotFoundProblem($"Contract ID {id} not found.");
        }

        await displayNames.EnrichFileAttributionAsync(User, files, cancellationToken);
        return Ok(files);
    }

    [HttpPut("{id}/files/{fileId}", Name = "UpdateContractFile")]
    [Authorize(Policy = PermissionClaims.ContractsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Update an attached document's type and validity metadata.",
        Description = "A full replacement: an omitted date or issuer clears the stored value. " +
                      "fileType may not be omitted. files.read is deliberately not required — this " +
                      "verb reads no file metadata and takes no file id the caller had not already " +
                      "attached.")]
    public async Task<IActionResult> UpdateFile(
        [FromRoute(Name = "id")] Guid id,
        [FromRoute(Name = "fileId")] Guid fileId,
        [FromBody] UpdateContractFileRequest request, CancellationToken cancellationToken = default)
    {
        return await service.UpdateFile(id, fileId, request, cancellationToken)
            ? NoContent()
            : this.NotFoundProblem($"File ID {fileId} is not attached to contract ID {id}.");
    }

    [HttpGet("{id}/files/{fileId}", Name = "DownloadContractFile")]
    [Authorize(Policy = PermissionClaims.ContractsRead)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Download a document attached to a contract.")]
    public async Task<IActionResult> DownloadFile(
        [FromRoute(Name = "id")] Guid id,
        [FromRoute(Name = "fileId")] Guid fileId, CancellationToken cancellationToken = default)
    {
        if (!await service.IsFileAttachedToContract(id, fileId, cancellationToken))
        {
            return this.NotFoundProblem($"File ID {fileId} is not attached to contract ID {id}.");
        }

        return await StreamFile(fileId, cancellationToken);
    }

    [HttpDelete("{id}/files/{fileId}", Name = "DetachContractFile")]
    [Authorize(Policy = PermissionClaims.ContractsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Detach a document from a contract (the underlying file is left intact).")]
    public async Task<IActionResult> DetachFile(
        [FromRoute(Name = "id")] Guid id,
        [FromRoute(Name = "fileId")] Guid fileId, CancellationToken cancellationToken = default)
    {
        return await service.DetachFile(id, fileId, cancellationToken)
            ? NoContent()
            : this.NotFoundProblem($"File ID {fileId} is not attached to contract ID {id}.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

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

    /// <summary>
    /// Validates an attach target: the file must exist and its server-recorded content type must be on
    /// the allow-list. Returns a problem result to short-circuit, or null when the file is allowed.
    /// </summary>
    private async Task<IActionResult?> ValidateAttachableFile(Guid fileId, CancellationToken cancellationToken = default)
    {
        var metadata = await fileService.GetFileMetadataAsync(fileId, cancellationToken);
        if (metadata is null)
        {
            return this.NotFoundProblem($"File ID {fileId} not found.");
        }

        if (!AllowedContentTypes.Contains(metadata.ContentType))
        {
            return this.BadRequestProblem($"Content type '{metadata.ContentType}' is not allowed for contract documents.");
        }

        return null;
    }

    private async Task<IActionResult> StreamFile(Guid fileId, CancellationToken cancellationToken = default)
    {
        var (metadata, content) = await fileService.GetFileContentAsync(fileId, cancellationToken);
        if (metadata is null || content is null)
        {
            return NotFound();
        }

        // Safe-download headers (§10): force a download and forbid content-type sniffing so a
        // mislabeled upload cannot be rendered/executed inline in the browser.
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.ETag = $"\"{metadata.Sha256Hash}\"";
        return File(content, metadata.ContentType, metadata.FileName);
    }

}
