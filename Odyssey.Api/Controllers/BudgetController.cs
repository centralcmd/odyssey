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
[Route("api/budgets")]
public class BudgetController : ControllerBase
{
    private readonly ILogger<BudgetController> logger;
    private readonly BudgetService budgetService;

    public BudgetController(ILogger<BudgetController> logger, BudgetService budgetService)
    {
        this.logger = logger;
        this.budgetService = budgetService;
    }

    [HttpGet(Name = "GetBudgets")]
    [Authorize(Policy = PermissionClaims.BudgetsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ExistingBudget>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "List budgets.",
        Description = @"List budgets with search, sorting and pagination.")]
    public async Task<IActionResult> Get(
        [FromQuery] BudgetsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var result = await budgetService.ListAsync(query, cancellationToken);
        return Ok(result);
    }

    [HttpGet("summary", Name = "GetBudgetSummary")]
    [Authorize(Policy = PermissionClaims.BudgetsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(BudgetSummary))]
    [SwaggerOperation(
        Summary = "Summary rollup: active / archived counts and the combined planned balance.",
        Description = @"Aggregated server-side so the page header does not have to fetch every budget and its items.")]
    public async Task<IActionResult> GetSummary(CancellationToken cancellationToken = default)
    {
        return Ok(await budgetService.GetSummary(cancellationToken));
    }

    [HttpGet("{id}", Name = "GetBudget")]
    [Authorize(Policy = PermissionClaims.BudgetsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingBudget))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Get a budget based on the budget ID.",
        Description = @"Get a budget based on the budget ID.")]
    public async Task<IActionResult> Get(
        [FromRoute(Name = "id")] [SwaggerParameter("ID", Required = true,
            Description = @"The ID for the budget to get.")] Guid id, CancellationToken cancellationToken = default)
    {
        var budget = await budgetService.Get(id, cancellationToken);
        if (budget is null)
        {
            return this.NotFoundProblem($"Budget ID {id} not found.");
        }

        return Ok(budget);
    }

    [HttpPost(Name = "PostBudget")]
    [Authorize(Policy = PermissionClaims.BudgetsCreate)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Create a new budget.",
        Description = @"Create a new budget. If a new budget is created, the url for the new budget is 
                        returned in the location header.")]
    public async Task<IActionResult> Post(
        [FromBody] [SwaggerParameter("NewBudget", Required = true,
            Description = "The new budget to create.")] NewBudget newBudget, CancellationToken cancellationToken = default)
    {
        var budget = await budgetService.Create(newBudget, cancellationToken);
        return CreatedAtRoute("GetBudget", new { id = budget.BudgetId }, "");
    }

    [HttpPut("{id}", Name = "PutBudget")]
    [Authorize(Policy = PermissionClaims.BudgetsUpdate)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Update the details for a budget.",
        Description = @"Update the details for a budget. If the budget ID does not exist, a new budget is 
                        created based on the provided details and the url for the new budget is returned in the 
                        location header.")]
    public async Task<IActionResult> Put(
        [FromRoute(Name = "id")] [SwaggerParameter("ID", Required = true,
            Description = @"The ID for the budget to update.")] Guid id,
        [FromBody] [SwaggerParameter("NewBudget", Required = true,
            Description = @"The budget with the updated values.")] NewBudget newBudget, CancellationToken cancellationToken = default)
    {
        var budget = await budgetService.Update(id, newBudget, cancellationToken);
        return budget is null ? await Post(newBudget, cancellationToken) : NoContent();
    }

    [HttpDelete("{id}", Name = "DeleteBudget")]
    [Authorize(Policy = PermissionClaims.BudgetsDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Delete a budget based on the budget ID.",
        Description = @"Delete a budget based on the budget ID.")]
    public async Task<IActionResult> Delete(
        [FromRoute(Name = "id")] [SwaggerParameter("ID", Required = true,
            Description = @"The ID for the budget to delete.")] Guid id, CancellationToken cancellationToken = default)
    {
        await budgetService.Delete(id, cancellationToken);
        return NoContent();
    }
    
    [HttpGet("{id}/transactions", Name = "GetBudgetTransactions")]
    [Authorize(Policy = PermissionClaims.TransactionsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(BudgetReport))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Get transactions in a budget based on the budget ID.",
        Description = @"Get transactions based on the budget ID.")]
    public async Task<IActionResult> GetBudgetTransactions(
        [FromRoute(Name = "id")] [SwaggerParameter("ID", Required = true,
            Description = @"The ID for the budget to get transactions for.")] Guid id, CancellationToken cancellationToken = default)
    {
        var transactions = await budgetService.GetTransactions(id, cancellationToken);
        if (transactions is null)
        {
            return this.NotFoundProblem($"Budget ID {id} not found.");
        }

        return Ok(transactions);
    }
}
