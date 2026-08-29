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
[Route("api/budget-items")]
public class BudgetItemController : ControllerBase
{
    private readonly ILogger<BudgetItemController> logger;
    private readonly BudgetItemService budgetItemService;

    public BudgetItemController(ILogger<BudgetItemController> logger, BudgetItemService budgetItemService)
    {
        this.logger = logger;
        this.budgetItemService = budgetItemService;
    }

    [HttpGet(Name = "GetBudgetItems")]
    [Authorize(Policy = PermissionClaims.BudgetsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ExistingBudgetItem>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "List budget items.",
        Description = @"List budget items with search, filtering, sorting and pagination.")]
    public async Task<IActionResult> Get(
        [FromQuery] BudgetItemsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var result = await budgetItemService.ListAsync(query, cancellationToken);
        return Ok(result);
    }

    [HttpGet("{id}", Name = "GetBudgetItem")]
    [Authorize(Policy = PermissionClaims.BudgetsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingBudgetItem))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Get a budget item based on the budget item ID.",
        Description = @"Get a budget item based on the budget item ID.")]
    public async Task<IActionResult> Get(
        [FromRoute(Name = "id")] [SwaggerParameter("ID", Required = true,
            Description = @"The ID for the budget item to get.")] Guid id, CancellationToken cancellationToken = default)
    {
        var budgetItem = await budgetItemService.Get(id, cancellationToken);
        if (budgetItem is null)
        {
            return this.NotFoundProblem($"Budget item ID {id} not found.");
        }

        return Ok(budgetItem);
    }

    [HttpPost(Name = "PostBudgetItem")]
    [Authorize(Policy = PermissionClaims.BudgetsCreate)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Create a new budget item.",
        Description = @"Create a new budget item. If a new budget item is created, the url for the new budget item is 
                        returned in the location header.")]
    public async Task<IActionResult> Post(
        [FromBody] [SwaggerParameter("NewBudgetItem", Required = true,
            Description = "The new budget item to create.")] NewBudgetItem newBudgetItem, CancellationToken cancellationToken = default)
    {
        var budgetItem = await budgetItemService.Create(newBudgetItem, cancellationToken);
        return CreatedAtRoute("GetBudgetItem", new { id = budgetItem.BudgetItemId }, "");
    }

    [HttpPut("{id}", Name = "PutBudgetItem")]
    [Authorize(Policy = PermissionClaims.BudgetsUpdate)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Update the details for a budget item.",
        Description = @"Update the details for a budget item. If the budget item ID does not exist, a new budget item is 
                        created based on the provided details and the url for the new budget item is returned in the 
                        location header.")]
    public async Task<IActionResult> Put(
        [FromRoute(Name = "id")] [SwaggerParameter("ID", Required = true,
            Description = @"The ID for the budget item to update.")] Guid id,
        [FromBody] [SwaggerParameter("NewBudgetItem", Required = true,
            Description = @"The budget item with the updated values.")] NewBudgetItem newBudgetItem, CancellationToken cancellationToken = default)
    {
        var budgetItem = await budgetItemService.Update(id, newBudgetItem, cancellationToken);
        return budgetItem is null ? await Post(newBudgetItem, cancellationToken) : NoContent();
    }

    [HttpDelete("{id}", Name = "DeleteBudgetItem")]
    [Authorize(Policy = PermissionClaims.BudgetsDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Delete a budget item based on the budget item ID.",
        Description = @"Delete a budget item based on the budget item ID.")]
    public async Task<IActionResult> Delete(
        [FromRoute(Name = "id")] [SwaggerParameter("ID", Required = true,
            Description = @"The ID for the budget item to delete.")] Guid id, CancellationToken cancellationToken = default)
    {
        await budgetItemService.Delete(id, cancellationToken);
        return NoContent();
    }
}
