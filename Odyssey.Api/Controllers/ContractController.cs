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

[ApiController]
[Route("api/contracts")]
public class ContractController : ControllerBase
{
    private readonly ContractService service;
    private readonly ContractSummaryService summaryService;
    private readonly IUserDisplayNameResolver displayNames;

    public ContractController(
        ContractService service,
        ContractSummaryService summaryService,
        IUserDisplayNameResolver displayNames)
    {
        this.service = service;
        this.summaryService = summaryService;
        this.displayNames = displayNames;
    }

    // ── Contracts ────────────────────────────────────────────────────────────────

    [HttpGet(Name = "GetContracts")]
    [Authorize(Policy = PermissionClaims.ContractsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ContractListItem>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "List contracts (lean projection with derived status), with search, filtering, sorting and pagination.",
        Description = @"'search' matches the name, the description, the reference number and linked contact-party
                        names (case-insensitive substring). 'sortBy' accepts Name (default), StartDate, EndDate,
                        Type, Status and ReferenceNumber; ReferenceNumber sorts ascending by default with
                        contracts that have none last in both directions (issue #181).")]
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
        return Ok(await summaryService.GetSummary(baseCurrency, cancellationToken));
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
        await EnrichCreatorAsync(contract, cancellationToken);
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
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        // The caller's id travels to the service for the signature-transition log line (issue #145
        // §7.7) — the body accepts no user id, on this or any other contract endpoint. A Signed
        // transition can happen on POST as well as PUT, so both actions carry it.
        var created = await service.Create(
            request, userId, cancellationToken);
        await displayNames.EnrichFileAttributionAsync(User, [created], cancellationToken);
        await EnrichCreatorAsync(created, cancellationToken);
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

        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        var updated = await service.Update(
            id, request, userId, cancellationToken);
        if (updated is null)
        {
            return this.NotFoundProblem($"Contract ID {id} not found.");
        }

        await displayNames.EnrichFileAttributionAsync(User, [updated], cancellationToken);
        await EnrichCreatorAsync(updated, cancellationToken);
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


    /// <summary>
    /// Resolves who added the contract to a display label. No author recorded stays null — the client
    /// then shows no "Added by" line — rather than becoming "Unknown user", which would claim an author
    /// existed and was deleted for every contract created before the column did.
    /// </summary>
    private async Task EnrichCreatorAsync(ExistingContract contract, CancellationToken cancellationToken)
    {
        var authorId = await service.CreatedByUserIdOf(contract.ContractId, cancellationToken);
        contract.CreatedBy = string.IsNullOrWhiteSpace(authorId)
            ? null
            : await displayNames.ResolveAsync(User, authorId, cancellationToken);
    }
}
