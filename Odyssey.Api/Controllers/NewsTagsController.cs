using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Odyssey.Core.Internet.News.Reading;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Internet;

namespace Odyssey.Api.Controllers;

/// <summary>
/// The news tags (issue #470 §5.4), on the contract every tag resource shares (journal, task, photo and transaction
/// tags): a paged list with search, status filter and sort; create; a full-replacement <c>PUT</c> that also archives or
/// restores; and a delete that refuses a tag in use with <c>409</c> (archive it instead). At most 500 tags.
/// </summary>
[ApiController]
[Route("api/news-tags")]
public sealed class NewsTagsController(NewsTagService tags) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = PermissionClaims.NewsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ExistingNewsTag>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [EndpointSummary("List news tags with search, archival-status filter, sort and paging.")]
    public async Task<IActionResult> List([FromQuery] NewsTagsQueryParams query, CancellationToken cancellationToken = default) =>
        Ok(await tags.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}", Name = "GetNewsTag")]
    [Authorize(Policy = PermissionClaims.NewsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingNewsTag))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [EndpointSummary("One news tag.")]
    public async Task<IActionResult> Get([FromRoute] Guid id, CancellationToken cancellationToken = default) =>
        await tags.GetAsync(id, cancellationToken) is { } tag ? Ok(tag) : NotFound(id);

    [HttpPost]
    [Authorize(Policy = PermissionClaims.NewsTagsCreate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingNewsTag))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [EndpointSummary("Create a news tag.")]
    public async Task<IActionResult> Create([FromBody] NewNewsTag request, CancellationToken cancellationToken = default)
    {
        var created = await tags.CreateAsync(request, cancellationToken);
        return CreatedAtRoute("GetNewsTag", new { id = created.NewsTagId }, created);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = PermissionClaims.NewsTagsUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingNewsTag))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [EndpointSummary("Update a news tag and toggle its archival state.")]
    public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] UpdateNewsTag request, CancellationToken cancellationToken = default) =>
        await tags.UpdateAsync(id, request, cancellationToken) is { } tag ? Ok(tag) : NotFound(id);

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = PermissionClaims.NewsTagsDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [EndpointSummary("Delete an unused news tag; a tag in use returns 409 (archive it instead).")]
    public async Task<IActionResult> Delete([FromRoute] Guid id, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        return await tags.DeleteAsync(id, userId, cancellationToken) ? NoContent() : NotFound(id);
    }

    private ObjectResult NotFound(Guid id) => this.NotFoundProblem($"News tag {id} was not found.");
}
