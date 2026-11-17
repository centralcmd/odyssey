using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Odyssey.Core.Internet.News.Reading;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Internet;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

/// <summary>
/// The news articles (issue #470 §5.2). Reads are <c>news.read</c>, the read-state and tag writes <c>news.update</c>.
/// Only the detail, the revisions and the image content touch files, so only they can answer
/// <c>503 news-storage-unavailable</c>; everything else is database-only. A feed pending deletion hides its articles.
/// </summary>
[ApiController]
[Route("api/news-articles")]
public sealed class NewsArticlesController(NewsArticleService articles, NewsSearchLimiter searchLimiter) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = PermissionClaims.NewsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(NewsArticlePage))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "One page of articles: feeds OR'd, tags OR'd (own or inherited), the lists AND'd; continue with nextCursor.")]
    public async Task<IActionResult> List([FromQuery] NewsArticlesQueryParams query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query.Search))
        {
            return Ok(await articles.ListAsync(query, cancellationToken));
        }

        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        using var lease = searchLimiter.TryAcquire(userId, out var retryAfter);
        if (lease is null)
        {
            Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
            return Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: ReasonPhrases.GetReasonPhrase(StatusCodes.Status429TooManyRequests),
                detail: "Too many searches right now; try again shortly.",
                extensions: new Dictionary<string, object?> { ["code"] = NewsErrorCodes.SearchLimited });
        }

        return Ok(await articles.ListAsync(query, cancellationToken));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = PermissionClaims.NewsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(NewsArticleDetail))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "One article with its body, read from its plain-text file, and its stored images.")]
    public async Task<IActionResult> Get([FromRoute] Guid id, CancellationToken cancellationToken = default) =>
        await articles.GetAsync(id, cancellationToken) is { } article ? Ok(article) : NotFound(id);

    [HttpGet("{id:guid}/revisions")]
    [Authorize(Policy = PermissionClaims.NewsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(List<NewsArticleRevisionSummary>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "An article's earlier versions, newest first, metadata only.")]
    public async Task<IActionResult> Revisions([FromRoute] Guid id, CancellationToken cancellationToken = default) =>
        await articles.RevisionsAsync(id, cancellationToken) is { } revisions ? Ok(revisions) : NotFound(id);

    [HttpGet("{id:guid}/revisions/{number:int}")]
    [Authorize(Policy = PermissionClaims.NewsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(NewsArticleRevisionDetail))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "One earlier version with its body.")]
    public async Task<IActionResult> Revision([FromRoute] Guid id, [FromRoute] int number, CancellationToken cancellationToken = default) =>
        await articles.RevisionAsync(id, number, cancellationToken) is { } revision
            ? Ok(revision)
            : this.NotFoundProblem($"Revision {number} of news article {id} was not found.");

    /// <remarks>
    /// The order is the spec's (§5.2 item 14): authorise, load the image row with its article and feed, 404 unless it is
    /// <c>Stored</c> and this article's, and only then open the file — so a 503 is answered only after every 404. A
    /// conditional request is answered from the database. Never writes.
    /// </remarks>
    [HttpGet("{id:guid}/images/{imageId:guid}/content")]
    [Authorize(Policy = PermissionClaims.NewsRead)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "A stored image of an article, with nosniff, CORP same-site, a strong ETag and range processing.")]
    public async Task<IActionResult> ImageContent([FromRoute] Guid id, [FromRoute] Guid imageId, CancellationToken cancellationToken = default)
    {
        if (await articles.ImageAsync(id, imageId, cancellationToken) is not { } descriptor)
        {
            return ImageNotFound(id, imageId);
        }

        Response.Headers.CacheControl = "private, no-cache";
        Response.Headers.XContentTypeOptions = "nosniff";
        // same-site, not same-origin: CORP compares the port too, and the dev stack's client and API differ in it.
        Response.Headers["Cross-Origin-Resource-Policy"] = "same-site";

        var etag = new EntityTagHeaderValue($"\"{descriptor.Sha256Hex}\"");
        if (Request.GetTypedHeaders().IfNoneMatch is { Count: > 0 } ifNoneMatch && ifNoneMatch.Any(t => t.Compare(etag, useStrongComparison: false)))
        {
            Response.Headers.ETag = etag.ToString();
            return StatusCode(StatusCodes.Status304NotModified);
        }

        if (await articles.OpenImageAsync(id, imageId, descriptor, cancellationToken) is not { } content)
        {
            return ImageNotFound(id, imageId);
        }

        Response.Headers.ContentDisposition = "inline";
        return File(content, descriptor.ContentType, lastModified: null, entityTag: etag, enableRangeProcessing: true);
    }

    [HttpPut("{id:guid}/read-state")]
    [Authorize(Policy = PermissionClaims.NewsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Mark one article read or unread. Idempotent.")]
    public async Task<IActionResult> SetReadState([FromRoute] Guid id, [FromBody] NewsArticleReadState request, CancellationToken cancellationToken = default) =>
        await articles.SetReadAsync(id, request.IsRead!.Value, cancellationToken) ? NoContent() : NotFound(id);

    [HttpPost("read-state")]
    [Authorize(Policy = PermissionClaims.NewsUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(MarkNewsReadResult))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Set one read state on 1–500 articles; unknown ids change nothing.")]
    public async Task<IActionResult> SetReadStateBulk([FromBody] NewsArticlesReadStateBulk request, CancellationToken cancellationToken = default) =>
        Ok(await articles.SetReadAsync(request, cancellationToken));

    [HttpPost("mark-read")]
    [Authorize(Policy = PermissionClaims.NewsUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(MarkNewsReadResult))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Mark read what the list's filters match, up to the first page's asOfUtc; repeat while hasMore.")]
    public async Task<IActionResult> MarkRead([FromBody] MarkNewsRead request, CancellationToken cancellationToken = default) =>
        Ok(await articles.MarkReadAsync(request, cancellationToken));

    [HttpPut("{id:guid}/tags")]
    [Authorize(Policy = PermissionClaims.NewsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Replace an article's own tags.")]
    public async Task<IActionResult> SetTags([FromRoute] Guid id, [FromBody] NewsArticleTagsUpdate request, CancellationToken cancellationToken = default) =>
        await articles.SetTagsAsync(id, request, cancellationToken) ? NoContent() : NotFound(id);

    [HttpPut("{id:guid}/tags/{tagId:guid}")]
    [Authorize(Policy = PermissionClaims.NewsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Add one tag to an article. Idempotent; an archived tag is a 422.")]
    public async Task<IActionResult> AddTag([FromRoute] Guid id, [FromRoute] Guid tagId, CancellationToken cancellationToken = default) =>
        await articles.AddTagAsync(id, tagId, cancellationToken) ? NoContent() : TagNotFound(id, tagId);

    [HttpDelete("{id:guid}/tags/{tagId:guid}")]
    [Authorize(Policy = PermissionClaims.NewsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Remove one tag from an article. Idempotent.")]
    public async Task<IActionResult> RemoveTag([FromRoute] Guid id, [FromRoute] Guid tagId, CancellationToken cancellationToken = default) =>
        await articles.RemoveTagAsync(id, tagId, cancellationToken) ? NoContent() : TagNotFound(id, tagId);

    private ObjectResult NotFound(Guid id) => this.NotFoundProblem($"News article {id} was not found.");

    private ObjectResult ImageNotFound(Guid id, Guid imageId) => this.NotFoundProblem($"Image {imageId} of news article {id} was not found.");

    private ObjectResult TagNotFound(Guid id, Guid tagId) => this.NotFoundProblem($"News article {id} or news tag {tagId} was not found.");
}
