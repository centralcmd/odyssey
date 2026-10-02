using Odyssey.Core;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Swashbuckle.AspNetCore.Annotations;

using Odyssey.Api.Identity;
using Odyssey.Core.Finance;
using Odyssey.Core.Identity;

namespace Odyssey.Api.Controllers;

[ApiController]
[Route("api/transactions")]
public sealed class TransactionsController : ControllerBase
{
    private readonly ILogger<TransactionsController> logger;
    private readonly TransactionService transactionService;
    private readonly FileService fileService;
    private readonly IUserDisplayNameResolver displayNames;

    public TransactionsController(
        ILogger<TransactionsController> logger,
        TransactionService transactionService,
        FileService fileService,
        IUserDisplayNameResolver displayNames)
    {
        this.logger = logger;
        this.transactionService = transactionService;
        this.fileService = fileService;
        this.displayNames = displayNames;
    }
    
    [HttpGet(Name = "GetTransactions")]
    [Authorize(Policy = PermissionClaims.TransactionsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ExistingTransaction>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "List transactions.",
        Description = @"List transactions with search, filtering, sorting and pagination.")]
    public async Task<IActionResult> Get(
        [FromQuery] TransactionsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var result = await transactionService.ListAsync(query, cancellationToken);
        await displayNames.EnrichFileAttributionAsync(User, result.Items, cancellationToken);
        return Ok(result);
    }

    [HttpGet("summary", Name = "GetTransactionSummary")]
    [Authorize(Policy = PermissionClaims.TransactionsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(TransactionSummary))]
    [SwaggerOperation(
        Summary = "Summary rollup: counts by status and direction, plus the money in / out totals.",
        Description = @"Aggregated server-side over every transaction, so the page header does not have to fetch the ledger to count it.")]
    public async Task<IActionResult> GetSummary(CancellationToken cancellationToken = default)
    {
        return Ok(await transactionService.GetSummary(cancellationToken));
    }

