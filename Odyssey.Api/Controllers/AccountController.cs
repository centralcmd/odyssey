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
[Route("api/accounts")]
public class AccountController : ControllerBase
{
    private const string DefaultMainCurrency = "NOK";

    private readonly ILogger<AccountController> logger;
    private readonly AccountService accountService;
    private readonly FileService fileService;
    private readonly FileAnalysisService fileAnalysisService;
    private readonly AccountTotalsService accountTotalsService;

    public AccountController(
        ILogger<AccountController> logger,
        AccountService accountService,
        FileService fileService,
        FileAnalysisService fileAnalysisService,
        AccountTotalsService accountTotalsService)
    {
        this.logger = logger;
        this.accountService = accountService;
        this.fileService = fileService;
        this.fileAnalysisService = fileAnalysisService;
        this.accountTotalsService = accountTotalsService;
    }
    
    [HttpGet(Name = "GetAccounts")]
    [Authorize(Policy = PermissionClaims.AccountsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ExistingAccount>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "List accounts.",
        Description = @"List accounts with search, filtering, sorting and pagination.")]
    public async Task<IActionResult> Get(
        [FromQuery] AccountsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var result = await accountService.ListAsync(query, cancellationToken);
        return Ok(result);
    }

    [HttpGet("summary", Name = "GetAccountSummary")]
    [Authorize(Policy = PermissionClaims.AccountsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(AccountSummary))]
    [SwaggerOperation(
        Summary = "Summary rollup: counts by status and type, the value aggregates and the per-account allocations.",
        Description = @"Aggregated server-side so the page header and its allocation donuts do not have to fetch every account.")]
    public async Task<IActionResult> GetSummary(CancellationToken cancellationToken = default)
    {
        return Ok(await accountService.GetSummary(cancellationToken));
    }

