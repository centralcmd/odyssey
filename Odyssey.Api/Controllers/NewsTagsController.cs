using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Odyssey.Core.Internet.News.Reading;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Internet;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

/// <summary>
/// The news tags (issue #470 §5.4): the journal tags' shape and guards, at most 500, unpaged. A tag in use is a
/// <c>409</c> with its counts unless the delete detaches its links, which needs <c>news-feeds.update</c> for feed links
/// and <c>news.update</c> for article links — decided inside the delete's transaction, so a link added after a
/// pre-flight can never be destroyed by a caller who was not asked to prove the claim for it.
/// </summary>
[ApiController]
[Route("api/news-tags")]
public sealed class NewsTagsController(NewsTagService tags) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = PermissionClaims.NewsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(List<ExistingNewsTag>))]
    [SwaggerOperation(Summary = "Every news tag with its direct feed and article link counts.")]
    public async Task<IActionResult> List(CancellationToken cancellationToken = default) =>
        Ok(await tags.ListAsync(cancellationToken));

    [HttpGet("{id:guid}", Name = "GetNewsTag")]
    [Authorize(Policy = PermissionClaims.NewsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingNewsTag))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "One news tag.")]
    public async Task<IActionResult> Get([FromRoute] Guid id, CancellationToken cancellationToken = default) =>
        await tags.GetAsync(id, cancellationToken) is { } tag ? Ok(tag) : NotFound(id);

    [HttpPost]
    [Authorize(Policy = PermissionClaims.NewsTagsCreate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingNewsTag))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Create a news tag.")]
    public async Task<IActionResult> Create([FromBody] NewNewsTag request, CancellationToken cancellationToken = default)
    {
        var created = await tags.CreateAsync(request, cancellationToken);
        return CreatedAtRoute("GetNewsTag", new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = PermissionClaims.NewsTagsUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingNewsTag))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Replace a news tag's name, description, icon and colour.")]
    public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] UpdateNewsTag request, CancellationToken cancellationToken = default) =>
        await tags.UpdateAsync(id, request, cancellationToken) is { } tag ? Ok(tag) : NotFound(id);

    [HttpPost("{id:guid}/archive")]
    [Authorize(Policy = PermissionClaims.NewsTagsUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingNewsTag))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Archive a news tag; its links stay and it still filters.")]
    public async Task<IActionResult> Archive([FromRoute] Guid id, CancellationToken cancellationToken = default) =>
        await tags.SetArchivedAsync(id, true, cancellationToken) is { } tag ? Ok(tag) : NotFound(id);

    [HttpPost("{id:guid}/unarchive")]
    [Authorize(Policy = PermissionClaims.NewsTagsUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingNewsTag))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Unarchive a news tag.")]
    public async Task<IActionResult> Unarchive([FromRoute] Guid id, CancellationToken cancellationToken = default) =>
        await tags.SetArchivedAsync(id, false, cancellationToken) is { } tag ? Ok(tag) : NotFound(id);

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = PermissionClaims.NewsTagsDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Delete a news tag; one in use is a 409 unless detachLinks=true removes its links too.")]
    public async Task<IActionResult> Delete([FromRoute] Guid id, [FromQuery] bool detachLinks = false, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        var rights = new NewsTagDetachRights(
            FeedLinks: User.HasPermission(PermissionClaims.NewsFeedsUpdate),
            ArticleLinks: User.HasPermission(PermissionClaims.NewsUpdate));
        return await tags.DeleteAsync(id, detachLinks, rights, userId, cancellationToken) ? NoContent() : NotFound(id);
    }

    private ObjectResult NotFound(Guid id) => this.NotFoundProblem($"News tag {id} was not found.");
}
