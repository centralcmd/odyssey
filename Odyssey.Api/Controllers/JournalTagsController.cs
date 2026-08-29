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
[Route("api/journal-tags")]
public class JournalTagsController : ControllerBase
{
    private readonly ILogger<JournalTagsController> logger;
    private readonly JournalTagService service;

    public JournalTagsController(
        ILogger<JournalTagsController> logger,
        JournalTagService service)
    {
        this.logger = logger;
        this.service = service;
    }

    [HttpGet(Name = "GetJournalTags")]
    [Authorize(Policy = PermissionClaims.JournalTagsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ExistingJournalTag>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "List journal tags with search, archival-status filter, sort and paging.")]
    public async Task<IActionResult> Get(
        [FromQuery] JournalTagsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        return Ok(await service.ListAsync(query, cancellationToken));
    }

    [HttpGet("{id}", Name = "GetJournalTag")]
    [Authorize(Policy = PermissionClaims.JournalTagsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingJournalTag))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Get a journal tag.")]
    public async Task<IActionResult> Get(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        var tag = await service.Get(id, cancellationToken);
        return tag is null ? this.NotFoundProblem($"Journal tag ID {id} not found.") : Ok(tag);
    }

    [HttpPost(Name = "PostJournalTag")]
    [Authorize(Policy = PermissionClaims.JournalTagsCreate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingJournalTag))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Create a journal tag.")]
    public async Task<IActionResult> Post(
        [FromBody] NewJournalTag request, CancellationToken cancellationToken = default)
    {
        var created = await service.Create(request, cancellationToken);
        return CreatedAtRoute("GetJournalTag", new { id = created.JournalTagId }, created);
    }

    [HttpPut("{id}", Name = "PutJournalTag")]
    [Authorize(Policy = PermissionClaims.JournalTagsUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingJournalTag))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Update a journal tag and toggle its archival state.")]
    public async Task<IActionResult> Put(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] UpdateJournalTag request, CancellationToken cancellationToken = default)
    {
        var updated = await service.Update(id, request, cancellationToken);
        return updated is null ? this.NotFoundProblem($"Journal tag ID {id} not found.") : Ok(updated);
    }

    [HttpDelete("{id}", Name = "DeleteJournalTag")]
    [Authorize(Policy = PermissionClaims.JournalTagsDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Delete an unused journal tag; a tag in use returns 409 (archive it instead).")]
    public async Task<IActionResult> Delete(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        return await service.Delete(id, cancellationToken) ? NoContent() : this.NotFoundProblem($"Journal tag ID {id} not found.");
    }
}
