using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Odyssey.Core.Internet.News.Podcasts;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Internet;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

/// <summary>Podcast artwork (issue #556 §5.4): the stored original or a rendered WebP, under <c>news.read</c>. Never writes.</summary>
[ApiController]
[Route("api/podcast-artwork")]
public sealed class PodcastArtworkController(PodcastEpisodeService episodes) : ControllerBase
{
    [HttpGet("{id:guid}/content")]
    [Authorize(Policy = PermissionClaims.NewsRead)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Podcast artwork: Small, Medium (default) or the stored Original.")]
    public async Task<IActionResult> Content(
        [FromRoute] Guid id,
        [FromQuery, System.ComponentModel.DataAnnotations.EnumDataType(typeof(PodcastArtworkSize))] PodcastArtworkSize size = PodcastArtworkSize.Medium,
        CancellationToken cancellationToken = default)
    {
        if (await episodes.ArtworkAsync(id, size, cancellationToken) is not { } descriptor)
        {
            return NotFound(id);
        }

        Response.Headers.CacheControl = "private, no-cache";
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers["Cross-Origin-Resource-Policy"] = "same-site";
        var etag = new EntityTagHeaderValue($"\"{descriptor.Sha256Hex}\"");
        if (Request.GetTypedHeaders().IfNoneMatch is { Count: > 0 } ifNoneMatch && ifNoneMatch.Any(t => t.Compare(etag, useStrongComparison: false)))
        {
            Response.Headers.ETag = etag.ToString();
            return StatusCode(StatusCodes.Status304NotModified);
        }

        if (await episodes.OpenArtworkAsync(id, descriptor, cancellationToken) is not { } content)
        {
            return NotFound(id);
        }

        Response.Headers.ContentDisposition = "inline";
        return File(content, descriptor.ContentType, lastModified: null, entityTag: etag, enableRangeProcessing: true);
    }

    private ObjectResult NotFound(Guid id) => this.NotFoundProblem($"Podcast artwork {id} was not found.");
}
