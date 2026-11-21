using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;
using Odyssey.Core.Internet.News.Opml;
using Odyssey.Core.Internet.News.Reading;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Internet;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

/// <summary>
/// The news feeds (issue #470 §5.1). Reads are <c>news.read</c>; the <c>management</c> block (the fetch URL and the error
/// detail) is filled only for a <c>news-feeds.update</c> holder. A feed pending deletion is a 404 everywhere. The API
/// makes no outbound request on any of these.
/// </summary>
[ApiController]
[Route("api/news-feeds")]
public sealed class NewsFeedsController(NewsFeedService feeds) : ControllerBase
{
    /// <summary>The declared content types an OPML upload may carry (issue #508 §6); empty is accepted too.</summary>
    private static readonly HashSet<string> OpmlContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/x-opml", "text/xml", "application/xml", "application/octet-stream",
    };

    private bool CanManage => User.HasPermission(PermissionClaims.NewsFeedsUpdate);

    [HttpGet]
    [Authorize(Policy = PermissionClaims.NewsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ExistingNewsFeed>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "List news feeds by state, derived health, tag and search.")]
    public async Task<IActionResult> List([FromQuery] NewsFeedsQueryParams query, CancellationToken cancellationToken = default) =>
        Ok(await feeds.ListAsync(query, CanManage, cancellationToken));

    [HttpGet("catalogue")]
    [Authorize(Policy = PermissionClaims.NewsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(List<NewsFeedCatalogueEntry>))]
    [SwaggerOperation(Summary = "Every feed not pending deletion, unpaged and with no URL.")]
    public async Task<IActionResult> Catalogue([FromQuery] NewsFeedKindFilter? kind = null, CancellationToken cancellationToken = default) =>
        Ok(await feeds.CatalogueAsync(cancellationToken, kind));

    [HttpGet("unread-counts")]
    [Authorize(Policy = PermissionClaims.NewsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(NewsUnreadCounts))]
    [SwaggerOperation(Summary = "Unread counts in total, by feed and by effective tag; archived feeds excluded.")]
    public async Task<IActionResult> UnreadCounts(CancellationToken cancellationToken = default) =>
        Ok(await feeds.UnreadCountsAsync(cancellationToken));

    // ── OPML (issue #508) ───────────────────────────────────────────────────────────────────────────────

    [HttpGet("opml")]
    [Authorize(Policy = PermissionClaims.NewsFeedsUpdate)]
    [EnableRateLimiting(ImportExportRateLimiting.ExportConcurrencyPolicy)]
    [TypeFilter(typeof(ExportConcurrencyFilter))]
    [Produces("text/x-opml")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Export every feed not pending deletion as an OPML 2.0 file, each tag a folder.")]
    public Task<IActionResult> ExportOpml([FromServices] NewsOpmlExportService export, CancellationToken cancellationToken = default) =>
        WriteOpmlAsync(export, null, cancellationToken);

    [HttpPost("opml/export")]
    [Authorize(Policy = PermissionClaims.NewsFeedsUpdate)]
    [Consumes("application/json")]
    [RequestSizeLimit(NewsOpmlLimits.MaxExportRequestBytes)]
    [EnableRateLimiting(ImportExportRateLimiting.ExportConcurrencyPolicy)]
    [TypeFilter(typeof(ExportConcurrencyFilter))]
    [Produces("text/x-opml")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Export the named feeds as an OPML 2.0 file; unknown ids and feeds pending deletion are skipped.")]
    public Task<IActionResult> ExportOpmlFiltered(
        [FromBody] NewsOpmlExportRequest request, [FromServices] NewsOpmlExportService export, CancellationToken cancellationToken = default) =>
        WriteOpmlAsync(export, request.FeedIds!, cancellationToken);

    [HttpPost("opml")]
    [Authorize(Policy = PermissionClaims.NewsFeedsCreate)]
    [Consumes("multipart/form-data")]
    [ImportSizeLimit(ImportSurface.NewsOpml)]
    [EnableRateLimiting(ImportExportRateLimiting.ImportConcurrencyPolicy)]
    [TypeFilter(typeof(NewsOpmlImportConcurrencyFilter))]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(NewsOpmlImportResult))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Subscribe to the feeds of an OPML file in one step and report each. No outbound request is made.")]
    public async Task<IActionResult> ImportOpml(
        IFormFile? file, [FromServices] NewsOpmlImportService import, CancellationToken cancellationToken = default)
    {
        if (file is null || file.Length == 0)
        {
            throw NewsErrors.OpmlFileRequired();
        }

        var extension = Path.GetExtension(file.FileName);
        if (!(string.Equals(extension, ".opml", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".xml", StringComparison.OrdinalIgnoreCase))
            || !IsAcceptedOpmlContentType(file.ContentType))
        {
            throw NewsErrors.OpmlWrongType();
        }

        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        // Tag creation is checked here, never as a second [Authorize]: without it the feeds still import (§5.2).
        var canCreateTags = User.HasPermission(PermissionClaims.NewsTagsCreate);
        await using var stream = file.OpenReadStream();
        return Ok(await import.ImportAsync(stream, canCreateTags, userId, cancellationToken));
    }

    internal static bool IsAcceptedOpmlContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return true;
        }

        return MediaTypeHeaderValue.TryParse(contentType, out var parsed)
            && parsed.MediaType.Value is { } mediaType
            && OpmlContentTypes.Contains(mediaType);
    }

    /// <summary>Loads first, so a query fault is a clean <c>503</c>; then the headers; then the body (§5.1).</summary>
    private async Task<IActionResult> WriteOpmlAsync(NewsOpmlExportService export, IReadOnlyCollection<Guid>? feedIds, CancellationToken cancellationToken)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        var document = await export.LoadAsync(feedIds, userId, cancellationToken);
        Response.ContentType = OpmlDocumentWriter.ContentType;
        var disposition = new ContentDispositionHeaderValue("attachment");
        disposition.SetHttpFileName(export.FileName());
        Response.Headers[HeaderNames.ContentDisposition] = disposition.ToString();
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Odyssey-Export-Rows"] = document.Feeds.Count.ToString(CultureInfo.InvariantCulture);
        await OpmlDocumentWriter.WriteAsync(document, Response.Body, cancellationToken);
        return new EmptyResult();
    }

    [HttpGet("{id:guid}", Name = "GetNewsFeed")]
    [Authorize(Policy = PermissionClaims.NewsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingNewsFeed))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "One news feed. Never writes.")]
    public async Task<IActionResult> Get([FromRoute] Guid id, CancellationToken cancellationToken = default) =>
        await feeds.GetAsync(id, CanManage, cancellationToken) is { } feed ? Ok(feed) : NotFound(id);

    [HttpPost]
    [Authorize(Policy = PermissionClaims.NewsFeedsCreate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingNewsFeed))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Subscribe to a feed. No outbound request is made; the worker fetches it on its next tick.")]
    public async Task<IActionResult> Create([FromBody] NewNewsFeed request, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        var created = await feeds.CreateAsync(request, CanManage, userId, cancellationToken);
        return CreatedAtRoute("GetNewsFeed", new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = PermissionClaims.NewsFeedsUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingNewsFeed))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Replace a feed's settings under its version; a url equal to the pending suggestion accepts it.")]
    public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] UpdateNewsFeed request, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        return await feeds.UpdateAsync(id, request, CanManage, userId, cancellationToken) is { } feed ? Ok(feed) : NotFound(id);
    }

    [HttpPut("{id:guid}/tags")]
    [Authorize(Policy = PermissionClaims.NewsFeedsUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingNewsFeed))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Replace a feed's tags; every article of the feed inherits them.")]
    public async Task<IActionResult> SetTags([FromRoute] Guid id, [FromBody] NewsFeedTagsUpdate request, CancellationToken cancellationToken = default) =>
        await feeds.SetTagsAsync(id, request, CanManage, cancellationToken) is { } feed ? Ok(feed) : NotFound(id);

    [HttpPost("{id:guid}/refresh")]
    [Authorize(Policy = PermissionClaims.NewsFeedsUpdate)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Ask the worker to fetch the feed soon (no sooner than 60 s after its last attempt).")]
    public async Task<IActionResult> Refresh([FromRoute] Guid id, CancellationToken cancellationToken = default) =>
        await feeds.RefreshAsync(id, cancellationToken) ? Accepted() : NotFound(id);

    [HttpPost("{id:guid}/archive")]
    [Authorize(Policy = PermissionClaims.NewsFeedsUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingNewsFeed))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Archive a feed: it stops being fetched and leaves the default lists.")]
    public Task<IActionResult> Archive([FromRoute] Guid id, CancellationToken cancellationToken = default) =>
        SetArchivedAsync(id, true, cancellationToken);

    [HttpPost("{id:guid}/unarchive")]
    [Authorize(Policy = PermissionClaims.NewsFeedsUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingNewsFeed))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Unarchive a feed.")]
    public Task<IActionResult> Unarchive([FromRoute] Guid id, CancellationToken cancellationToken = default) =>
        SetArchivedAsync(id, false, cancellationToken);

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = PermissionClaims.NewsFeedsDelete)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Delete an archived feed: hidden at once, purged by the worker in batches.")]
    public async Task<IActionResult> Delete([FromRoute] Guid id, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        return await feeds.RequestDeleteAsync(id, userId, cancellationToken) ? Accepted() : NotFound(id);
    }

    [HttpPost("{id:guid}/release-quarantine")]
    [Authorize(Policy = PermissionClaims.NewsFeedsUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingNewsFeed))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Release a quarantined feed so the worker fetches it again.")]
    public async Task<IActionResult> ReleaseQuarantine([FromRoute] Guid id, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        return await feeds.ReleaseQuarantineAsync(id, CanManage, userId, cancellationToken) is { } feed ? Ok(feed) : NotFound(id);
    }

    private async Task<IActionResult> SetArchivedAsync(Guid id, bool archived, CancellationToken cancellationToken)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        return await feeds.SetArchivedAsync(id, archived, CanManage, userId, cancellationToken) is { } feed ? Ok(feed) : NotFound(id);
    }

    private ObjectResult NotFound(Guid id) => this.NotFoundProblem($"News feed {id} was not found.");
}
