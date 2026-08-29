using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Odyssey.Dtos.Authorization;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

[ApiController]
[Route("api/task-tags")]
public class JournalTaskTagsController : ControllerBase
{
    private readonly ILogger<JournalTaskTagsController> logger;
    private readonly JournalTaskTagService service;

    public JournalTaskTagsController(ILogger<JournalTaskTagsController> logger, JournalTaskTagService service)
    {
        this.logger = logger;
        this.service = service;
    }

    [HttpGet(Name = "GetJournalTaskTags")]
    [Authorize(Policy = PermissionClaims.TaskTagsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ExistingJournalTaskTag>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "List task tags (search, status filter, sort, paginate).")]
    public async Task<IActionResult> Get(
        [FromQuery] JournalTaskTagsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        return Ok(await service.ListAsync(query, cancellationToken));
    }

    [HttpGet("{id}", Name = "GetJournalTaskTag")]
    [Authorize(Policy = PermissionClaims.TaskTagsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingJournalTaskTag))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Get a task tag.")]
    public async Task<IActionResult> Get(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        var tag = await service.Get(id, cancellationToken);
        return tag is null ? this.NotFoundProblem($"Task tag ID {id} not found.") : Ok(tag);
    }

    [HttpPost(Name = "PostJournalTaskTag")]
    [Authorize(Policy = PermissionClaims.TaskTagsCreate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingJournalTaskTag))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Create a task tag.")]
    public async Task<IActionResult> Post(
        [FromBody] NewJournalTaskTag request, CancellationToken cancellationToken = default)
    {
        var created = await service.Create(request, cancellationToken);
        return CreatedAtRoute("GetJournalTaskTag", new { id = created.JournalTaskTagId }, created);
    }

    [HttpPut("{id}", Name = "PutJournalTaskTag")]
    [Authorize(Policy = PermissionClaims.TaskTagsUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingJournalTaskTag))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Rename, describe, or archive-toggle a task tag.")]
    public async Task<IActionResult> Put(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] UpdateJournalTaskTag request, CancellationToken cancellationToken = default)
    {
        var updated = await service.Update(id, request, cancellationToken);
        return updated is null ? this.NotFoundProblem($"Task tag ID {id} not found.") : Ok(updated);
    }

    [HttpDelete("{id}", Name = "DeleteJournalTaskTag")]
    [Authorize(Policy = PermissionClaims.TaskTagsDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Delete an unused task tag (in-use → 409; archive instead).")]
    public async Task<IActionResult> Delete(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        return await service.Delete(id, cancellationToken)
            ? NoContent()
            : this.NotFoundProblem($"Task tag ID {id} not found.");
    }
}
