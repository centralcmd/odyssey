using Odyssey.Dtos.Finance;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Swashbuckle.AspNetCore.Annotations;

using Odyssey.Core.Finance;

namespace Odyssey.Api.Controllers;

[ApiController]
[Route("api/transaction-tags")]
public class TransactionTagController : ControllerBase
{
    private readonly ILogger<TransactionTagController> logger;
    private readonly TransactionTagService transactionTagService;

    public TransactionTagController(
        ILogger<TransactionTagController> logger,
        TransactionTagService transactionTagService)
    {
        this.logger = logger;
        this.transactionTagService = transactionTagService;
    }

    [HttpGet(Name = "GetTransactionTags")]
    [Authorize(Policy = PermissionClaims.TransactionTagsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ExistingTransactionTag>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "List transaction tags.",
        Description = @"List transaction tags with search, status filter, sorting and pagination.")]
    public async Task<IActionResult> Get(
        [FromQuery] TransactionTagsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var result = await transactionTagService.ListAsync(query, cancellationToken);
        return Ok(result);
    }

    [HttpGet("{id}", Name = "GetTransactionTag")]
    [Authorize(Policy = PermissionClaims.TransactionTagsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingTransactionTag))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Get a transaction tag based on the transaction tag ID.",
        Description = @"Get a transaction tag based on the transaction tag ID.")]
    public async Task<IActionResult> Get(
        [FromRoute(Name = "id")] [SwaggerParameter("ID", Required = true,
            Description = @"The ID for the transaction tag to get.")] Guid id, CancellationToken cancellationToken = default)
    {
        var transactionTag = await transactionTagService.Get(id, cancellationToken);
        if (transactionTag is null)
        {
            return this.NotFoundProblem($"Transaction Tag ID {id} not found.");
        }

        return Ok(transactionTag);
    }

    [HttpPost(Name = "PostTransactionTag")]
    [Authorize(Policy = PermissionClaims.TransactionTagsCreate)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Create a new transaction tag.",
        Description = @"Create a new transaction tag. If a new transaction tag is created, the url for the new transaction tag is
                        returned in the location header.")]
    public async Task<IActionResult> Post(
        [FromBody] [SwaggerParameter("NewTransactionTag", Required = true,
            Description = "The new transaction tag to create.")] NewTransactionTag newTransactionTag, CancellationToken cancellationToken = default)
    {
        var transactionTag = await transactionTagService.Create(newTransactionTag, cancellationToken);
        return CreatedAtRoute("GetTransactionTag", new { id = transactionTag.TransactionTagId }, "");
    }

    [HttpPut("{id}", Name = "PutTransactionTag")]
    [Authorize(Policy = PermissionClaims.TransactionTagsUpdate)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Update the details for a transaction tag.",
        Description = @"Update the details for a transaction tag. If the transaction tag ID does not exist, a new transaction tag is
                        created based on the provided details and the url for the new transaction tag is returned in the
                        location header.")]
    public async Task<IActionResult> Put(
        [FromRoute(Name = "id")] [SwaggerParameter("ID", Required = true,
            Description = @"The ID for the transaction tag to update.")] Guid id,
        [FromBody] [SwaggerParameter("NewTransactionTag", Required = true,
            Description = @"The transaction tag with the updated values.")] NewTransactionTag newTransactionTag, CancellationToken cancellationToken = default)
    {
        var transactionTag = await transactionTagService.Update(id, newTransactionTag, cancellationToken);
        return transactionTag is null ? await Post(newTransactionTag, cancellationToken) : NoContent();
    }

    [HttpDelete("{id}", Name = "DeleteTransactionTag")]
    [Authorize(Policy = PermissionClaims.TransactionTagsDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Delete a transaction tag based on the transaction tag ID.",
        Description = @"Delete a transaction tag based on the transaction tag ID.")]
    public async Task<IActionResult> Delete(
        [FromRoute(Name = "id")] [SwaggerParameter("ID", Required = true,
            Description = @"The ID for the transaction tag to delete.")] Guid id, CancellationToken cancellationToken = default)
    {
        await transactionTagService.Delete(id, cancellationToken);
        return NoContent();
    }
}