    [HttpGet("{id}", Name = "GetTransaction")]
    [Authorize(Policy = PermissionClaims.TransactionsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingTransaction))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Get a transaction based on the transaction ID.",
        Description = @"Get a transaction based on the transaction ID.")]
    public async Task<IActionResult> Get(
        [FromRoute(Name = "id")] [SwaggerParameter("ID", Required = true, 
            Description = @"The ID for the transaction to get.")] Guid id, CancellationToken cancellationToken = default)
    {
        var transaction = await transactionService.Get(id, cancellationToken);
        if (transaction is null)
        {
            return this.NotFoundProblem($"Transaction ID {id} not found.");
        }

        await displayNames.EnrichFileAttributionAsync(User, [transaction], cancellationToken);
        return Ok(transaction);
    }
    
    [HttpPost(Name = "PostTransaction")]
    [Authorize(Policy = PermissionClaims.TransactionsCreate)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Create a new transaction.",
        Description = @"Create a new transaction. If a new transaction is created, the url for the new transaction is 
                        returned in the location header.")]
    public async Task<IActionResult> Post(
        [FromBody] [SwaggerParameter("NewTransaction", Required = true, 
            Description = "The new transaction to create.")] NewTransaction newTransaction, CancellationToken cancellationToken = default)
    {
        var transaction = await transactionService.Create(newTransaction, cancellationToken);
        return CreatedAtRoute("GetTransaction", new { id = transaction.TransactionId }, "");
    }

    [HttpPut("{id}", Name = "PutTransaction")]
    [Authorize(Policy = PermissionClaims.TransactionsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Update the details for a transaction.",
        Description = @"Update the details for a transaction. Not an upsert: an unknown ID is a 404 and
                        nothing is created. Use POST to create.")]
    public async Task<IActionResult> Put(
        [FromRoute(Name = "id")] [SwaggerParameter("ID", Required = true, 
            Description = @"The ID for the transaction to update.")] Guid id, 
        [FromBody] [SwaggerParameter("NewTransaction", Required = true, 
            Description = @"The transaction with the updated values.")] NewTransaction newTransaction, CancellationToken cancellationToken = default)
    {
        var transaction = await transactionService.Update(id, newTransaction, cancellationToken);
        return transaction is null
            ? this.NotFoundProblem($"Transaction ID {id} not found.")
            : NoContent();
    }

    [HttpDelete("{id}", Name = "DeleteTransaction")]
    [Authorize(Policy = PermissionClaims.TransactionsDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Delete a transaction based on the transaction ID.",
        Description = @"Delete a transaction based on the transaction ID.")]
    public async Task<IActionResult> Delete(
        [FromRoute(Name = "id")] [SwaggerParameter("ID", Required = true, 
            Description = @"The ID for the transaction to delete.")] Guid id, CancellationToken cancellationToken = default)
    {
        await transactionService.Delete(id, cancellationToken);
        return NoContent();
    }

    [HttpGet("{transactionId}/files", Name = "GetTransactionFiles")]
    [Authorize(Policy = PermissionClaims.TransactionsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(List<ExistingTransactionFile>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Get files attached to a transaction.",
        Description = @"Get files attached to a transaction.")]
    public async Task<IActionResult> GetTransactionFiles(
        [FromRoute(Name = "transactionId")] [SwaggerParameter("TransactionId", Required = true, 
            Description = @"The ID for the transaction.")] Guid transactionId, CancellationToken cancellationToken = default)
    {
        var files = await transactionService.GetFiles(transactionId, cancellationToken);
        if (files is null)
        {
            return this.NotFoundProblem($"Transaction ID {transactionId} not found.");
        }

        await displayNames.EnrichFileAttributionAsync(User, files, cancellationToken);
        return Ok(files);
    }

    [HttpPost("{transactionId}/files", Name = "AttachTransactionFile")]
    [Authorize(Policy = PermissionClaims.TransactionsUpdate)]
    [Authorize(Policy = PermissionClaims.FilesRead)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingTransactionFile))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Attach an uploaded file to a transaction (requires transactions.update + files.read).",
        Description = @"Attach an already-uploaded file to a transaction.")]
    public async Task<IActionResult> AttachTransactionFile(
        [FromRoute(Name = "transactionId")] [SwaggerParameter("TransactionId", Required = true, 
            Description = @"The ID for the transaction.")] Guid transactionId,
        [FromBody] [SwaggerParameter("Request", Required = true,
            Description = @"The file to attach.")] AttachTransactionFileRequest request, CancellationToken cancellationToken = default)
    {
        if (!await transactionService.Exists(transactionId, cancellationToken))
        {
            return this.NotFoundProblem($"Transaction ID {transactionId} not found.");
        }

        var fileMetadata = await fileService.GetFileMetadataAsync(request.FileId);
        if (fileMetadata is null)
        {
            return this.NotFoundProblem($"File ID {request.FileId} not found.");
        }
        
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        // An undefined Type never gets here over HTTP: [EnumDataType] on the request is a model-validation
        // 400 (issue #287 M5). The mirror's fallback only matters for a direct, non-HTTP caller.
        var fileType = EnumMirror.Convert(request.Type, Odyssey.Context.TransactionFileType.Other);
        var created = await transactionService.AttachFileToTransaction(transactionId, request.FileId, userId, fileType, cancellationToken);
        if (created is null)
        {
            return this.NotFoundProblem($"Transaction ID {transactionId} not found.");
        }

        // The created link carries AttachedByUserId, so it is enriched like every list (issue #106).
        await displayNames.EnrichFileAttributionAsync(User, [created], cancellationToken);
        return CreatedAtRoute("GetTransactionFiles", new { transactionId }, created);
    }

    [HttpDelete("{transactionId}/files/{fileId}", Name = "DetachTransactionFile")]
    [Authorize(Policy = PermissionClaims.TransactionsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Detach a file from a transaction.",
        Description = @"Detach a file from a transaction.")]
    public async Task<IActionResult> DetachTransactionFile(
        [FromRoute(Name = "transactionId")] [SwaggerParameter("TransactionId", Required = true, 
            Description = @"The ID for the transaction.")] Guid transactionId,
        [FromRoute(Name = "fileId")] [SwaggerParameter("FileId", Required = true, 
            Description = @"The ID for the file to detach.")] Guid fileId, CancellationToken cancellationToken = default)
    {
        return await transactionService.DetachFileFromTransaction(transactionId, fileId, cancellationToken)
            ? NoContent()
            : this.NotFoundProblem($"File ID {fileId} is not attached to transaction ID {transactionId}.");
    }
}