    [HttpGet("{id}", Name = "GetAccount")]
    [Authorize(Policy = PermissionClaims.AccountsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingAccount))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Get an account based on the account ID.",
        Description = @"Get an account based on the account ID.")]
    public async Task<IActionResult> Get(
        [FromRoute(Name = "id")] [SwaggerParameter("ID", Required = true, 
            Description = @"The ID for the account to get.")] Guid id, CancellationToken cancellationToken = default)
    {
        var account = await accountService.Get(id, cancellationToken);
        if (account is null)
        {
            return this.NotFoundProblem($"Account ID {id} not found.");
        }
    
        return Ok(account);
    }
    
    [HttpGet("totals", Name = "GetAccountTotals")]
    [Authorize(Policy = PermissionClaims.AccountsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(AccountTotals))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Get total assets, liabilities and net worth in the main currency.",
        Description = @"Converts each active account's balance into the main currency using the latest
                        exchange rates and returns total assets, total liabilities, net worth, and the
                        accounts that could not be converted (no rate to the main currency).")]
    public async Task<IActionResult> GetTotals(
        [FromQuery(Name = "mainCurrency")] [SwaggerParameter("MainCurrency", Required = false,
            Description = @"The currency to convert into. Defaults to NOK.")] string? mainCurrency = null, CancellationToken cancellationToken = default)
    {
        var main = string.IsNullOrWhiteSpace(mainCurrency) ? DefaultMainCurrency : mainCurrency;
        var totals = await accountTotalsService.ComputeAsync(main, cancellationToken);
        return Ok(totals);
    }

    [HttpPost(Name = "PostAccount")]
    [Authorize(Policy = PermissionClaims.AccountsCreate)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Create a new account.",
        Description = @"Create a new account. If a new account is created, the url for the new account is 
                        returned in the location header.")]
    public async Task<IActionResult> Post(
        [FromBody] [SwaggerParameter("NewAccount", Required = true, 
            Description = "The new account to create.")] NewAccount newAccount, CancellationToken cancellationToken = default)
    {
        var account = await accountService.Create(newAccount, cancellationToken);
        return CreatedAtRoute("GetAccount", new { id = account.AccountId }, "");
    }

    [HttpPut("{id}", Name = "PutAccount")]
    [Authorize(Policy = PermissionClaims.AccountsUpdate)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Update the details for an account.",
        Description = @"Update the details for an account. If the account ID does not exist, a new account is 
                        created based on the provided details and the url for the new account is returned in the 
                        location header.")]
    public async Task<IActionResult> Put(
        [FromRoute(Name = "id")] [SwaggerParameter("ID", Required = true, 
            Description = @"The ID for the account to update.")] Guid id, 
        [FromBody] [SwaggerParameter("NewAccount", Required = true, 
            Description = @"The account with the updated values.")] NewAccount newAccount, CancellationToken cancellationToken = default)
    {
        // Documented upsert: an unknown id creates the account via Post, so this returns 201 with a
        // Location pointing at the newly-generated id (not the route id). Post and Put therefore share
        // the same NewAccount body contract and must keep their validation in lockstep.
        var account = await accountService.Update(id, newAccount, cancellationToken);
        return account is null ? await Post(newAccount, cancellationToken) : NoContent();
    }
    
    [HttpDelete("{id}", Name = "DeleteAccount")]
    [Authorize(Policy = PermissionClaims.AccountsDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Delete an account based on the account ID.",
        Description = @"Delete an account based on the account ID.")]
    public async Task<IActionResult> Delete(
        [FromRoute(Name = "id")] [SwaggerParameter("ID", Required = true,
            Description = @"The ID for the account to delete.")] Guid id, CancellationToken cancellationToken = default)
    {
        await accountService.Delete(id, cancellationToken);
        return NoContent();
    }

    [HttpGet("{accountId}/files", Name = "GetAccountFiles")]
    [Authorize(Policy = PermissionClaims.AccountsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(List<ExistingAccountFile>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Get files attached to an account.")]
    public async Task<IActionResult> GetAccountFiles(
        [FromRoute(Name = "accountId")] Guid accountId, CancellationToken cancellationToken = default)
    {
        var files = await accountService.GetAccountFiles(accountId, cancellationToken);
        if (files is null)
            return this.NotFoundProblem($"Account ID {accountId} not found.");

        return Ok(files);
    }

    [HttpGet("{accountId}/transactions", Name = "GetAccountTransactions")]
    [Authorize(Policy = PermissionClaims.AccountsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(List<ExistingTransaction>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Get the transactions that belong to an account.")]
    public async Task<IActionResult> GetAccountTransactions(
        [FromRoute(Name = "accountId")] Guid accountId, CancellationToken cancellationToken = default)
    {
        var transactions = await accountService.GetTransactions(accountId, cancellationToken);
        if (transactions is null)
            return this.NotFoundProblem($"Account ID {accountId} not found.");

        return Ok(transactions);
    }

    [HttpPost("{accountId}/files", Name = "AttachAccountFile")]
    [Authorize(Policy = PermissionClaims.AccountsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Attach an uploaded file to an account.")]
    public async Task<IActionResult> AttachAccountFile(
        [FromRoute(Name = "accountId")] Guid accountId,
        [FromBody] AttachAccountFileRequest request, CancellationToken cancellationToken = default)
    {
        var account = await accountService.Get(accountId, cancellationToken);
        if (account is null)
            return this.NotFoundProblem($"Account ID {accountId} not found.");

        var fileMetadata = await fileService.GetFileMetadataAsync(request.FileId, cancellationToken);
        if (fileMetadata is null)
            return this.NotFoundProblem($"File ID {request.FileId} not found.");

        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? throw new InvalidOperationException("User ID not found in claims.");

        await accountService.AttachFileToAccount(accountId, request.FileId, userId, request.FileType, request, cancellationToken);

        return NoContent();
    }

    [HttpGet("{accountId}/files/analysis/resumable", Name = "GetResumableAnalysisJobs")]
    [Authorize(Policy = PermissionClaims.FileAnalysisRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(IReadOnlyList<ResumableAnalysisSummary>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "List the latest resumable analysis review per file for an account.",
        Description = "Returns, for each of the account's statement files, the latest analysis job that can be " +
                      "resumed (extraction completed, candidates still pending). A minimal counts-only summary keyed " +
                      "by file id — one request for the whole Files section. Files with no resumable job are simply " +
                      "absent. 503 when the feature is disabled.")]
    public async Task<IActionResult> GetResumableAnalysisJobs(
        [FromRoute(Name = "accountId")] Guid accountId, CancellationToken cancellationToken = default)
    {
        var summaries = await fileAnalysisService.GetResumableJobsAsync(accountId, cancellationToken);
        return Ok(summaries);
    }

    [HttpPost("{accountId}/files/{fileId}/analyze", Name = "AnalyzeAccountFile")]
    [Authorize(Policy = PermissionClaims.FileAnalysisCreate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(AnalyzeFileResponse))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Trigger AI analysis of an account statement file.",
        Description = "Runs the file through the configured AI provider and extracts candidate transactions. File type must be Statement.")]
    public async Task<IActionResult> AnalyzeAccountFile(
        [FromRoute(Name = "accountId")] Guid accountId,
        [FromRoute(Name = "fileId")] Guid fileId,
        [FromBody] [SwaggerParameter("Per-document consent for the external AI transfer", Required = false)]
        AnalyzeFileRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? throw new InvalidOperationException("User ID not found in claims.");

        var result = await fileAnalysisService.AnalyzeAsync(accountId, fileId, userId, request, cancellationToken);
        return Ok(result);
    }

    [HttpPut("{accountId}/files/{fileId}", Name = "UpdateAccountFile")]
    [Authorize(Policy = PermissionClaims.AccountsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Update an attached file's document type.")]
    public async Task<IActionResult> UpdateAccountFile(
        [FromRoute(Name = "accountId")] Guid accountId,
        [FromRoute(Name = "fileId")] Guid fileId,
        [FromBody] UpdateAccountFileRequest request, CancellationToken cancellationToken = default)
    {
        var updated = await accountService.UpdateAccountFileType(accountId, fileId, request, cancellationToken);
        if (updated is null)
            return this.NotFoundProblem($"File ID {fileId} is not attached to account ID {accountId}.");

        return NoContent();
    }

    [HttpDelete("{accountId}/files/{fileId}", Name = "DetachAccountFile")]
    [Authorize(Policy = PermissionClaims.AccountsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Detach a file from an account.")]
    public async Task<IActionResult> DetachAccountFile(
        [FromRoute(Name = "accountId")] Guid accountId,
        [FromRoute(Name = "fileId")] Guid fileId, CancellationToken cancellationToken = default)
    {
        await accountService.DetachFileFromAccount(accountId, fileId, cancellationToken);
        return NoContent();
    }
}
